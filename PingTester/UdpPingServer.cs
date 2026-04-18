using PingTester;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public class UdpPingServer
{
    private UdpClient _udpClient;
    private Thread _thread;
    private bool _running;
    private IPAddress _confirmedLocalIP;  // ← 【修正】STUN成功時に確定したローカルIP

    // Ping レスポンスパケットを ServerLoop から Ping() へ渡すためのキューとシグナル
    private readonly ConcurrentQueue<byte[]> _pingResponseQueue = new ConcurrentQueue<byte[]>();
    private readonly SemaphoreSlim _pingResponseSignal = new SemaphoreSlim(0);

    // STUN トランザクションID → TCS のマップ（ServerLoop から GetExternalEndPointAsync へ応答を渡す）
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _stunPendingMap
        = new ConcurrentDictionary<string, TaskCompletionSource<byte[]>>();

    // パケット種別識別バイト（先頭 1 バイト）
    private const byte PacketRequest = 0x01;
    private const byte PacketResponse = 0x02;
    // Punch() が送るダミーパケット（0x00）は無視する

    // パケットサイズ: 種別(1) + 送信時刻 Ticks(8) + パディング(8) = 17 バイト
    private const int PayloadSize = 17;

    public void Start(int port)
    {
        try
        {
            // 【修正】0.0.0.0 にバインド → OS自動判定で外部接続用NIC選択
            _udpClient = new UdpClient(port);
            _running = true;
            _thread = new Thread(ServerLoop) { IsBackground = true };
            _thread.Start();
            MainProcess.WriteLog($"UDPエコーサーバ起動: ポート {port}");
        }
        catch (Exception ex)
        {
            MainProcess.WriteLog($"UDPエコーサーバ起動失敗: {ex.Message}");
            throw;
        }
    }

    private void ServerLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remoteEP);
                if (data.Length == 0) continue;

                // 【追加】全受信パケット記録
                MainProcess.WriteLog($"受信: {remoteEP.Address}:{remoteEP.Port} - {data.Length}bytes type={data[0]:X2}");

                // STUN パケットは最優先で判定（マジッククッキーで識別）
                // STUN Binding Response の先頭バイトは 0x01 であり
                // PacketRequest と衝突するため switch より前に処理する
                if (IsStunPacket(data))
                {
                    MainProcess.WriteLog("STUN応答受信 確認");
                    string key = ToTransactionKey(data, 8);
                    if (_stunPendingMap.TryGetValue(key, out var tcs))
                        tcs.TrySetResult(data);
                    continue;
                }

                switch (data[0])
                {
                    case PacketRequest:
                        // [2026-04-19 追加] Pingリクエスト受信ログ（疎通確認用）
                        MainProcess.WriteLog($"Pingリクエスト受信: {remoteEP.Address}:{remoteEP.Port}");
                        // Ping リクエスト: 種別バイトを Response に書き換えてそのままエコー返す
                        data[0] = PacketResponse;
                        try
                        {
                            int sent = _udpClient.Send(data, data.Length, remoteEP);
                            // [2026-04-19 追加] エコー送信ログ（疎通確認用）
                            MainProcess.WriteLog($"Pingエコー送信: {remoteEP.Address}:{remoteEP.Port} {sent}bytes");
                        }
                        catch (Exception ex)
                        {
                            MainProcess.WriteLog($"Pingエコー送信失敗: {remoteEP.Address}:{remoteEP.Port} - {ex.Message}");
                        }
                        break;

                    case PacketResponse:
                        // Ping レスポンス: Ping() の待機スレッドに渡す
                        // [2026-04-19 追加] Pingレスポンス受信ログ（疎通確認用）
                        MainProcess.WriteLog($"Pingレスポンス受信: {remoteEP.Address}:{remoteEP.Port}");
                        _pingResponseQueue.Enqueue(data);
                        _pingResponseSignal.Release();
                        break;

                    default:
                        // [2026-04-19 追加] 不明パケット受信ログ（疎通確認用）
                        MainProcess.WriteLog($"不明パケット受信: {remoteEP.Address}:{remoteEP.Port} type={data[0]:X2}");
                        break;
                }
            }
            catch (SocketException ex) when (!_running)
            {
                Debug.WriteLine($"Stop()による終了:{ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                MainProcess.WriteLog("UDPサーバエラー: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _udpClient?.Close();
        _thread?.Join(1000);
        MainProcess.WriteLog("UDPエコーサーバ停止");
    }

    /// <summary>
    /// STUN成功時に確定したローカルIPアドレス
    /// 複数NIC環境で外部接続に使用されたIPが確定したもの
    /// </summary>
    public IPAddress GetConfirmedLocalIP()
    {
        return _confirmedLocalIP;
    }

    /// <summary>
    /// サーバソケット経由で STUN を実行し外部エンドポイントを取得する。
    /// 複数サーバに問い合わせて外部ポートの一致を確認し NAT タイプを判定する。
    /// 全サーバ失敗時は null を返す。
    /// </summary>
    public async Task<IPEndPoint> GetExternalEndPointAsync(int timeoutMs = 3000)
    {
        MainProcess.WriteLog($"【STUN開始】 タイムアウト={timeoutMs}ms, ローカルEP={((IPEndPoint)_udpClient.Client.LocalEndPoint)}");

        // [2026-04-19 修正] 複数サーバに問い合わせて外部ポートの変化を確認（Symmetric NAT 判定）
        var results = new List<IPEndPoint>();

        foreach (var (host, port) in StunClient.StunServers)
        {
            try
            {
                MainProcess.WriteLog($"  [STUN] {host}:{port} に接続試行...");

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
                if (addresses.Length == 0)
                {
                    MainProcess.WriteLog($"  [STUN] ⚠️  DNS解決失敗: {host}");
                    continue;
                }
                MainProcess.WriteLog($"  [STUN] DNS解決成功: {host} → {addresses[0]}");

                byte[] transactionId = new byte[12];
                new Random().NextBytes(transactionId);
                string key = ToTransactionKey(transactionId, 0);

                var tcs = new TaskCompletionSource<byte[]>();
                _stunPendingMap[key] = tcs;
                try
                {
                    byte[] request = StunClient.BuildBindingRequest(transactionId);
                    IPEndPoint stunEP = new IPEndPoint(addresses[0], port);

                    MainProcess.WriteLog($"  [STUN] リクエスト送信: {request.Length}bytes → {stunEP}");
                    _udpClient.Send(request, request.Length, stunEP);

                    if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task)
                    {
                        IPEndPoint result = StunClient.ParseBindingResponse(tcs.Task.Result, transactionId);
                        if (result != null)
                        {
                            MainProcess.WriteLog($"✅ [STUN成功] {host}:{port} → 外部EP={result}");
                            // [2026-04-19 追加] 各サーバの結果を記録してポート変化を確認
                            MainProcess.WriteLog($"  [NATチェック] {host}:{port} → 外部EP={result}");
                            results.Add(result);
                        }
                        else
                        {
                            MainProcess.WriteLog($"  [STUN] ⚠️  応答パース失敗: {tcs.Task.Result.Length}bytes");
                        }
                    }
                    else
                    {
                        MainProcess.WriteLog($"  [STUN] ❌ タイムアウト({timeoutMs}ms): {host}:{port}");
                    }
                }
                finally
                {
                    _stunPendingMap.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                MainProcess.WriteLog($"  [STUN] ❌ 例外: {host}:{port} - {ex.GetType().Name}: {ex.Message}");
            }

            // [2026-04-19 追加] サーバ間で少し間隔を空ける
            await Task.Delay(500);
        }

        if (results.Count == 0) return null;

        // [2026-04-19 追加] 外部ポートが全サーバで一致するか確認してNATタイプを判定
        bool isSymmetric = results.Select(r => r.Port).Distinct().Count() > 1;
        if (isSymmetric)
        {
            MainProcess.WriteLog("⚠️ [NAT判定] Symmetric NAT を検出。ホールパンチングは機能しません。");
            MainProcess.WriteLog($"  検出ポート一覧: {string.Join(", ", results.Select(r => r.Port))}");
        }
        else
        {
            MainProcess.WriteLog($"✅ [NAT判定] Cone NAT (ポート={results[0].Port} 一致)");
        }

        // [2026-04-19 追加] 確定したローカルIPをログ出力
        IPAddress actualLocalIP = GetActualLocalIPAddress();
        if (actualLocalIP != null)
            MainProcess.WriteLog($"✅ [確定ローカルIP] {actualLocalIP}");

        //最初に成功した結果を返す
        return results[0];
    }

    /// <summary>
    /// サーバソケット（設定ポート）を使って UDP Ping を n 回送信し Min/Max/Average を返す。
    /// ソケットを共有することで NAT に開けた穴を正しく使用できる。
    /// 戻り値: (min_ms, max_ms, avg_ms)、全タイムアウト時は (-1, -1, -1)
    /// </summary>
    public (double min, double max, double avg) Ping(
        string targetIP, int targetPort, int count, int timeoutMs = 3000)
    {
        if (_udpClient == null || !_running)
        {
            MainProcess.WriteLog("Pingエラー: UdpClient が null または 未実行");
            return (-1, -1, -1);
        }

        MainProcess.WriteLog($"Ping開始: ローカルエンドポイント={((IPEndPoint)_udpClient.Client.LocalEndPoint)}");

        // 前回の Ping 残りレスポンスを破棄してシグナルをリセット
        while (_pingResponseQueue.TryDequeue(out _)) { }
        while (_pingResponseSignal.CurrentCount > 0) _pingResponseSignal.Wait(0);

        List<double> rtts = new List<double>();
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse(targetIP), targetPort);

        for (int i = 0; i < count; i++)
        {
            // リクエストパケット生成: 種別(1) + 送信時刻 Ticks(8) + パディング(8)
            byte[] payload = new byte[PayloadSize];
            payload[0] = PacketRequest;
            Buffer.BlockCopy(BitConverter.GetBytes(DateTime.UtcNow.Ticks), 0, payload, 1, 8);

            // 【追加】送信前の確認
            MainProcess.WriteLog($"Ping送信[{i + 1}/{count}]: {remoteEP.Address}:{remoteEP.Port}");

            // サーバソケット経由で送信 — NAT の穴を通る
            try
            {
                int sent = _udpClient.Send(payload, payload.Length, remoteEP);
                MainProcess.WriteLog($"  送信完了: {sent}bytes");
            }
            catch (Exception ex)
            {
                MainProcess.WriteLog($"  送信エラー: {ex.Message}");
                continue;
            }

            if (_pingResponseSignal.Wait(timeoutMs))
            {
                if (_pingResponseQueue.TryDequeue(out byte[] response) && response.Length >= 9)
                {
                    long sentTicks = BitConverter.ToInt64(response, 1);
                    double rtt = (DateTime.UtcNow.Ticks - sentTicks) / (double)TimeSpan.TicksPerMillisecond;
                    rtts.Add(rtt);
                }
            }
            else
            {
                MainProcess.WriteLog($"UDP Pingタイムアウト: {targetIP}:{targetPort}");
            }

            Thread.Sleep(100);
        }

        if (rtts.Count == 0) return (-1, -1, -1);
        return (rtts.Min(), rtts.Max(), rtts.Average());
    }

    /// <summary>
    /// サーバソケットから相手 IP:Port へダミーパケットを送信して NAT に経路を開ける。
    /// </summary>
    public void Punch(string targetIP, int targetPort, int count = 3)
    {
        if (_udpClient == null) return;
        try
        {
            IPEndPoint target = new IPEndPoint(IPAddress.Parse(targetIP), targetPort);
            byte[] dummy = new byte[] { 0x00 };
            for (int i = 0; i < count; i++)
            {
                _udpClient.Send(dummy, dummy.Length, target);
                Thread.Sleep(100);
            }
            MainProcess.WriteLog($"ホールパンチング送信: {targetIP}:{targetPort} x{count}");
        }
        catch (Exception ex)
        {
            MainProcess.WriteLog("ホールパンチング送信失敗: " + ex.Message);
        }
    }

    /// <summary>
    /// 外部への通信に実際に使用されるローカルIPアドレスを取得
    /// （0.0.0.0ではなく実際のIPを返す）
    /// </summary>
    private static IPAddress GetActualLocalIPAddress()
    {
        try
        {
            // 一時的なUDPソケットでGoogle DNSへの仮想経路を確認
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                // 実際には接続しない（UDPなので接続不要）
                // ルーティングテーブルから送信元IPを判定するだけ
                socket.Connect("8.8.8.8", 53);
                IPEndPoint localEP = (IPEndPoint)socket.LocalEndPoint;
                IPAddress actualIP = localEP.Address;

                MainProcess.WriteLog($"  [実ローカルIP判定] {actualIP}");
                return actualIP;
            }
        }
        catch (Exception ex)
        {
            MainProcess.WriteLog($"  [実ローカルIP判定失敗] {ex.Message}");
            return null;
        }
    }

    // STUN パケット判定（マジッククッキー 0x2112A442 の存在で識別）
    private static bool IsStunPacket(byte[] data)
        => data.Length >= 20
        && data[4] == 0x21 && data[5] == 0x12
        && data[6] == 0xA4 && data[7] == 0x42;

    // STUN パケットのトランザクションID（指定オフセットから 12 バイト）をキー文字列に変換
    private static string ToTransactionKey(byte[] data, int offset)
        => BitConverter.ToString(data, offset, 12);
}
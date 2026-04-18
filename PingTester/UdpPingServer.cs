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
        _udpClient = new UdpClient(port);
        _running = true;
        _thread = new Thread(ServerLoop) { IsBackground = true };
        _thread.Start();
        MainProcess.WriteLog($"UDPエコーサーバ起動: ポート {port}");
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

                // STUN パケットは最優先で判定（マジッククッキーで識別）
                // STUN Binding Response の先頭バイトは 0x01 であり
                // PacketRequest と衝突するため switch より前に処理する
                if (IsStunPacket(data))
                {
                    string key = ToTransactionKey(data, 8);
                    if (_stunPendingMap.TryGetValue(key, out var tcs))
                        tcs.TrySetResult(data);
                    continue;
                }

                switch (data[0])
                {
                    case PacketRequest:
                        // Ping リクエスト: 種別バイトを Response に書き換えてそのままエコー返す
                        data[0] = PacketResponse;
                        _udpClient.Send(data, data.Length, remoteEP);
                        break;

                    case PacketResponse:
                        // Ping レスポンス: Ping() の待機スレッドに渡す
                        _pingResponseQueue.Enqueue(data);
                        _pingResponseSignal.Release();
                        break;

                    default:
                        // 0x00(Punch ダミー)など、その他は無視
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
    /// サーバソケット経由で STUN を実行し外部エンドポイントを取得する。
    /// 同一ソケットを使うことで NAT マッピングが Ping 通信と完全に一致する。
    /// 全サーバ失敗時は null を返す。
    /// </summary>
    public async Task<IPEndPoint> GetExternalEndPointAsync(int timeoutMs = 3000)
    {
        foreach (var (host, port) in StunClient.StunServers)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
                if (addresses.Length == 0) continue;

                byte[] transactionId = new byte[12];
                new Random().NextBytes(transactionId);
                string key = ToTransactionKey(transactionId, 0);

                var tcs = new TaskCompletionSource<byte[]>();
                _stunPendingMap[key] = tcs;
                try
                {
                    byte[] request = StunClient.BuildBindingRequest(transactionId);
                    IPEndPoint stunEP = new IPEndPoint(addresses[0], port);
                    _udpClient.Send(request, request.Length, stunEP);

                    if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task)
                    {
                        IPEndPoint result = StunClient.ParseBindingResponse(tcs.Task.Result, transactionId);
                        if (result != null) return result;
                    }
                }
                finally
                {
                    _stunPendingMap.TryRemove(key, out _);
                }
            }
            catch { /* 次のサーバへ */ }
        }
        return null;
    }

    /// <summary>
    /// サーバソケット（設定ポート）を使って UDP Ping を n 回送信し Min/Max/Average を返す。
    /// ソケットを共有することで NAT に開けた穴を正しく使用できる。
    /// 戻り値: (min_ms, max_ms, avg_ms)、全タイムアウト時は (-1, -1, -1)
    /// </summary>
    public (double min, double max, double avg) Ping(
        string targetIP, int targetPort, int count, int timeoutMs = 3000)
    {
        if (_udpClient == null || !_running) return (-1, -1, -1);

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

            // サーバソケット経由で送信 — NAT の穴を通る
            _udpClient.Send(payload, payload.Length, remoteEP);

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

    // STUN パケット判定（マジッククッキー 0x2112A442 の存在で識別）
    private static bool IsStunPacket(byte[] data)
        => data.Length >= 20
        && data[4] == 0x21 && data[5] == 0x12
        && data[6] == 0xA4 && data[7] == 0x42;

    // STUN パケットのトランザクションID（指定オフセットから 12 バイト）をキー文字列に変換
    private static string ToTransactionKey(byte[] data, int offset)
        => BitConverter.ToString(data, offset, 12);
}
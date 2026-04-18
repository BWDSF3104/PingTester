using PingTester;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class UdpPingServer
{
    private UdpClient _udpClient;
    private Thread _thread;
    private bool _running;

    // Ping レスポンスパケットを ServerLoop から Ping() へ渡すためのキューとシグナル
    private readonly ConcurrentQueue<byte[]> _pingResponseQueue = new ConcurrentQueue<byte[]>();
    private readonly SemaphoreSlim _pingResponseSignal = new SemaphoreSlim(0);

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

                switch (data[0])
                {
                    case PacketRequest:
                        // Ping リクエスト: 種別バイトを Response に書き換えてそのままエコー返す
                        // タイムスタンプ(byte[1-8])は変更不要 — 送信側が RTT 計算に使う
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
}
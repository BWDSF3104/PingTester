using PingTester;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Linq;

// [2026-04-18 廃止] UdpPingServer.Ping() に移行。別ソケット使用のため NAT ホールパンチングが機能しなかった
[Obsolete("UdpPingServer.Ping() を使用してください。サーバソケット共有により NAT 経路が正しく機能します。")]
public class UdpPingClient
{
    /// <summary>
    /// UDP Pingを n回送信し、Min/Max/Averageを返す。
    /// 戻り値: (min_ms, max_ms, avg_ms)、タイムアウト時は (-1, -1, -1)
    /// </summary>
    public static (double min, double max, double avg) Ping(
        string targetIP, int port, int count, int timeoutMs = 3000)
    {
        List<double> rtts = new List<double>();
        byte[] payload = new byte[32]; // ダミーペイロード
        new Random().NextBytes(payload);

        using (UdpClient client = new UdpClient())
        {
            client.Client.ReceiveTimeout = timeoutMs;
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse(targetIP), port);

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    client.Send(payload, payload.Length, remoteEP);

                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    client.Receive(ref ep);
                    sw.Stop();

                    rtts.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch (SocketException)
                {
                    // タイムアウトはスキップ（ロスとして扱う）
                    MainProcess.WriteLog($"UDP Pingタイムアウト: {targetIP}:{port}");
                }

                Thread.Sleep(100); // 送信間隔
            }
        }

        if (rtts.Count == 0) return (-1, -1, -1);

        return (rtts.Min(), rtts.Max(), rtts.Average());
    }
}
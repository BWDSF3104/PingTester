using PingTester;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class UdpPingServer
{
    private UdpClient _udpClient;
    private Thread _thread;
    private bool _running;

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
                // 受け取ったデータをそのままエコー返す
                _udpClient.Send(data, data.Length, remoteEP);
            }
            catch (SocketException ex) when (!_running)
            {
                Debug.WriteLine($"Stop()による終了:{ex.Message}");
                break; // Stop()による終了
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
    /// サーバのソケット（設定ポート）から相手 IP:Port に向けてダミーパケットを送信し
    /// 自分側の NAT に経路を開ける。相手側も同様に実行することで双方向通信が確立する。
    /// </summary>
    /// <param name="targetIP">相手の外部IPアドレス</param>
    /// <param name="targetPort">相手の外部ポート番号</param>
    /// <param name="count">送信回数</param>
    // [2026-04-18 追加] UDPホールパンチング用ダミーパケット送信
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
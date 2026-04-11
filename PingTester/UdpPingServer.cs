using PingTester;
using System;
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
}
using PusherClient;
using System;
using System.Net;
using System.Threading.Tasks;

public class SignalingService
{
    private Pusher _pusher;
    private Channel _channel;
    private const string AppKey = "9b49d7f5df2a97bcc01e";
    private const string Cluster = "ap3";

    /// <summary>
    /// 
    /// </summary>
    /// <param name="roomId">GUIDやランダムな数字で生成し、それを相手に共有する形にすれば、他人の通信と混ざることはありません。</param>
    /// <returns></returns>
    public async Task Start(string roomId)
    {
        // .NET 4.8でTLS 1.2を強制（これがないと接続エラーになる場合があります）
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        // Pusherの初期化
        _pusher = new Pusher(AppKey, new PusherOptions
        {
            Cluster = Cluster,
            Encrypted = true
        });

        // 接続
        await _pusher.ConnectAsync();

        // チャンネル購読 (クライアントイベント送信には private- 接頭辞が必要)
        // 本来は認証が必要ですが、テスト用なら全許可のAuth設定にするか、
        // 単純なシグナリングなら subscribe だけで受信は可能です。
        _channel = await _pusher.SubscribeAsync($"private-ping-tool-{roomId}");

        // 相手からポート情報が届いた時のイベント
        _channel.Bind("client-udp-info", (dynamic data) =>
        {
            string remotePortInfo = data.port_info;
            Console.WriteLine($"相手のポート情報を受信: {remotePortInfo}");
            // ここでP2P接続処理を開始
        });

        Console.WriteLine("シグナリング待機中...");
    }

    public async Task SendPortInfo(string myPortInfo)
    {
        // 相手に自分のポート情報を送信
        // イベント名は必ず "client-" で始まる必要があります
        await _channel.TriggerAsync("client-udp-info", new { port_info = myPortInfo });
        Console.WriteLine("自分のポート情報を送信しました。");
    }
}
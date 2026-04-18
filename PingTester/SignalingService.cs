using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using PingTester;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// MQTT (HiveMQ Public Broker) を使って UDP ポート情報を交換するシグナリングサービス。
/// トピック構造:
///   購読: pingtester/room/{roomId}/+/info  （ワイルドカードで全参加者を受信）
///   送信: pingtester/room/{roomId}/{senderId}/info  （自分専用トピックに retain 送信）
/// retain=true により、後から参加したユーザーも既存メンバーの情報を即取得できる。
/// </summary>
public class SignalingService
{
    // HiveMQ パブリックブローカー (TLS 8883)
    private const string BrokerHost = "broker.hivemq.com";
    private const int BrokerPort = 8883;
    private const string TopicBase = "pingtester/room";

    private IMqttClient _mqttClient;
    private string _roomId;
    private string _senderId;
    private string _myName;

    /// <summary>相手からポート情報を受信したときに発火するイベント。引数は (名前, "IP:Port" 文字列)。</summary>
    public event Action<string, string> OnPortInfoReceived;

    /// <summary>
    /// MQTT ブローカーに接続し、指定ルームのトピックをワイルドカード購読する。
    /// </summary>
    /// <param name="roomId">ルームID。全員共通の固定文字列を想定。</param>
    /// <param name="myName">自分の表示名。受信側の IPAndNames に反映される。</param>
    public async Task Start(string roomId, string myName)
    {
        _roomId = roomId;
        _myName = myName;
        // 送信者識別用に起動ごとに一意なIDを生成
        // [2026-04-18 修正] 起動ごとにランダム生成していた _senderId を MAC アドレス＋マシン名の固定値に変更
        //                   異常終了後の再起動時に同じ _senderId で retain を上書きでき
        //                   自分自身のメッセージを正しく除外できる
        _senderId = GenerateSenderId();

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(BrokerHost, BrokerPort)
            .WithTlsOptions(o => o.UseTls())
            .WithCleanSession(true)
            .WithClientId("PingTester_" + _senderId)
            .Build();

        // 受信ハンドラを登録
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        await _mqttClient.ConnectAsync(options, CancellationToken.None);

        // ルーム内の全参加者トピックをワイルドカードで購読（QoS1）
        // "+" は単一レベルのワイルドカード。例: pingtester/room/myroom/+/info
        string subscribeTopic = BuildSubscribeTopic();
        await _mqttClient.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(subscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            CancellationToken.None);

        MainProcess.WriteLog($"MQTT: 接続完了 broker={BrokerHost}:{BrokerPort} roomId={roomId} name={myName}");
    }

    /// <summary>
    /// MAC アドレスとマシン名を組み合わせた固定の送信者 ID を生成する。
    /// 再起動後も同じ値になるため、異常終了時に残った retain を次回起動時に上書きできる。
    /// </summary>
    // [2026-04-18 追加] 固定 senderId 生成
    private static string GenerateSenderId()
    {
        try
        {
            string mac = "unknown";
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                {
                    string addr = nic.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(addr) && addr != "000000000000")
                    {
                        mac = addr;
                        break;
                    }
                }
            }
            return $"{Environment.MachineName}_{mac}";
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    /// <summary>
    /// 自分の UDP ポート情報をルームに送信する。retain=true のため後着ユーザーも取得可能。
    /// </summary>
    /// <param name="myPortInfo">送信するポート情報文字列 (例: "203.0.113.5:54321")</param>
    public async Task SendPortInfo(string myPortInfo)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            MainProcess.WriteLog("MQTT: 未接続のため送信不可");
            return;
        }

        string payload = JsonConvert.SerializeObject(new SignalingMessage
        {
            Sender = _senderId,
            Name = _myName,
            PortInfo = myPortInfo
        });

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(BuildPublishTopic())
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            // retain=true: ブローカーが最新メッセージを保持し後着ユーザーに即配信する
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(message, CancellationToken.None);
        MainProcess.WriteLog($"MQTT: 送信完了 name={_myName} port_info={myPortInfo}");
    }

    /// <summary>
    /// MQTT ブローカーから切断する。アプリ終了時に呼ぶ。
    /// retain メッセージを空ペイロードで上書きしてブローカーから削除する。
    /// </summary>
    public async Task Stop()
    {
        if (_mqttClient == null || !_mqttClient.IsConnected) return;

        // retain 削除: 空ペイロード + retain=true で送ることでブローカーが保持を破棄する
        var clearMessage = new MqttApplicationMessageBuilder()
            .WithTopic(BuildPublishTopic())
            .WithPayload(Array.Empty<byte>())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(clearMessage, CancellationToken.None);
        await _mqttClient.DisconnectAsync();
        MainProcess.WriteLog("MQTT: 切断・retain クリア完了");
    }

    // ---- private ----

    // 送信トピック: 自分専用。他者と重複しないよう senderId をパスに含める
    private string BuildPublishTopic() => $"{TopicBase}/{_roomId}/{_senderId}/info";

    // 購読トピック: "+" ワイルドカードで同ルームの全参加者を一括購読
    private string BuildSubscribeTopic() => $"{TopicBase}/{_roomId}/+/info";

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var segment = e.ApplicationMessage.PayloadSegment;

            // retain クリア（空ペイロード）は無視する
            if (segment.Count == 0) return Task.CompletedTask;

            string raw = Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
            var msg = JsonConvert.DeserializeObject<SignalingMessage>(raw);

            // 自分自身が送ったメッセージは無視
            if (msg == null || msg.Sender == _senderId) return Task.CompletedTask;

            MainProcess.WriteLog($"MQTT: 受信 name={msg.Name} port_info={msg.PortInfo}");
            OnPortInfoReceived?.Invoke(msg.Name, msg.PortInfo);
        }
        catch (Exception ex)
        {
            MainProcess.WriteLog("MQTT: 受信処理エラー: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>MQTT トピックに乗せる JSON メッセージ構造。</summary>
    private class SignalingMessage
    {
        [JsonProperty("sender")]
        public string Sender { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("port_info")]
        public string PortInfo { get; set; }
    }
}
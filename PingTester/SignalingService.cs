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
/// Pusher から移行。サーバ不要・無料・認証不要で動作する。
/// トピック: pingtester/room/{roomId}/info
/// 自分の送信したメッセージは sender フィールドで識別して無視する。
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

    /// <summary>相手からポート情報を受信したときに発火するイベント。</summary>
    public event Action<string> OnPortInfoReceived;

    /// <summary>
    /// MQTT ブローカーに接続し、指定ルームのトピックを購読する。
    /// </summary>
    /// <param name="roomId">ルームID。UUID 等の衝突しにくい文字列を推奨。</param>
    public async Task Start(string roomId)
    {
        _roomId = roomId;
        // 送信者識別用に起動ごとに一意なIDを生成
        _senderId = Guid.NewGuid().ToString("N");

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

        // ルーム固有トピックを QoS1 で購読
        string topic = BuildTopic();
        await _mqttClient.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            CancellationToken.None);

        MainProcess.WriteLog($"MQTT: 接続完了 broker={BrokerHost}:{BrokerPort} roomId={roomId}");
    }

    /// <summary>
    /// 自分の UDP ポート情報をルームに送信する。
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
            PortInfo = myPortInfo
        });

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(BuildTopic())
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _mqttClient.PublishAsync(message, CancellationToken.None);
        MainProcess.WriteLog($"MQTT: 送信完了 port_info={myPortInfo}");
    }

    /// <summary>
    /// MQTT ブローカーから切断する。アプリ終了時に呼ぶ。
    /// </summary>
    public async Task Stop()
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync();
            MainProcess.WriteLog("MQTT: 切断");
        }
    }

    // ---- private ----

    private string BuildTopic() => $"{TopicBase}/{_roomId}/info";

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var segment = e.ApplicationMessage.PayloadSegment;
            string raw = Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
            var msg = JsonConvert.DeserializeObject<SignalingMessage>(raw);

            // 自分自身が送ったメッセージは無視
            if (msg == null || msg.Sender == _senderId) return Task.CompletedTask;

            MainProcess.WriteLog($"MQTT: 受信 port_info={msg.PortInfo}");
            OnPortInfoReceived?.Invoke(msg.PortInfo);
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

        [JsonProperty("port_info")]
        public string PortInfo { get; set; }
    }
}
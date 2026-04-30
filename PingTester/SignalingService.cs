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
    /// 接続失敗時は retryCount 回リトライする（間隔 retryIntervalMs ms）。
    /// </summary>
    /// <param name="roomId">ルームID。全員共通の固定文字列を想定。</param>
    /// <param name="myName">自分の表示名。受信側の IPAndNames に反映される。</param>
    /// <param name="retryCount">接続失敗時のリトライ回数（0=リトライなし）</param>
    /// <param name="retryIntervalMs">リトライ間隔(ms)</param>
    /// <param name="onStatusChanged">接続状態変化時に呼ばれるコールバック（UI 表示用）</param>
    // [2026-04-23 追加] retryCount / retryIntervalMs / onStatusChanged パラメータを追加
    public async Task Start(string roomId, string myName,
        int retryCount = 0, int retryIntervalMs = 3000,
        Action<string> onStatusChanged = null)
    {
        _roomId = roomId;
        _myName = myName;
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

        // [2026-04-23 追加] リトライループ
        int attempt = 0;
        while (true)
        {
            try
            {
                string statusMsg = attempt == 0
                    ? "MQTT: 接続中..."
                    : $"MQTT: 再試行中 ({attempt}/{retryCount})...";
                MainProcess.WriteLog(statusMsg);
                onStatusChanged?.Invoke(statusMsg);

                await _mqttClient.ConnectAsync(options, CancellationToken.None);
                break; // 接続成功
            }
            catch (Exception ex)
            {
                if (attempt >= retryCount)
                {
                    // リトライ上限到達 → 例外を上位に伝播
                    string failMsg = $"MQTT: 接続失敗（{retryCount + 1}回試行）";
                    MainProcess.WriteLog(failMsg + " : " + ex.Message);
                    onStatusChanged?.Invoke(failMsg);
                    throw;
                }
                attempt++;
                string retryMsg = $"MQTT: 接続失敗。{retryIntervalMs / 1000}秒後に再試行 ({attempt}/{retryCount})...";
                MainProcess.WriteLog(retryMsg + " : " + ex.Message);
                onStatusChanged?.Invoke(retryMsg);
                await Task.Delay(retryIntervalMs);
            }
        }

        // ルーム内の全参加者トピックをワイルドカードで購読（QoS1）
        string subscribeTopic = BuildSubscribeTopic();
        await _mqttClient.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(subscribeTopic, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            CancellationToken.None);

        string connectedMsg = $"MQTT: 接続完了 broker={BrokerHost}:{BrokerPort} roomId={roomId} name={myName}";
        MainProcess.WriteLog(connectedMsg);
        onStatusChanged?.Invoke("MQTT: 接続済み");
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
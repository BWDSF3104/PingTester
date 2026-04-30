using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace PingTester
{
    /// <summary>
    /// ルーム内の retain メッセージを一覧表示し、個別/一括削除を行うウィンドウ。
    /// 既存の SignalingService には影響を与えず、専用の MQTT クライアントを使用する。
    /// </summary>
    public partial class RetainedMessageWindow : Window
    {
        private const string BrokerHost = "broker.hivemq.com";
        private const int BrokerPort = 8883;

        private readonly string _roomId;
        private IMqttClient _mqttClient;
        private readonly ObservableCollection<RetainedEntry> _entries
            = new ObservableCollection<RetainedEntry>();

        public RetainedMessageWindow(string roomId)
        {
            InitializeComponent();
            _roomId = roomId;
            roomIdLabel.Text = roomId;
            retainListView.ItemsSource = _entries;
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await ConnectAndLoadAsync();
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            await ConnectAndLoadAsync();
        }

        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (retainListView.SelectedItems.Count == 0)
            {
                MessageBox.Show("削除するエントリを選択してください。", "確認",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = new List<RetainedEntry>();
            foreach (RetainedEntry item in retainListView.SelectedItems)
                targets.Add(item);

            await DeleteEntriesAsync(targets);
        }

        private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_entries.Count == 0) return;

            if (MessageBox.Show($"{_entries.Count} 件の retain メッセージを全削除しますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            await DeleteEntriesAsync(new List<RetainedEntry>(_entries));
        }

        /// <summary>
        /// MQTT に接続し retain メッセージを購読・収集して一覧に表示する。
        /// 既に接続済みの場合は再購読して一覧を更新する。
        /// </summary>
        private async Task ConnectAndLoadAsync()
        {
            SetStatus("接続中...");
            SetButtonsEnabled(false);
            _entries.Clear();

            try
            {
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    _mqttClient?.Dispose();
                    var factory = new MqttFactory();
                    _mqttClient = factory.CreateMqttClient();

                    // [SignalingService と同一設定] TLS・CleanSession・ClientId
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(BrokerHost, BrokerPort)
                        .WithTlsOptions(o => o.UseTls())
                        .WithCleanSession(true)
                        .WithClientId("PingTester_Retain_" + Guid.NewGuid().ToString("N").Substring(0, 8))
                        .Build();

                    await _mqttClient.ConnectAsync(options, CancellationToken.None);
                }

                _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;

                string topic = $"pingtester/room/{_roomId}/+/info";

                // [SignalingService と同一設定] MqttClientSubscribeOptionsBuilder + QoS1
                await _mqttClient.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build(),
                    CancellationToken.None);

                SetStatus("受信中（2秒待機）...");
                await Task.Delay(2000);

                _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceived;

                await _mqttClient.UnsubscribeAsync(
                    new MqttClientUnsubscribeOptionsBuilder()
                        .WithTopicFilter(topic)
                        .Build(),
                    CancellationToken.None);

                SetStatus($"{_entries.Count} 件の retain メッセージを取得しました");
            }
            catch (Exception ex)
            {
                SetStatus("エラー: " + ex.Message);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            var segment = e.ApplicationMessage.PayloadSegment;

            // 空ペイロード（削除済み retain）は無視
            if (segment.Count == 0) return Task.CompletedTask;

            // [SignalingService と同一] PayloadSegment から UTF8 文字列に変換
            string raw = Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);

            // JSON デシリアライズして Name と PortInfo を取得
            string name = ExtractNameFromTopic(topic);
            string portInfo = raw;
            try
            {
                var msg = JsonConvert.DeserializeObject<RetainedMessageJson>(raw);
                if (msg != null)
                {
                    if (!string.IsNullOrEmpty(msg.Name)) name = msg.Name;
                    if (!string.IsNullOrEmpty(msg.PortInfo)) portInfo = msg.PortInfo;
                }
            }
            catch { /* JSON でなければ raw をそのまま表示 */ }

            Dispatcher.Invoke(() =>
            {
                // 同一トピックが既にあれば更新、なければ追加
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Topic == topic)
                    {
                        _entries[i] = new RetainedEntry
                        {
                            Name = name,
                            PortInfo = portInfo,
                            Topic = topic
                        };
                        return;
                    }
                }
                _entries.Add(new RetainedEntry
                {
                    Name = name,
                    PortInfo = portInfo,
                    Topic = topic
                });
            });

            return Task.CompletedTask;
        }

        /// <summary>
        /// 指定エントリに対して空ペイロード + retain=true を送信して削除する。
        /// [SignalingService.Stop() と同一手法]
        /// </summary>
        private async Task DeleteEntriesAsync(List<RetainedEntry> targets)
        {
            SetButtonsEnabled(false);
            SetStatus("削除中...");

            try
            {
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    SetStatus("エラー: MQTT 未接続。再読み込みしてください。");
                    return;
                }

                foreach (var entry in targets)
                {
                    // [SignalingService.Stop() と同一] 空ペイロード + retain=true で削除
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(entry.Topic)
                        .WithPayload(Array.Empty<byte>())
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(true)
                        .Build();

                    await _mqttClient.PublishAsync(message, CancellationToken.None);
                    Dispatcher.Invoke(() => _entries.Remove(entry));
                }

                SetStatus($"削除完了。残り {_entries.Count} 件");
            }
            catch (Exception ex)
            {
                SetStatus("削除エラー: " + ex.Message);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private static string ExtractNameFromTopic(string topic)
        {
            // pingtester/room/{roomId}/{senderId}/info
            string[] parts = topic.Split('/');
            return parts.Length >= 4 ? parts[3] : topic;
        }

        private void SetStatus(string message)
        {
            Dispatcher.Invoke(() => statusLabel.Text = message);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            Dispatcher.Invoke(() =>
            {
                reloadButton.IsEnabled = enabled;
                deleteSelectedButton.IsEnabled = enabled;
                deleteAllButton.IsEnabled = enabled;
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _mqttClient?.DisconnectAsync().Wait(2000);
            _mqttClient?.Dispose();
        }

        /// <summary>SignalingService の SignalingMessage と同一構造</summary>
        private class RetainedMessageJson
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("port_info")]
            public string PortInfo { get; set; }
        }
    }

    /// <summary>
    /// retain メッセージ一覧の表示用データクラス。
    /// </summary>
    public class RetainedEntry
    {
        public string Name { get; set; }
        public string PortInfo { get; set; }
        public string Topic { get; set; }
    }
}
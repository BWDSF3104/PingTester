using NetFwTypeLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PingTester
{
    public class MainProcess
    {
        static StreamWriter LogSW;

        // [2026-04-12 追加] UIログ表示用エントリ一覧。新しいログが先頭に挿入される
        public static System.Collections.ObjectModel.ObservableCollection<string> LogEntries { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();

        public static Settings ReadSettings()
        {
            Settings settings = new Settings();
            try
            {
                XElement element = XElement.Load("Settings.xml");
                // [2026-04-23 修正] Port 要素の読み込みを廃止。OS 自動割り当てに移行
                if (!int.TryParse(element.Element("Send").Value, out int send))
                {
                    send = 10;
                }
                settings.NumberOfSend = send;

                // [2026-04-18 追加] RoomId と MyName を Settings.xml から読み込む
                string roomId = element.Element("RoomId")?.Value;
                if (!string.IsNullOrEmpty(roomId)) settings.RoomId = roomId;
                WriteLog("【設定ファイル】読込RoomId:" + settings.RoomId);

                string myName = element.Element("MyName")?.Value;
                if (!string.IsNullOrEmpty(myName)) settings.MyName = myName;
                WriteLog("【設定ファイル】読込MyName:" + settings.MyName);

                // [2026-04-23 追加] MqttRetryCount を読み込み
                if (int.TryParse(element.Element("MqttRetryCount")?.Value, out int mqttRetry) && mqttRetry >= 0)
                    settings.MqttRetryCount = mqttRetry;
                WriteLog("【設定ファイル】読込MqttRetryCount:" + settings.MqttRetryCount);

                WriteLog("【設定ファイル】読込Ping送信回数:" + send);

                // [2026-04-23 修正] IPAndNames は MQTT 自動取得のため起動時は空リスト
                // Settings.xml の IP 要素は読み込まず、接続後に MQTT 経由で自動補完される
                settings.IPAndNames = new System.Collections.ObjectModel.ObservableCollection<IPAndName>();
                WriteLog("【設定ファイル】IPリストは起動時空（MQTT自動取得）");

                // [2026-04-12 修正] 未使用の GetRequest 呼び出しを削除
                Console.WriteLine("ルータ探索中...");
                WriteLog("ルータ探索中...");

                UPnPWanService napt = UPnPWanService.FindUPnPWanService();

                WriteLog("ルータ探索終了");

                IPAddress extIP = null;
                if (napt != null)
                {
                    WriteLog("ルータ情報取得成功");
                    settings.Napt = napt;
                    extIP = napt.GetExternalIPAddress();
                    settings.GIPStr = extIP?.ToString();
                    WriteLog("【ルータ情報】グローバルIP:" + extIP);
                }
                else
                {
                    WriteLog("ルータ情報取得失敗");
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message + Environment.NewLine + ex.StackTrace);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }

            return settings;
        }

        public static string ShowPortMapping(Settings settings)
        {
            try
            {
                Console.WriteLine("現在のポートマッピング情報");
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.Append("【ポートマッピング情報】:\r\n");
                foreach (var e in settings.Napt?.GetGenericPortMappingEntries())
                {
                    Console.WriteLine("[{0}] {1}:{2} -> {3}:{4} : {5}", e.Protocol, settings.Napt.GetExternalIPAddress(), e.ExternalPort, e.InternalClient, e.InternalPort, e.PortMappingDescription);
                    WriteLog(string.Format("[{0}] {1}:{2} -> {3}:{4} : {5}", e.Protocol, settings.Napt.GetExternalIPAddress(), e.ExternalPort, e.InternalClient, e.InternalPort, e.PortMappingDescription));
                    stringBuilder.Append(string.Format("[{0}] {1}:{2} -> {3}:{4} : {5}\r\n", e.Protocol, settings.Napt.GetExternalIPAddress(), e.ExternalPort, e.InternalClient, e.InternalPort, e.PortMappingDescription));
                }
                return stringBuilder.ToString();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
                return string.Empty;
            }
        }

        public static void StartWaitPing(Settings settings)
        {
            Console.WriteLine("ファイアウォール設定追加");
            FireWallSetting();

            // [2026-04-18 修正] 処理順を「STUN取得 → UdpPingServer起動 → MQTT接続・送信」に変更
            //                   旧順序では MQTT 購読開始後すぐに retain メッセージが届き
            //                   UdpPingServer 未起動のため Punch がスキップされていた
            //                   購読を UdpPingServer 起動後に行うことで Punch が確実に機能する
            Task.Run(async () =>
            {
                // [2026-04-18 修正] 処理順を「UdpPingServer起動 → サーバソケット経由STUN → MQTT接続 → MQTT送信」に変更
                //                   旧実装では一時ソケットで STUN を取得後にソケットを閉じ
                //                   UdpPingServer が別ソケットを作成するため NAT が異なる外部ポートを
                //                   割り当てる場合があり、通知した外部ポートと実際の通信ポートが不一致になっていた
                //                   サーバソケット自身で STUN を実行することで NAT マッピングを完全に一致させる

                // ① UdpPingServer を先に起動する
                //    STUN もこのソケット経由で実行するため起動を最初に行う
                // [2026-04-23 修正] ポート固定指定を廃止。0 を渡して OS に自動割り当てさせる
                settings.UdpServer = new UdpPingServer();
                settings.UdpServer.Start(0);

                // ② UdpPingServer 自身のソケット経由で STUN を実行する
                //    同一ソケットを使うことで取得した外部ポートと実際の通信ポートが必ず一致する
                string externalPortInfo = null;
                bool stunSuccess = false;
                try
                {
                    WriteLog("STUN: 外部エンドポイント取得中...");
                    IPEndPoint externalEP = await settings.UdpServer.GetExternalEndPointAsync();
                    if (externalEP != null)
                    {
                        stunSuccess = true;
                        externalPortInfo = externalEP.ToString();
                        WriteLog($"STUN: 成功 → 外部エンドポイント {externalEP}");

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            settings.GIPStr = externalEP.Address.ToString();
                            // [2026-04-23 追加] STUN 取得の外部エンドポイントを UI 表示用に保持
                            settings.ExternalEPStr = externalEP.ToString();
                        });
                    }
                    else
                    {
                        WriteLog("STUN: 全サーバ失敗。UPnP にフォールバック");
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("STUN: 例外発生。UPnP にフォールバック: " + ex.Message);
                }

                if (!stunSuccess)
                {
                    AddUdpPortMapping(settings);
                    if (!string.IsNullOrEmpty(settings.GIPStr))
                        // [2026-04-23 修正] settings.Port 廃止のため UdpServer.LocalPort を使用
                        externalPortInfo = $"{settings.GIPStr}:{settings.UdpServer.LocalPort}";
                }

                // ③ MQTT 接続・購読開始（UdpPingServer 起動後に行うことで
                //    retain メッセージ受信時に Punch が確実に実行される）
                try
                {
                    settings.SignalingService = new SignalingService();
                    settings.SignalingService.OnPortInfoReceived += (name, portInfo) =>
                        HandlePortInfoReceived(settings, name, portInfo);

                    // [2026-04-23 追加] リトライ回数を Settings から取得し、
                    //                   ステータス変化を MqttStatusText に反映する
                    await settings.SignalingService.Start(
                        settings.RoomId,
                        settings.MyName,
                        retryCount: settings.MqttRetryCount,
                        retryIntervalMs: 3000,
                        onStatusChanged: msg =>
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                settings.MqttStatusText = msg));
                }
                catch (Exception ex)
                {
                    WriteLog("MQTT: 接続失敗: " + ex.Message);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        settings.MqttStatusText = "MQTT: 接続失敗");
                }

                // ④ 外部エンドポイントが確定したら MQTT で送信
                if (externalPortInfo != null && settings.SignalingService != null)
                {
                    try
                    {
                        await settings.SignalingService.SendPortInfo(externalPortInfo);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("MQTT: ポート情報送信失敗: " + ex.Message);
                    }
                }
            });
        }

        // [2026-04-12 修正] TCP/psping サーバ起動処理を廃止。UDP (StartWaitPing) に統一。
        [Obsolete("TCP/psping は廃止。StartWaitPing を使用してください。")]
        private static bool StartWaitPingTCP(Settings settings)
        {
            WriteLog("StartWaitPingTCP: TCP/psping 廃止のため処理なし");
            return false;
        }

        // EndWaitPing 内でUDPの待機停止
        public static void EndWaitPing(Settings settings)
        {
            settings.UdpServer?.Stop();
            RemoveUdpPortMapping(settings);
            // [2026-04-18 追加] MQTT 切断・retain クリア
            settings.SignalingService?.Stop().ContinueWith(t =>
            {
                if (t.Exception != null)
                    WriteLog("MQTT: 停止時エラー: " + t.Exception.InnerException?.Message);
            });
        }

        /// <summary>
        /// [2026-04-23 追加] RoomId または MyName 変更後に MQTT を再接続する。
        /// 既存接続を切断→retain クリア→再接続→ポート情報再送信 の順に実行する。
        /// </summary>
        public static async Task ReconnectMqttAsync(Settings settings)
        {
            // 既存 MQTT 接続を切断・retain クリア
            if (settings.SignalingService != null)
            {
                try
                {
                    await settings.SignalingService.Stop();
                    WriteLog("MQTT: 再接続のため切断完了");
                }
                catch (Exception ex)
                {
                    WriteLog("MQTT: 再接続前の切断エラー: " + ex.Message);
                }
                settings.SignalingService = null;
            }

            // 再接続
            try
            {
                settings.SignalingService = new SignalingService();
                settings.SignalingService.OnPortInfoReceived += (name, portInfo) =>
                    HandlePortInfoReceived(settings, name, portInfo);

                // [2026-04-23 追加] 再接続時もリトライ設定・ステータスコールバックを適用
                await settings.SignalingService.Start(
                    settings.RoomId,
                    settings.MyName,
                    retryCount: settings.MqttRetryCount,
                    retryIntervalMs: 3000,
                    onStatusChanged: msg =>
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            settings.MqttStatusText = msg));
            }
            catch (Exception ex)
            {
                WriteLog("MQTT: 再接続失敗: " + ex.Message);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    settings.MqttStatusText = "MQTT: 接続失敗");
                return;
            }

            // 外部エンドポイントが確定済みであれば再送信
            string externalPortInfo = settings.ExternalEPStr;
            if (!string.IsNullOrEmpty(externalPortInfo))
            {
                try
                {
                    await settings.SignalingService.SendPortInfo(externalPortInfo);
                }
                catch (Exception ex)
                {
                    WriteLog("MQTT: 再接続後ポート情報送信失敗: " + ex.Message);
                }
            }
        }


        // [2026-04-12 修正] TCP/psping サーバ停止処理を廃止。UDP (EndWaitPing) に統一。
        [Obsolete("TCP/psping は廃止。EndWaitPing を使用してください。")]
        public static void EndWaitPingTCP(Settings settings)
        {
            WriteLog("EndWaitPingTCP: TCP/psping 廃止のため処理なし");
        }

        // StartSendPing 内の psping 呼び出しを置き換え
        // [2026-04-18 修正] UdpPingClient(別ソケット) → UdpPingServer.Ping()(サーバソケット共有) に変更
        //                   サーバソケットを使うことで NAT ホールパンチングで開けた穴が正しく機能する
        private static IPAndName MeasureUdpPing(IPAndName ian, Settings settings)
        {
            // [2026-04-23 修正] settings.Port 廃止のため UdpServer.LocalPort を使用
            int targetPort = ian.ExternalPort > 0 ? ian.ExternalPort : (settings.UdpServer?.LocalPort ?? 0);

            // UdpPingServer が未起動の場合はタイムアウト扱い
            var (min, max, avg) = settings.UdpServer != null
                ? settings.UdpServer.Ping(ian.IP, targetPort, settings.NumberOfSend)
                : (-1.0, -1.0, -1.0);

            IPAndName result = new IPAndName
            {
                IP = ian.IP,
                Name = ian.Name,
                ExternalPort = ian.ExternalPort,  // [2026-04-23 追加] 次回 Ping でも正しいポートを使えるよう引き継ぐ
                PrevAverage = ian.Average,
                Count = ian.Count,
                AllAverage = ian.AllAverage,
                // [2026-04-23 追加] 実際に使用した送信先ポートを Ping 結果に記録
                UsedPort = targetPort
            };

            if (min < 0) return result; // 全タイムアウト

            result.Min = min;
            result.Max = max;
            result.Average = avg;

            if (result.Count == 0)
                result.AllAverage = avg;
            else
            {
                result.AllAverage = (result.AllAverage * result.Count + avg * settings.NumberOfSend)
                                    / (result.Count + settings.NumberOfSend);
            }
            result.Count += settings.NumberOfSend;

            return result;
        }

        /// <summary>
        /// STUN失敗時にUPnPでUDPポートマッピングを追加する。
        /// 通常は使用されないが、STUNが失敗する環境での通信を可能にするためのフォールバック手段として実装。
        /// </summary>
        /// <param name="settings"></param>
        private static void AddUdpPortMapping(Settings settings)
        {
            try
            {
                IPAddress localIP = settings.Napt?.GetLocalIPAddress();
                if (settings.Napt == null || localIP == null) return;

                // [2026-04-23 修正] settings.Port 廃止のため UdpServer.LocalPort を使用
                int localPort = settings.UdpServer?.LocalPort ?? 0;
                if (localPort == 0)
                {
                    WriteLog("UDPポートマッピング: ローカルポート未確定のためスキップ");
                    return;
                }
                WriteLog($"UDPポートマッピング: ローカルIP={localIP} ポート={localPort}");

                settings.Napt.AddPortMapping(
                    null,
                    (ushort)localPort,
                    "UDP",
                    (ushort)localPort,
                    localIP,
                    true,
                    "PingTester UDP mapping",
                    3600
                );
                WriteLog($"✅ UDPポートマッピング成功: {localIP}:{localPort}");
            }
            catch (Exception ex)
            {
                WriteLog("UDPポートマッピング追加失敗: " + ex.Message);
            }
        }

        private static void RemoveUdpPortMapping(Settings settings)
        {
            try
            {
                if (settings.Napt == null) return;
                // [2026-04-23 修正] settings.Port 廃止のため UdpServer.LocalPort を使用
                int localPort = settings.UdpServer?.LocalPort ?? 0;
                if (localPort == 0) return;
                settings.Napt.DeletePortMapping(null, (ushort)localPort, "UDP");
            }
            catch (Exception ex)
            {
                WriteLog("UDPポートマッピング削除失敗: " + ex.Message);
            }
        }

        // [2026-04-12 修正] psping.exe プロセス起動・出力解析を MeasureUdpPing に置き換え
        public static void StartSendPing(Settings settings)
        {
            settings.PossibleExecute = false;
            settings.Title = "PingTester(実行中)";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int index = -1;
                var results = settings.IPAndNames.Select(ian =>
                {
                    index++;

                    // [2026-04-23 修正] settings.Port 廃止のため UdpServer.LocalPort を使用
                    int logPort = ian.ExternalPort > 0 ? ian.ExternalPort : (settings.UdpServer?.LocalPort ?? 0);
                    WriteLog(string.Format("UDP Ping実行: {0}:{1} x{2}", ian.IP, logPort, settings.NumberOfSend));
                    settings.Title = string.Format("PingTester(実行中[{0}]:{1}:{2})", ian.Name, ian.IP, logPort);

                    IPAndName resultIAN = MeasureUdpPing(ian, settings);

                    // [2026-04-12 修正] 計測成功時のみ途中経過をリストに反映
                    if (resultIAN.Average > 0)
                    {
                        UpdateIPAndNames(settings, index, resultIAN);
                    }

                    return resultIAN;
                }).ToArray();

                System.Collections.ObjectModel.ObservableCollection<IPAndName> iPAndNames =
                    new System.Collections.ObjectModel.ObservableCollection<IPAndName>(results);

                foreach (var res in results)
                {
                    string text = string.Format("Min:{0}ms Max:{1}ms Average:{2}ms", res.Min, res.Max, res.Average);
                    Console.WriteLine(text);
                    WriteLog(text);
                }

                settings.IPAndNames = iPAndNames;
                settings.Title = "PingTester(実行終了)";
                settings.PossibleExecute = true;
            }, null);
        }

        private static void FireWallRuleAdd(string name, string appPath, INetFwPolicy2 defaultFirewallPolicy)
        {
            try
            {
                INetFwPolicy2 firewallPolicy;
                if (defaultFirewallPolicy == null)
                {
                    firewallPolicy = (INetFwPolicy2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                }
                else
                {
                    firewallPolicy = defaultFirewallPolicy;
                }
                INetFwRule gotRule = null;
                INetFwRule gotRule2 = null;
                WriteLog("ファイアウォールルール検索開始:" + name);
                foreach (INetFwRule fwRule in firewallPolicy.Rules)
                {
                    if (fwRule.Name == name + "_IN")
                    {
                        WriteLog("ファイアウォールルール取得:" + fwRule.Name);
                        gotRule = fwRule;
                    }
                    else if (fwRule.Name == name + "_OUT")
                    {
                        WriteLog("ファイアウォールルール取得:" + fwRule.Name);
                        gotRule2 = fwRule;
                    }
                    if (gotRule != null && gotRule2 != null)
                    {
                        break;
                    }
                }
                //var gotRule = firewallPolicy.Rules.Item(name + "_IN");

                if (gotRule == null)
                {
                    INetFwRule firewallRule = (INetFwRule)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                    firewallRule.Action = NET_FW_ACTION_.NET_FW_ACTION_ALLOW;
                    firewallRule.Description = name + "　更新日：" + DateTime.Now;
                    firewallRule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_IN;
                    firewallRule.Enabled = true;
                    firewallRule.InterfaceTypes = "All";
                    firewallRule.ApplicationName = appPath;
                    firewallRule.Name = name + "_IN";

                    firewallPolicy.Rules.Add(firewallRule);
                    WriteLog("ファイアウォールルール追加:" + name + "_IN");
                }
                else
                {
                    gotRule.Description = name + "　更新日：" + DateTime.Now;
                    gotRule.ApplicationName = appPath;
                    WriteLog("ファイアウォールルール更新:" + name + "_IN");
                }

                //var gotRule2 = firewallPolicy.Rules.Item(name + "_OUT");

                // [2026-04-12 修正] OUTルール判定を gotRule2 に修正（gotRule の誤りを訂正）
                if (gotRule2 == null)
                {
                    INetFwRule firewallRule = (INetFwRule)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                    firewallRule.Action = NET_FW_ACTION_.NET_FW_ACTION_ALLOW;
                    firewallRule.Description = name + "　更新日：" + DateTime.Now;
                    firewallRule.Direction = NET_FW_RULE_DIRECTION_.NET_FW_RULE_DIR_OUT;
                    firewallRule.Enabled = true;
                    firewallRule.InterfaceTypes = "All";
                    firewallRule.ApplicationName = appPath;
                    firewallRule.Name = name + "_OUT";

                    firewallPolicy.Rules.Add(firewallRule);
                    WriteLog("ファイアウォールルール追加:" + name + "_OUT");
                }
                else
                {
                    gotRule2.Description = name + "　更新日：" + DateTime.Now;
                    gotRule2.ApplicationName = appPath;
                    WriteLog("ファイアウォールルール更新:" + name + "_OUT");
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
        }


        // [2026-04-12 修正] UDP Ping に移行したため psping.exe のファイアウォール設定を削除
        private static void FireWallSetting()
        {
            System.Reflection.Assembly myAssembly = System.Reflection.Assembly.GetEntryAssembly();
            string path = myAssembly.Location;
            INetFwPolicy2 firewallPolicy = (INetFwPolicy2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));

            WriteLog("PingTesterファイアウォール設定");
            FireWallRuleAdd("PingTester", path, firewallPolicy);
        }

        private static void UpdateIPAndNames(Settings settings, int index, IPAndName iPAndName)
        {
            System.Collections.ObjectModel.ObservableCollection<IPAndName> iPAndNames = new System.Collections.ObjectModel.ObservableCollection<IPAndName>(settings.IPAndNames);
            iPAndNames[index] = iPAndName;
            settings.IPAndNames = iPAndNames;
        }

        //未使用のSendMSearchおよびREQUEST_MESSAGEを削除

        public static string GetRequest(string url)
        {
            try
            {
                Encoding enc = Encoding.GetEncoding("UTF-8");

                WebRequest req = WebRequest.Create(url);
                WebResponse res = req.GetResponse();

                Stream st = res.GetResponseStream();
                StreamReader sr = new StreamReader(st, enc);
                string html = sr.ReadToEnd();
                sr.Close();
                st.Close();
                return html;
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
                return null;
            }
        }

        public static void TryMethod(Action action)
        {
            try
            {
                action.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }

        // [2026-04-12 修正] UDP Ping に移行したため psping.exe プロセス終了処理を廃止
        // EndWaitPingTCP から呼ばれていた名残。TCP/psping 廃止により何もしない。
        public static void MyProcessKill()
        {
            WriteLog("MyProcessKill: psping.exe 廃止のため処理なし");
        }

        public static void SaveSetting(Settings settings)
        {
            StreamWriter sw = null;
            try
            {
                sw = new StreamWriter(new FileStream("Settings.xml", FileMode.Create));
                sw.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
                sw.WriteLine("<Main>");
                // [2026-04-23 修正] Port・IP 要素の保存を廃止
                //   Port: OS 自動割り当てのため保存不要
                //   IP:   MQTT 自動取得のため保存不要（起動時は空リストが正常）
                sw.WriteLine(string.Format("  <Send>{0}</Send>", settings.NumberOfSend));
                // [2026-04-18 追加] RoomId と MyName を保存
                sw.WriteLine(string.Format("  <RoomId>{0}</RoomId>", settings.RoomId));
                sw.WriteLine(string.Format("  <MyName>{0}</MyName>", settings.MyName));
                // [2026-04-23 追加] MqttRetryCount を保存
                sw.WriteLine(string.Format("  <MqttRetryCount>{0}</MqttRetryCount>", settings.MqttRetryCount));
                sw.WriteLine("</Main>");
                sw.Dispose();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
        }

        // [2026-04-18 追加] MQTT 受信時に IPAndNames を自動更新するハンドラ
        // 同名エントリが既存なら ExternalPort を更新、なければ新規追加する
        private static void HandlePortInfoReceived(Settings settings, string name, string portInfo)
        {
            try
            {
                // "IP:Port" を分割してパース
                int lastColon = portInfo.LastIndexOf(':');
                if (lastColon < 0) return;

                string ip = portInfo.Substring(0, lastColon);
                string portStr = portInfo.Substring(lastColon + 1);
                if (!int.TryParse(portStr, out int port)) return;

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    // [2026-04-18 追加] 受信した IP が自分自身（外部IP）と一致する場合は登録しない
                    //                   異常終了時の retain 残りや senderId 変更前の自己受信を防ぐ
                    if (ip == settings.GIPStr)
                    {
                        WriteLog($"MQTT: 自分自身のエントリのため無視 ip={ip}");
                        return;
                    }

                    var existing = null as IPAndName;
                    foreach (var ian in settings.IPAndNames)
                    {
                        if (ian.Name == name || ian.IP == ip)
                        {
                            existing = ian;
                            break;
                        }
                    }

                    if (existing != null)
                    {
                        existing.IP = ip;
                        existing.ExternalPort = port;
                        WriteLog($"MQTT: エントリ更新 name={name} ip={ip} port={port}");
                    }
                    else
                    {
                        settings.AddIPAndName(new IPAndName
                        {
                            Name = name,
                            IP = ip,
                            ExternalPort = port
                        });
                        WriteLog($"MQTT: エントリ追加 name={name} ip={ip} port={port}");
                    }

                    // ホールパンチングを実行
                    settings.UdpServer?.Punch(ip, port);
                });
            }
            catch (Exception ex)
            {
                WriteLog("MQTT: ポート情報処理エラー: " + ex.Message);
            }
        }

        public static void StartLog()
        {
            try
            {
                LogSW = new StreamWriter("Log.txt", true, Encoding.GetEncoding("shift_jis"));
                LogSW.WriteLine(string.Format("______{0}______", DateTime.Now));
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
        }

        public static void EndLog()
        {
            LogSW.Dispose();
        }

        public static void WriteLog(string text)
        {
            // [2026-04-12 修正] ファイルログに加え、UIログ表示領域にも追記
            lock (LogSW)
            {
                LogSW?.WriteLine(text);
            }
            string entry = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, text);
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // 新しいログを先頭に挿入し、最大500件で古いものを削除
                LogEntries.Insert(0, entry);
                if (LogEntries.Count > 500)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
            });
        }

        private static bool nowScan = false;
        private static string prevPorts;
        public static bool ScanPortMapping(Settings settings)
        {
            if (nowScan)
            {
                nowScan = false;
                return true;
            }
            else
            {
                nowScan = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        FileStream stream = new FileStream("ScanLog.txt", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                        StreamWriter sw = new StreamWriter(stream);
                        sw.WriteLine(DateTime.Now);
                        while (nowScan)
                        {
                            string message = ShowPortMapping(settings);
                            if (message != prevPorts)
                            {
                                sw.WriteLine(message);
                                prevPorts = message;
                            }
                            Thread.Sleep(500);
                        }
                        sw.Close();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Forms.MessageBox.Show(ex.Message);
                        MainProcess.WriteLog(ex.Message + "\n" + ex.StackTrace);
                    }
                }, null);
                return false;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, int flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint TOPMOST_FLAGS = (SWP_NOSIZE | SWP_NOMOVE);
        const uint NOTOPMOST_FLAGS = (SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOMOVE);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        const int ASYNCWINDOWPOS = 0x4000;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private extern static bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("dwmapi.dll", PreserveSig = false)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = false)]
        public static extern int DwmIsCompositionEnabled(ref int pfEnabled);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attr, out RECT lpRect, int attrSize);

        public enum DWMWINDOWATTRIBUTE : uint
        {
            NCRenderingEnabled = 1,
            NCRenderingPolicy,
            TransitionsForceDisabled,
            AllowNCpaint,
            CaptionButtonBounds,
            NonClientRtlLayout,
            ForceIconicRepresentation,
            Flip3DPolicy,
            ExtendedFrameBounds,
            HasIconBitmap,
            DisallowPeek,
            ExcludedFromPeek,
            Cloak,
            Cloaked,
            FreezeRepresentation,
            PlaceHolder1,
            PlaceHolder2,
            PlaceHolder3,
            AccentPolicy = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

    }
}

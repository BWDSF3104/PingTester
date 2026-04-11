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
using System.Xml.Linq;

namespace PingTester
{
    public class MainProcess
    {
        static StreamWriter LogSW;

        public static Settings ReadSettings()
        {
            Settings settings = new Settings();
            try
            {
                XElement element = XElement.Load("Settings.xml");
                if (!int.TryParse(element.Element("Port").Value, out int port))
                {
                    port = 12345;
                }
                settings.Port = port;
                WriteLog("読込使用ポート:" + port);

                if (!int.TryParse(element.Element("Send").Value, out int send))
                {
                    send = 10;
                }
                settings.NumberOfSend = send;
                WriteLog("読込Ping送信回数:" + send);

                string ipAndNamesStr = element.Element("IP").Value;
                System.Collections.ObjectModel.ObservableCollection<IPAndName> iPAndNames = new System.Collections.ObjectModel.ObservableCollection<IPAndName>();
                if (ipAndNamesStr != null)
                {
                    string[] ipAndNamesStrArray = ipAndNamesStr.Split(new string[] { "\t" }, StringSplitOptions.RemoveEmptyEntries);
                    var selectResult = ipAndNamesStrArray.Select(tmps =>
                     {
                         Console.WriteLine(tmps);
                         WriteLog(tmps);
                         string[] ipAndNameSplited = tmps.Split(',');
                         if (ipAndNameSplited.Length > 1)
                         {
                             IPAndName iPAndName = new IPAndName
                             {
                                 IP = ipAndNameSplited[0],
                                 Name = ipAndNameSplited[1]
                             };
                             iPAndNames.Add(iPAndName);
                             return iPAndName;
                         }
                         return null;
                     }).ToArray();
                }
                settings.IPAndNames = iPAndNames;

                // [2026-04-12 修正] 未使用の GetRequest 呼び出しを削除
                Console.WriteLine("ルータ探索中...");
                WriteLog("ルータ探索中...");

                UPnPWanService napt = UPnPWanService.FindUPnPWanService();

                WriteLog("ルータ探索終了");

                IPAddress extIP = null;
                if (napt != null)
                {
                    WriteLog("ルータ取得");
                    settings.Napt = napt;
                    extIP = napt.GetExternalIPAddress();
                    settings.GIPStr = extIP?.ToString();
                    WriteLog("グローバルIP:" + extIP);
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
                Console.WriteLine("現在のポートマッピングs");
                WriteLog("現在のポートマッピングs");
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.Append("ポートマッピング:\r\n");
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
            AddUdpPortMapping(settings); // UDPポートマッピング追加

            settings.UdpServer = new UdpPingServer();
            settings.UdpServer.Start(settings.Port);
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
        }

        // [2026-04-12 修正] TCP/psping サーバ停止処理を廃止。UDP (EndWaitPing) に統一。
        [Obsolete("TCP/psping は廃止。EndWaitPing を使用してください。")]
        public static void EndWaitPingTCP(Settings settings)
        {
            WriteLog("EndWaitPingTCP: TCP/psping 廃止のため処理なし");
        }

        // StartSendPing 内の psping 呼び出しを置き換え
        private static IPAndName MeasureUdpPing(IPAndName ian, Settings settings)
        {
            var (min, max, avg) = UdpPingClient.Ping(ian.IP, settings.Port, settings.NumberOfSend);

            IPAndName result = new IPAndName
            {
                IP = ian.IP,
                Name = ian.Name,
                PrevAverage = ian.Average,
                Count = ian.Count,
                AllAverage = ian.AllAverage
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

        private static void AddUdpPortMapping(Settings settings)
        {
            try
            {
                IPAddress localIP = settings.Napt?.GetLocalIPAddress();
                if (settings.Napt == null || localIP == null) return;

                settings.Napt.AddPortMapping(
                    null,
                    (ushort)settings.Port,
                    "UDP", // ← UDPに変更
                    (ushort)settings.Port,
                    localIP,
                    true,
                    "PingTester UDP mapping",
                    3600
                );
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

                settings.Napt.DeletePortMapping(null, (ushort)settings.Port, "UDP");
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

                    // [2026-04-12 修正] psping.exe 呼び出しを UDP Ping 計測に置き換え
                    WriteLog(string.Format("UDP Ping実行: {0}:{1} x{2}", ian.IP, settings.Port, settings.NumberOfSend));
                    settings.Title = string.Format("PingTester(実行中[{0}]:{1}:{2})", ian.Name, ian.IP, settings.Port);

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
                sw.WriteLine(string.Format("  <Port>{0}</Port>", settings.Port));
                StringBuilder ipAndNamesBuilder = new StringBuilder();
                for (int index = 0; index < settings.IPAndNames.Count; index++)
                {
                    IPAndName ian = settings.IPAndNames[index];
                    ipAndNamesBuilder.Append(string.Format("{0},{1}", ian.IP, ian.Name));
                    if (index < settings.IPAndNames.Count - 1)
                    {
                        ipAndNamesBuilder.Append("\t");
                    }
                }
                sw.WriteLine(string.Format("  <IP>{0}</IP>", ipAndNamesBuilder.ToString()));
                sw.WriteLine(string.Format("  <Send>{0}</Send>", settings.NumberOfSend));
                sw.WriteLine("</Main>");
                sw.Dispose();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
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
            lock (LogSW)
            {
                LogSW?.WriteLine(text);
            }
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

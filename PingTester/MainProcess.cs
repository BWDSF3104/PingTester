using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using NetFwTypeLib;

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

                string requestResult = GetRequest("http://192.168.2.1");

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
            try
            {
                Console.WriteLine("ポートマッピング追加");
                WriteLog("ポートマッピング追加");
                settings.Napt?.AddPortMapping(null, (ushort)settings.Port, "TCP", (ushort)settings.Port, settings.Napt?.GetLocalIPAddress(), true, "PingTester Port mapping", 0);
                ShowPortMapping(settings);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
            try
            {
                Process process = new Process()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = "psping.exe",
                        Arguments = string.Format("-f -s 0.0.0.0:{0}", settings.Port)
                    }
                };
                settings.PsPingServer = process;

                WriteLog("Ping受信待機開始処理");
                process.Start();
                WriteLog("Ping受信待機開始処理終了");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
        }

        public static void EndWaitPing(Settings settings)
        {
            try
            {
                Console.WriteLine("ポートマッピング削除");
                settings.Napt?.DeletePortMapping(null, (ushort)settings.Port, "TCP");
                ShowPortMapping(settings);

                settings.PsPingServer.CloseMainWindow();
                MyProcessKill();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                WriteLog(ex.Message + "\n" + ex.StackTrace);
            }
        }

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
                     string args = string.Format("-n {2} {0}:{1}", IPAddress.Parse(ian.IP), settings.Port, settings.NumberOfSend);
                     Console.WriteLine("PsPing実行:" + args);
                     settings.Title = string.Format("PingTester(実行中[{1}]:{0})", args, ian.Name);
                     Process process = new Process()
                     {
                         StartInfo = new ProcessStartInfo()
                         {
                             FileName = "psping.exe",
                             Arguments = args,
                             UseShellExecute = false,
                             CreateNoWindow = true,
                             RedirectStandardOutput = true
                         }
                     };
                     process.Start();

                     string tmpText = string.Empty;
                     // リダイレクトがあったときに呼ばれるイベントハンドラ
                     process.OutputDataReceived +=
                     new DataReceivedEventHandler(delegate (object obj, DataReceivedEventArgs dargs)
                     {
                         if (!string.IsNullOrEmpty(dargs.Data))
                         {
                             tmpText = dargs.Data;
                             settings.Title = string.Format("PingTester(実行中[{1}]:{0}):{2}", args, ian.Name, tmpText);
                         }
                     });

                     // 非同期ストリーム読み取りの開始
                     // (C#2.0から追加されたメソッド)
                     process.BeginOutputReadLine();

                     process.WaitForExit();
                     //string[] processOutput = process.StandardOutput.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                     //string lastResult = processOutput[processOutput.Length - 1];
                     string lastResult = tmpText;
                     Console.WriteLine("LastResult: " + lastResult);
                     IPAndName resultIAN = new IPAndName()
                     {
                         IP = ian.IP,
                         Name = ian.Name,
                         PrevAverage = ian.Average,
                         AllAverage = ian.AllAverage,
                         Count = ian.Count
                     };
                     if (lastResult == "リモート コンピューターによりネットワーク接続が拒否されました。" | lastResult == "タイムアウト期間が経過したため、この操作は終了しました。")
                     {
                         return resultIAN;
                     }
                     else
                     {
                         double[] doubles = lastResult.Split(',').Select(splited =>
                          {
                              System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(@"[^0-9\.]");
                              if (double.TryParse(regex.Replace(splited, ""), out double parsed))
                              {
                                  return parsed;
                              }
                              else
                              {
                                  return 0.0;
                              }
                          }).ToArray();

                         if (doubles.Length > 2)
                         {
                             resultIAN.Min = doubles[0];
                             resultIAN.Max = doubles[1];
                             resultIAN.Average = doubles[2];
                             if (resultIAN.Count == 0)
                             {
                                 resultIAN.AllAverage = resultIAN.Average;
                             }
                             if (resultIAN.Count > 0)
                             {
                                 double newAllAverage = (resultIAN.AllAverage * resultIAN.Count + resultIAN.Average * settings.NumberOfSend) / (resultIAN.Count + settings.NumberOfSend);
                                 Console.WriteLine("総平均値計算:" + string.Format("({0} * {1} + {2} * {3}) / ({1} + {3}))", resultIAN.AllAverage, resultIAN.Count, resultIAN.Average, settings.NumberOfSend));
                                 resultIAN.AllAverage = newAllAverage;
                             }
                             resultIAN.Count += settings.NumberOfSend;
                         }
                         UpdateIPAndNames(settings, index, resultIAN);
                         return resultIAN;
                     }
                 }).ToArray();
                System.Collections.ObjectModel.ObservableCollection<IPAndName> iPAndNames = new System.Collections.ObjectModel.ObservableCollection<IPAndName>(results);
                results.Select(res =>
                {
                    string text = string.Format("Min:{0}ms Max:{1}ms Average:{2}ms", res.Min, res.Max, res.Average);
                    Console.WriteLine(text);
                    return text;
                }).ToArray();
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

                if (gotRule == null)
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

        private static void FireWallSetting()
        {
            System.Reflection.Assembly myAssembly = System.Reflection.Assembly.GetEntryAssembly();
            string path = myAssembly.Location;
            string dirPath = AppDomain.CurrentDomain.BaseDirectory;
            string psPingPath = dirPath + "psping.exe";
            INetFwPolicy2 firewallPolicy = (INetFwPolicy2)Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));

            WriteLog("PingTesterファイアウォール設定");
            FireWallRuleAdd("PingTester", path, firewallPolicy);
            WriteLog("PsPingファイアウォール設定");
            FireWallRuleAdd("PsPing", psPingPath, firewallPolicy);
        }

        private static void UpdateIPAndNames(Settings settings, int index, IPAndName iPAndName)
        {
            System.Collections.ObjectModel.ObservableCollection<IPAndName> iPAndNames = new System.Collections.ObjectModel.ObservableCollection<IPAndName>(settings.IPAndNames);
            iPAndNames[index] = iPAndName;
            settings.IPAndNames = iPAndNames;
        }

        static readonly string REQUEST_MESSAGE = String.Concat(
            "M-SEARCH * HTTP/1.1\r\n",
            "MX: 3\r\n",
            "HOST: 239.255.255.250:1900\r\n",
            "MAN: \"ssdp: discover\"\r\n",
            "ST: service:WANIPConnection:1\r\n" // ST:例
        );

        private static void SendMSearch()
        {
            IPEndPoint LocalEndPoint = new IPEndPoint(IPAddress.Any, 1900);
            IPEndPoint MulticastEndPoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            Socket UdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            UdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            UdpSocket.Bind(LocalEndPoint);
            UdpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastEndPoint.Address, IPAddress.Any));
            UdpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            UdpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

            Console.WriteLine("UDP-Socket setup done...\r\n");

            //string SearchString = "M-SEARCH * HTTP/1.1\r\nHOST:239.255.255.250:1900\r\nMAN:\"ssdp:discover\"\r\nST:ssdp:all\r\nMX:3\r\n\r\n";

            UdpSocket.SendTo(Encoding.UTF8.GetBytes(REQUEST_MESSAGE), SocketFlags.None, MulticastEndPoint);

            Console.WriteLine("M-Search sent...\r\n");

            byte[] ReceiveBuffer = new byte[64000];

            int ReceivedBytes = 0;

            while (true)
            {
                if (UdpSocket.Available > 0)
                {
                    ReceivedBytes = UdpSocket.Receive(ReceiveBuffer, SocketFlags.None);

                    if (ReceivedBytes > 0)
                    {
                        Console.WriteLine(Encoding.UTF8.GetString(ReceiveBuffer, 0, ReceivedBytes));
                    }
                }
            }
        }

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

        public static void MyProcessKill()
        {
            Process[] myProcesses;
            TryMethod(() =>
            {
                myProcesses = Process.GetProcessesByName("psping.exe");
                if (myProcesses.Length > 0)
                {
                    foreach (Process gotProcess in myProcesses)
                    {
                        string processName = gotProcess.ProcessName;
                        Console.WriteLine("プロセス:" + processName);
                        TryMethod(() =>
                        {
                            Console.WriteLine("停止処理(CloseMainWindow()):" + processName);
                            gotProcess.CloseMainWindow();
                            gotProcess.WaitForExit(5000);
                        });
                        Console.WriteLine("停止処理(Kill()):" + processName);
                        TryMethod(() => gotProcess.Kill());

                        Console.WriteLine("停止:" + processName);
                    }
                }
                else
                {
                    Console.WriteLine("操作なし:myProcess停止済み");
                }
            });
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

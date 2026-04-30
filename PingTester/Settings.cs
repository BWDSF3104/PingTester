using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace PingTester
{
    public class Settings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // [2026-04-23 追加] STUN で取得した外部エンドポイント文字列（例: "203.0.113.5:54321"）
        // Port プロパティを廃止し OS 自動割り当てに移行。表示は STUN 取得値を使用する
        private string _ExternalEPStr;
        public string ExternalEPStr
        {
            get => _ExternalEPStr;
            set
            {
                _ExternalEPStr = value;
                RaisePropertyChanged(nameof(ExternalEPStr));
            }
        }

        public UPnPWanService Napt { set; get; }

        private string _GIPstr;
        public string GIPStr
        {
            get => _GIPstr;
            set
            {
                _GIPstr = value;
                RaisePropertyChanged(nameof(GIPStr));
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<IPAndName> _IPAndNames;
        public System.Collections.ObjectModel.ObservableCollection<IPAndName> IPAndNames
        {
            get => _IPAndNames;
            set
            {
                _IPAndNames = value;
                RaisePropertyChanged(nameof(IPAndNames));
            }
        }

        public void RemoveIPAndName(IPAndName iPAndName)
        {
            System.Collections.ObjectModel.ObservableCollection<IPAndName> tmps = new System.Collections.ObjectModel.ObservableCollection<IPAndName>(IPAndNames);
            tmps.Remove(iPAndName);
            IPAndNames = tmps;
        }

        public void AddIPAndName(IPAndName iPAndName)
        {
            System.Collections.ObjectModel.ObservableCollection<IPAndName> tmps = new System.Collections.ObjectModel.ObservableCollection<IPAndName>(IPAndNames);
            tmps.Add(iPAndName);
            IPAndNames = tmps;
        }

        private string _Title = "PingTester";
        public string Title
        {
            get => _Title;
            set
            {
                _Title = value;
                RaisePropertyChanged(nameof(Title));
            }
        }

        private int _NumberOfSend;
        public int NumberOfSend
        {
            get => _NumberOfSend;
            set
            {
                _NumberOfSend = value;
                RaisePropertyChanged(nameof(NumberOfSend));
            }
        }

        private bool _PossibleExecute = true;
        public bool PossibleExecute
        {
            get => _PossibleExecute;
            set
            {
                _PossibleExecute = value;
                RaisePropertyChanged(nameof(PossibleExecute));
            }
        }

        public UdpPingServer UdpServer { get; set; }

        // [2026-04-18 追加] MQTT シグナリングサービス
        public SignalingService SignalingService { get; set; }

        // [2026-04-23 追加] MQTT 初回接続失敗時のリトライ回数（Settings.xml で設定）
        private int _MqttRetryCount = 3;
        public int MqttRetryCount
        {
            get => _MqttRetryCount;
            set { _MqttRetryCount = value; RaisePropertyChanged(nameof(MqttRetryCount)); }
        }

        // [2026-04-23 追加] MQTT 接続状態の UI 表示用テキスト
        private string _MqttStatusText = "未接続";
        public string MqttStatusText
        {
            get => _MqttStatusText;
            set { _MqttStatusText = value; RaisePropertyChanged(nameof(MqttStatusText)); }
        }

        // [2026-04-18 追加] MQTT ルームID（全員共通の固定値。Settings.xml で管理）
        private string _RoomId = "pingtester-default-room";
        public string RoomId
        {
            get => _RoomId;
            set { _RoomId = value; RaisePropertyChanged(nameof(RoomId)); }
        }

        // [2026-04-18 追加] 自分の表示名（相手側の IPAndNames リストに表示される）
        private string _MyName = Environment.MachineName;
        public string MyName
        {
            get => _MyName;
            set { _MyName = value; RaisePropertyChanged(nameof(MyName)); }
        }
    }

    public class IPAndName
    {
        public string IP { get; set; }
        //public IPAddress IPAddress { get; set; }
        public string Name { get; set; }
        // [2026-04-12 追加] 相手の外部ポート番号（UDPホールパンチング用）
        public int ExternalPort { get; set; }
        public double Average { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
        public double PrevAverage { get; set; }
        public int Count { get; set; }
        public double AllAverage { get; set; }
        // [2026-04-23 追加] Ping 計測時に実際に使用した送信先ポート番号（結果リストに表示）
        public int UsedPort { get; set; }
    }
}

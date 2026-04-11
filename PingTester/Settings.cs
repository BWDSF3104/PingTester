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

        private int _Port;
        public int Port
        {
            get => _Port;
            set
            {
                _Port = value;
                RaisePropertyChanged(nameof(Port));
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
    }

    public class IPAndName
    {
        public string IP { get; set; }
        //public IPAddress IPAddress { get; set; }
        public string Name { get; set; }
        public double Average { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
        public double PrevAverage { get; set; }
        public int Count { get; set; }
        public double AllAverage { get; set; }
    }
}

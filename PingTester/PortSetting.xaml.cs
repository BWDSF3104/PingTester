using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PingTester
{
    /// <summary>
    /// PortSetting.xaml の相互作用ロジック
    /// </summary>
    public partial class PortSetting : Window
    {
        private int _Port;
        public int Port { get => _Port; }
        public int Min { get; set; }
        public int Max { get; set; }
        private bool _SettingFinished = false;
        public bool SettingFinished { get => _SettingFinished; }

        public PortSetting()
        {
            InitializeComponent();
            Min = 1;
            Max = 65535;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(portBox.Text, out int result) && result >= Min && result <= Max)
            {
                _Port = result;
                _SettingFinished = true;
                Close();
            }
            else
            {
                MessageBox.Show(string.Format("{0}から{1}までの整数値を入力してください", Min, Max), "入力値エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

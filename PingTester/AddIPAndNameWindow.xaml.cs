using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    /// AddIPAndNameWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class AddIPAndNameWindow : Window
    {
        private string _Name;
        public string IPName { get => _Name; }
        private string _IP;
        public string IP { get => _IP; }
        private bool _SettingFinished = false;
        public bool SettingFinished { get => _SettingFinished; }

        public AddIPAndNameWindow()
        {
            InitializeComponent();
        }

        public AddIPAndNameWindow(string name, string ip)
        {
            InitializeComponent();
            nameBox.Text = name;
            ipBox.Text = ip;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(nameBox.Text))
            {
                MessageBox.Show("名前を入力してください", "入力値エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (!IPAddress.TryParse(ipBox.Text, out IPAddress iPAddress))
            {
                MessageBox.Show("適切なIPアドレスを入力してください", "入力値エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                _Name = nameBox.Text;
                _IP = iPAddress.ToString();
                _SettingFinished = true;
                Close();
            }
        }
    }
}

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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PingTester
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private Settings settings;

        public MainWindow()
        {
            MainProcess.WriteLog("初期設定中メッセージ表示");
            MessageWindow messageWindow = new MessageWindow("初期設定中...");
            messageWindow.Dispatcher.Invoke(new Action(() => { messageWindow.Show(); }));
            InitializeComponent();
            settings = MainProcess.ReadSettings();
            this.DataContext = settings;
            MainProcess.StartWaitPing(settings);
            messageWindow.Dispatcher.Invoke(new Action(() => { messageWindow.Close(); }));
            MainProcess.WriteLog("初期設定中メッセージ表示終了");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            MainProcess.EndWaitPing(settings);
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            MainProcess.StartSendPing(settings);
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(gIPTextBox.Text, true, 15, 100);
            }
            catch
            {
                Console.WriteLine("クリップボードにコピー失敗");
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (checkListView.SelectedIndex > -1)
            {
                if (MessageBox.Show("IPアドレスを削除しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    settings.RemoveIPAndName((IPAndName)checkListView.SelectedValue);
                    CallSaveSetting();
                }
            }
        }

        private void CheckListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (checkListView.SelectedIndex > -1)
            {
                deleteButton.IsEnabled = true;
                modButton.IsEnabled = true;
            }
            else
            {
                deleteButton.IsEnabled = false;
                modButton.IsEnabled = false;
            }
        }

        private void CallSaveSetting()
        {
            if (autoSaveEnable.IsChecked == true)
            {
                MainProcess.SaveSetting(settings);
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            AddIPAndNameWindow addIPAndNameWindow = new AddIPAndNameWindow();
            addIPAndNameWindow.ShowDialog();
            if (addIPAndNameWindow.SettingFinished)
            {
                settings.AddIPAndName(new IPAndName() { IP = addIPAndNameWindow.IP, Name = addIPAndNameWindow.IPName });
                CallSaveSetting();
            }
        }

        private void PortChangeButton_Click(object sender, RoutedEventArgs e)
        {
            PortSetting portSetting = new PortSetting();
            portSetting.ShowDialog();
            if (portSetting.SettingFinished)
            {
                MainProcess.EndWaitPing(settings);
                settings.Port = portSetting.Port;
                MainProcess.StartWaitPing(settings);
                CallSaveSetting();
            }
        }

        private void NumberOfSendChangeButton_Click(object sender, RoutedEventArgs e)
        {
            PortSetting portSetting = new PortSetting() { Min = 1, Max = 65535 };
            portSetting.ShowDialog();
            if (portSetting.SettingFinished)
            {
                settings.NumberOfSend = portSetting.Port;
                CallSaveSetting();
            }
        }

        public IntPtr GetHandle()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            return helper.Handle;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            MainProcess.SwitchToThisWindow(GetHandle(), true);
        }

        private void ModButton_Click(object sender, RoutedEventArgs e)
        {
            if (checkListView.SelectedIndex > -1)
            {
                IPAndName prevData = (IPAndName)checkListView.SelectedValue;
                AddIPAndNameWindow addIPAndNameWindow = new AddIPAndNameWindow(prevData.Name, prevData.IP);
                addIPAndNameWindow.ShowDialog();
                if (addIPAndNameWindow.SettingFinished)
                {
                    settings.RemoveIPAndName(prevData);
                    settings.AddIPAndName(new IPAndName() { IP = addIPAndNameWindow.IP, Name = addIPAndNameWindow.IPName });
                    CallSaveSetting();
                }
            }
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainProcess.ScanPortMapping(settings))
            {
                scanButton.Content = "ポートマッピング設定取得";
            }
            else
            {
                scanButton.Content = "停止";
            }
        }
    }
}

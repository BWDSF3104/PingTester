using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Windows;

namespace PingTester
{
    /// <summary>
    /// App.xaml の相互作用ロジック
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            MainProcess.StartLog();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            MainProcess.EndLog();
        }
    }
}

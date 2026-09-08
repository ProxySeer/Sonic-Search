using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace SonicSearch
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            bool isElevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            if (!isElevated)
            {
                var processInfo = new ProcessStartInfo();
                processInfo.Verb = "runas";
                processInfo.FileName = System.Reflection.Assembly.GetExecutingAssembly().Location;
                processInfo.UseShellExecute = true;

                try
                {
                    Process.Start(processInfo);
                }
                catch
                {
                    // User declined UAC prompt
                    MessageBox.Show("Administrator privileges are required to scan the Master File Table (MFT).", "SonicSearch", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}
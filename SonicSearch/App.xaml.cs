using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace SonicSearch
{
    public partial class App : Application
    {
        // Held for the app's whole lifetime once acquired - a static field (not local) so the
        // GC never collects it and silently releases the mutex out from under a running instance.
        private static Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppSettings.Load();
            ContentSearcher.ApplySettings();

            bool isElevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            // With the indexing service enabled, the app deliberately runs unelevated (that's
            // the whole point - an elevated window can't accept drag-and-drop from Explorer,
            // which runs unelevated; see ServiceInstallHelper.cs) and gets its file data over a
            // pipe instead of reading the MFT itself, so there's nothing here that needs admin.
            if (!isElevated && !AppSettings.Instance.UseIndexingService)
            {
                var processInfo = new ProcessStartInfo();
                processInfo.Verb = "runas";
                processInfo.FileName = System.Reflection.Assembly.GetExecutingAssembly().Location;
                processInfo.UseShellExecute = true;
                // Forward the original args (e.g. "/minimized" from the Windows-startup Run key)
                // to the elevated relaunch, since this unelevated bootstrap process never reaches
                // the code below that acts on them.
                processInfo.Arguments = string.Join(" ", e.Args);

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

            // From here on this IS the "real" instance that will actually run - the transient
            // bootstrap process above (unelevated, direct mode only) never reaches this point,
            // so there's no race with the mutex-holding elevated relaunch to worry about. This
            // guards against a genuine second instance: with the titlebar X now hiding to tray
            // instead of exiting, a user relaunching the exe by hand (not realizing the old one
            // is still running hidden) used to silently start a second full instance, both
            // independently connecting to the indexing service and registering the global
            // hotkey - that duplication was the actual cause of "Indexing Service Not Running"
            // on a subsequent launch, and of the repeated "file locked by SonicSearch" build
            // failures seen throughout development.
            _singleInstanceMutex = new Mutex(true, "SonicSearch_SingleInstance_Mutex_v1", out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show("SonicSearch is already running - check the system tray.", "SonicSearch", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            // "/minimized" is what StartupHelper's Windows-startup Run-key entry passes, so a
            // sign-in launch starts hidden in the tray instead of popping the window up on
            // screen. Show() then Hide() back-to-back (rather than never calling Show() at all)
            // keeps this window in Application.Windows, which the tray's "Exit SonicSearch"
            // handler relies on for the default ShutdownMode to actually terminate the app on
            // Close() - see the comment there.
            bool startMinimized = e.Args.Any(a => string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));
            var mainWindow = new MainWindow();
            mainWindow.Show();
            if (startMinimized) mainWindow.Hide();
        }
    }
}
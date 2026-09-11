using Microsoft.Win32;

namespace SonicSearch
{
    /// <summary>
    /// Reads/writes the per-user "run at sign-in" registry entry directly (HKCU, no elevation
    /// needed to write it) rather than mirroring the state in settings.json - the registry key
    /// IS the actual state Windows acts on, so keeping a separate settings flag in sync with it
    /// would just be a second source of truth that could drift (e.g. if the user removes the
    /// entry via Task Manager's Startup tab instead of this app).
    /// </summary>
    public static class StartupHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SonicSearch";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
            {
                return key?.GetValue(ValueName) != null;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                              Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) return;

                if (enabled)
                {
                    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    // Quote the path so the Run key survives spaces (e.g. "C:\Program Files\...").
                    // "/minimized" tells App.xaml.cs's OnStartup to hide straight to the tray
                    // instead of popping the window up on screen at sign-in.
                    key.SetValue(ValueName, "\"" + exePath + "\" /minimized");
                }
                else
                {
                    if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName);
                }
            }
        }
    }
}

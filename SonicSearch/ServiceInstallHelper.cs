using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace SonicSearch
{
    /// <summary>
    /// Installs/removes the SonicSearchService Windows Service via `sc.exe`, elevated through a
    /// single UAC prompt (same ProcessStartInfo { Verb = "runas" } pattern App.xaml.cs already
    /// uses to elevate the whole app) - this is the one-time privileged action the "Enable
    /// background indexing service" Settings checkbox performs; once installed, the service runs
    /// as LocalSystem and the GUI itself never needs to elevate again.
    /// </summary>
    public static class ServiceInstallHelper
    {
        private const string ServiceName = "SonicSearchService";

        public static bool IsInstalled()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    var status = sc.Status; // throws if not installed
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Staging copy of the service files, found next to SonicSearch.exe (dev/local-build
        /// convenience only - see SonicSearch.csproj's CopyServiceExeForLocalTesting target; a
        /// real installer would place this elsewhere before ever calling InstallAndStart).
        /// </summary>
        private static string StagingDir() => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Where the service actually runs from once installed: a stable location independent of
        /// wherever SonicSearch.exe happens to be built/reinstalled - NOT the GUI's own output
        /// folder. Pointing `sc create` straight at a dev build folder (an earlier version of
        /// this code did exactly that) means every GUI rebuild fights the running service for a
        /// lock on the very same files, and there's no path a real end-user install would ever
        /// point at a build folder either.
        /// </summary>
        private static string InstallDir() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SonicSearch", "Service");

        private static string InstalledExePath() => Path.Combine(InstallDir(), "SonicSearchService.exe");

        /// <summary>
        /// Installs (self-healing: stops/removes any existing registration first, so a leftover
        /// service from an earlier attempt - or from before InstallDir moved to ProgramData -
        /// can't make `sc create` fail with "service already exists") and starts the service,
        /// elevated. Throws with the tail of the install log on any failure - a single chained
        /// `cmd /c A &amp; B &amp; C` command (the previous approach) only reports the LAST
        /// command's exit code, so an earlier failed step (a missing file, a leftover
        /// registration) was invisible; writing a real .cmd script that logs every step lets a
        /// failure actually be diagnosed instead of just "doesn't work".
        /// </summary>
        public static void InstallAndStart()
        {
            string stagingDir = StagingDir().TrimEnd('\\');
            string stagingExe = Path.Combine(stagingDir, "SonicSearchService.exe");
            if (!File.Exists(stagingExe))
                throw new FileNotFoundException(
                    "SonicSearchService.exe was not found next to SonicSearch.exe. It needs to be deployed alongside the app.", stagingExe);

            string installDir = InstallDir();
            string installedExe = InstalledExePath();
            string logPath = Path.Combine(Path.GetTempPath(), "SonicSearchServiceInstall.log");
            string scriptPath = Path.Combine(Path.GetTempPath(), "SonicSearchServiceInstall.cmd");

            string script =
                "@echo off\r\n" +
                "> \"" + logPath + "\" echo === SonicSearchService install ===\r\n" +
                "sc stop " + ServiceName + " >> \"" + logPath + "\" 2>&1\r\n" +
                "sc delete " + ServiceName + " >> \"" + logPath + "\" 2>&1\r\n" +
                "if not exist \"" + installDir + "\" mkdir \"" + installDir + "\" >> \"" + logPath + "\" 2>&1\r\n" +
                "copy /Y \"" + stagingDir + "\\SonicSearchService.exe\" \"" + installDir + "\\\" >> \"" + logPath + "\" 2>&1\r\n" +
                "copy /Y \"" + stagingDir + "\\SonicSearchService.exe.config\" \"" + installDir + "\\\" >> \"" + logPath + "\" 2>&1\r\n" +
                "copy /Y \"" + stagingDir + "\\SonicSearch.Contracts.dll\" \"" + installDir + "\\\" >> \"" + logPath + "\" 2>&1\r\n" +
                "copy /Y \"" + stagingDir + "\\NtfsReader.dll\" \"" + installDir + "\\\" >> \"" + logPath + "\" 2>&1\r\n" +
                "sc create " + ServiceName + " binPath= \"" + installedExe + "\" start= auto DisplayName= \"SonicSearch Indexing Service\" >> \"" + logPath + "\" 2>&1\r\n" +
                "sc start " + ServiceName + " >> \"" + logPath + "\" 2>&1\r\n" +
                "echo DONE_EXIT_CODE=%errorlevel% >> \"" + logPath + "\"\r\n";

            File.WriteAllText(scriptPath, script);
            RunElevatedScript(scriptPath);

            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "(no log produced)";
            if (!log.Contains("DONE_EXIT_CODE=0"))
                throw new InvalidOperationException("Service install failed. Details:\n" + Tail(log, 800));
        }

        /// <summary>Runs `sc stop` + `sc delete` elevated. Safe to call even if already stopped
        /// or not installed (both `sc` commands tolerate that; exit code isn't checked here).</summary>
        public static void StopAndUninstall()
        {
            string args = string.Format("/c sc stop {0} & sc delete {0}", ServiceName);
            RunElevated("cmd.exe", args, ignoreExitCode: true);
        }

        private static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
            return "..." + text.Substring(text.Length - maxChars);
        }

        private static void RunElevatedScript(string scriptPath)
        {
            RunElevated("cmd.exe", "/c \"" + scriptPath + "\"", ignoreExitCode: true);
        }

        private static void RunElevated(string fileName, string arguments, bool ignoreExitCode = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var process = Process.Start(psi))
            {
                process.WaitForExit();
                if (!ignoreExitCode && process.ExitCode != 0)
                    throw new InvalidOperationException("sc.exe exited with code " + process.ExitCode);
            }
        }
    }
}

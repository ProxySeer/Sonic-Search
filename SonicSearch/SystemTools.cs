using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SonicSearch
{
    /// <summary>
    /// Built-in shortcuts to common Windows administrative tools and control panel
    /// applets, merged into search results alongside regular file/folder matches so
    /// they're reachable the same way a launcher like Wox/PowerToys Run would surface
    /// them (e.g. typing "device" surfaces Device Manager).
    /// </summary>
    public static class SystemTools
    {
        public sealed class Tool
        {
            public string Name;
            public string Target;
        }

        /// <summary>
        /// Resolved once at startup. Entries whose target file doesn't exist on this
        /// Windows edition (e.g. gpedit.msc/lusrmgr.msc are absent on Home editions)
        /// are silently dropped rather than offered as a broken result.
        /// </summary>
        public static readonly List<Tool> All = BuildList();

        private static List<Tool> BuildList()
        {
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            var candidates = new List<Tool>
            {
                new Tool { Name = "Control Panel", Target = Path.Combine(system32, "control.exe") },
                new Tool { Name = "Device Manager", Target = Path.Combine(system32, "devmgmt.msc") },
                new Tool { Name = "Disk Management", Target = Path.Combine(system32, "diskmgmt.msc") },
                new Tool { Name = "Computer Management", Target = Path.Combine(system32, "compmgmt.msc") },
                new Tool { Name = "Task Manager", Target = Path.Combine(system32, "taskmgr.exe") },
                new Tool { Name = "Task Scheduler", Target = Path.Combine(system32, "taskschd.msc") },
                new Tool { Name = "Services", Target = Path.Combine(system32, "services.msc") },
                new Tool { Name = "Event Viewer", Target = Path.Combine(system32, "eventvwr.msc") },
                new Tool { Name = "Local Users and Groups", Target = Path.Combine(system32, "lusrmgr.msc") },
                new Tool { Name = "Local Group Policy Editor", Target = Path.Combine(system32, "gpedit.msc") },
                new Tool { Name = "Registry Editor", Target = Path.Combine(windir, "regedit.exe") },
                new Tool { Name = "Command Prompt", Target = Path.Combine(system32, "cmd.exe") },
                new Tool { Name = "PowerShell", Target = Path.Combine(system32, @"WindowsPowerShell\v1.0\powershell.exe") },
                new Tool { Name = "System Properties", Target = Path.Combine(system32, "sysdm.cpl") },
                new Tool { Name = "Network Connections", Target = Path.Combine(system32, "ncpa.cpl") },
                new Tool { Name = "Programs and Features", Target = Path.Combine(system32, "appwiz.cpl") },
                new Tool { Name = "Display Settings", Target = Path.Combine(system32, "desk.cpl") },
                new Tool { Name = "Sound Settings", Target = Path.Combine(system32, "mmsys.cpl") },
                new Tool { Name = "Date and Time", Target = Path.Combine(system32, "timedate.cpl") },
                new Tool { Name = "Windows Firewall", Target = Path.Combine(system32, "firewall.cpl") },
                new Tool { Name = "Power Options", Target = Path.Combine(system32, "powercfg.cpl") },
            };

            return candidates.Where(t => File.Exists(t.Target)).ToList();
        }
    }
}

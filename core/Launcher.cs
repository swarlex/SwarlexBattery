// SPDX-License-Identifier: GPL-3.0-or-later
// SwarlexBattery.exe entry point. The scripts and plugins are embedded as resources
// ("app/<path>"), extracted once per build to %LOCALAPPDATA%\SwarlexBattery\app\<build-id>,
// and run inside this process by the PowerShell engine Windows already ships.
// No console window, no execution-policy prompt, no separate files to carry around.
using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SwarlexBattery")]
[assembly: AssemblyProduct("SwarlexBattery")]
[assembly: AssemblyDescription("Battery levels of wireless mice, keyboards and headsets in the Windows tray")]

namespace SwarlexBattery
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string exe = asm.Location;
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarlexBattery");
            string log = Path.Combine(baseDir, "swarlexbattery.log");
            try
            {
                string root = Extract(asm, baseDir);
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass; // only for our own extracted scripts
                using (Runspace rs = RunspaceFactory.CreateRunspace(iss))
                {
                    rs.ApartmentState = ApartmentState.STA;
                    rs.ThreadOptions = PSThreadOptions.UseCurrentThread; // WPF runs on this [STAThread]
                    rs.Open();
                    using (PowerShell ps = PowerShell.Create())
                    {
                        ps.Runspace = rs;
                        ps.AddCommand(Path.Combine(root, "SwarlexBattery.ps1")).AddParameter("ExePath", exe);
                        ps.Invoke();
                        if (ps.Streams.Error.Count > 0)
                        {
                            var sb = new StringBuilder();
                            foreach (ErrorRecord e in ps.Streams.Error) sb.AppendLine(e.ToString() + " @ " + e.InvocationInfo.PositionMessage);
                            Fail(log, sb.ToString());
                            return 1;
                        }
                    }
                }
                return 0;
            }
            catch (Exception e)
            {
                Fail(log, e.ToString());
                return 1;
            }
        }

        static string Extract(Assembly asm, string baseDir)
        {
            string id = asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
            string appDir = Path.Combine(baseDir, "app");
            string root = Path.Combine(appDir, id);
            if (File.Exists(Path.Combine(root, ".complete"))) return root;

            string tmp = root + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith("app/")) continue;
                string rel = name.Substring(4).Replace('/', Path.DirectorySeparatorChar);
                if (rel.Contains("..")) continue;
                string dest = Path.Combine(tmp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (Stream src = asm.GetManifestResourceStream(name))
                using (FileStream dst = File.Create(dest)) src.CopyTo(dst);
            }
            File.WriteAllText(Path.Combine(tmp, ".complete"), id);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.Move(tmp, root);

            // remove older builds
            foreach (string d in Directory.GetDirectories(appDir))
            {
                if (string.Equals(d, root, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, true); } catch { }
            }
            return root;
        }

        static void Fail(string log, string msg)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(log)); File.AppendAllText(log, DateTime.Now.ToString("u") + " FATAL " + msg + Environment.NewLine); } catch { }
            MessageBox.Show(msg.Length > 1500 ? msg.Substring(0, 1500) + "..." : msg, "SwarlexBattery", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

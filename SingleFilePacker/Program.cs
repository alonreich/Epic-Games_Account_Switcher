using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace SingleFilePacker
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--pack")) { Pack(); return; }
            ExtractAndRun(args);
        }
        static void Pack()
        {
            Console.WriteLine("Packing Payload...");
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Payload"))) dir = dir.Parent;
            if (dir == null) { Console.WriteLine("Payload directory not found."); Environment.Exit(1); }
            var payload = Path.Combine(dir.FullName, "Payload");
            var zipPath = Path.Combine(dir.FullName, "SingleFilePacker", "payload.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(payload, zipPath, CompressionLevel.Optimal, false);
            Console.WriteLine("Payload packed to: " + zipPath);
        }
        static void ExtractAndRun(string[] args)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("payload.zip"));
                if (string.IsNullOrEmpty(resourceName)) { MessageBox.Show("No payload found in this executable.", "Boot Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                var temp = Path.Combine(Path.GetTempPath(), "EpicSwitcherInstaller_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var fileStream = File.Create(Path.Combine(temp, "payload.zip"))) stream.CopyTo(fileStream);
                ZipFile.ExtractToDirectory(Path.Combine(temp, "payload.zip"), temp);
                var exe = Path.Combine(temp, "Epic-Games_Account_Switcher.exe");
                if (!File.Exists(exe)) { MessageBox.Show("Installer executable missing from payload.", "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                string cmdArgs = args != null && args.Length > 0 ? string.Join(" ", args.Select(a => "\"" + a + "\"")) : "";
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = cmdArgs, WorkingDirectory = temp, UseShellExecute = true });
            } catch (Exception ex) { MessageBox.Show("Fatal Boot Error: " + ex.Message + "\n\nStack: " + ex.StackTrace, "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}

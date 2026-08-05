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
            string temp = null;
            try
            {
                PurgeOldExtractionFolders();
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("payload.zip"));
                if (string.IsNullOrEmpty(resourceName)) { MessageBox.Show("No payload found in this executable.", "Boot Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                
                Form splash = new Form { 
                    Text = "Epic Games Account Switcher", 
                    StartPosition = FormStartPosition.CenterScreen, 
                    Size = new System.Drawing.Size(400, 150),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    ControlBox = false,
                    TopMost = true,
                    ShowInTaskbar = true
                };
                Label lbl = new Label { 
                    Text = "Preparing application environment...\nPlease wait while files are extracted.", 
                    Dock = DockStyle.Fill, 
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter, 
                    Font = new System.Drawing.Font("Segoe UI", 11)
                };
                splash.Controls.Add(lbl);
                splash.Show();
                splash.Refresh();

                temp = Path.Combine(Path.GetTempPath(), "EpicSwitcherInstaller_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var fileStream = File.Create(Path.Combine(temp, "payload.zip"))) stream.CopyTo(fileStream);
                ExtractZipSafely(Path.Combine(temp, "payload.zip"), temp);
                
                if (splash != null)
                {
                    splash.Close();
                    splash.Dispose();
                }

                var exe = Path.Combine(temp, "Epic-Games_Account_Switcher.exe");
                if (!File.Exists(exe))
                {
                    if (Directory.Exists(temp)) try { Directory.Delete(temp, true); } catch { }
                    MessageBox.Show("Installer executable missing from payload.", "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string cmdArgs = args != null && args.Length > 0 ? string.Join(" ", args.Select(a => "\"" + (a.EndsWith("\\") ? a + "\\" : a) + "\"")) : "";
                cmdArgs = (cmdArgs + " --extract-dir \"" + (temp.EndsWith("\\") ? temp + "\\" : temp) + "\"").Trim();
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = cmdArgs, WorkingDirectory = temp, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (temp != null && Directory.Exists(temp)) try { Directory.Delete(temp, true); } catch { }
                MessageBox.Show("Fatal Boot Error: " + ex.Message + "\n\nStack: " + ex.StackTrace, "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        static void ExtractZipSafely(string zipPath, string extractionRoot)
        {
            var root = Path.GetFullPath(extractionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(root, normalizedName));
                    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Payload contains an unsafe path: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    entry.ExtractToFile(fullPath, true);
                }
            }
        }

        static void PurgeOldExtractionFolders()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "EpicSwitcherInstaller_*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-12)) Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}

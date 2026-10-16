using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SingleFilePacker
{
    /// <summary>
    /// Single-file installer bootstrapper.
    /// --pack            (build time) zips .\Payload into payload.zip and writes its SHA-256 to payload.sha256.
    /// (default)         verifies the embedded payload hash, extracts it, runs the app's setup and waits, then deletes the extraction folder.
    ///   - Not elevated: extracts to %TEMP%\EpicSwitcherInstaller_* (user phase never runs with admin rights).
    ///   - Elevated:     extracts to %ProgramFiles%\Epic-Games_Account_Switcher_Setup_* (admin-only ACL inherited from Program Files),
    ///                   so no non-admin process can swap the files that run elevated.
    /// </summary>
    internal static class Program
    {
        private const string UserTempPrefix = "EpicSwitcherInstaller_";
        private const string ElevatedPrefix = "Epic-Games_Account_Switcher_Setup_";
        private const string AppExeName = "Epic-Games_Account_Switcher.exe";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Contains("--pack")) return Pack();
            return ExtractAndRun(args ?? new string[0]);
        }

        private static int Pack()
        {
            Console.WriteLine("Packing Payload...");
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Payload"))) dir = dir.Parent;
            if (dir == null) { Console.WriteLine("Payload directory not found."); return 1; }
            var payload = Path.Combine(dir.FullName, "Payload");
            if (!File.Exists(Path.Combine(payload, AppExeName))) { Console.WriteLine("Payload is missing " + AppExeName + "."); return 1; }
            var zipPath = Path.Combine(dir.FullName, "SingleFilePacker", "payload.zip");
            var hashPath = Path.Combine(dir.FullName, "SingleFilePacker", "payload.sha256");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (File.Exists(hashPath)) File.Delete(hashPath);
            ZipFile.CreateFromDirectory(payload, zipPath, CompressionLevel.Optimal, false);
            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zipPath)) hash = ToHex(sha.ComputeHash(fs));
            File.WriteAllText(hashPath, hash, Encoding.ASCII);
            Console.WriteLine("Payload packed to: " + zipPath + " (SHA-256 " + hash + ")");
            return 0;
        }

        private static bool IsAdministrator()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        private static int ExtractAndRun(string[] args)
        {
            string root = null;
            Form splash = null;
            try
            {
                var admin = IsAdministrator();
                PurgeOldExtractionFolders(admin);

                var assembly = Assembly.GetExecutingAssembly();
                var zipResource = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
                var hashResource = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("payload.sha256", StringComparison.OrdinalIgnoreCase));
                if (zipResource == null || hashResource == null)
                {
                    MessageBox.Show("This installer is incomplete. Please download it again.", "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                splash = ShowSplash();

                root = admin
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ElevatedPrefix + Guid.NewGuid().ToString("N"))
                    : Path.Combine(Path.GetTempPath(), UserTempPrefix + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(root)) throw new IOException("Extraction folder unexpectedly exists: " + root);
                Directory.CreateDirectory(root);

                string expected;
                using (var hs = assembly.GetManifestResourceStream(hashResource))
                using (var reader = new StreamReader(hs)) expected = reader.ReadToEnd().Trim();

                var zipPath = Path.Combine(root, "payload.zip");
                string actual;
                using (var stream = assembly.GetManifestResourceStream(zipResource))
                using (var sha = SHA256.Create())
                using (var crypto = new CryptoStream(stream, sha, CryptoStreamMode.Read))
                using (var fileStream = File.Create(zipPath))
                {
                    crypto.CopyTo(fileStream);
                    crypto.Flush();
                    actual = ToHex(sha.Hash);
                }
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installer is damaged (payload checksum mismatch). Please download it again.");

                ExtractZipSafely(zipPath, root);
                try { File.Delete(zipPath); } catch { }
                CloseSplash(ref splash);

                var exe = Path.Combine(root, AppExeName);
                if (!File.Exists(exe)) throw new FileNotFoundException("The installer payload is missing the application.");

                var forwarded = args.Where(a => !string.Equals(a, "--extract-dir", StringComparison.OrdinalIgnoreCase)).ToList();
                if (!forwarded.Any(a => a.Equals("--installing", StringComparison.OrdinalIgnoreCase) || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
                    forwarded.Insert(0, "--installing");
                forwarded.Add("--extract-dir"); forwarded.Add(root);
                forwarded.Add("--bootstrapper"); forwarded.Add(Assembly.GetExecutingAssembly().Location);

                using (var child = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = string.Join(" ", forwarded.Select(Quote)), WorkingDirectory = root, UseShellExecute = false }))
                {
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (Exception ex)
            {
                CloseSplash(ref splash);
                MessageBox.Show("Setup could not start:\n\n" + ex.Message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                if (root != null) DeleteWithRetry(root);
            }
        }

        private static Form ShowSplash()
        {
            try
            {
                var splash = new Form
                {
                    Text = "Epic Games Account Switcher",
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new System.Drawing.Size(400, 150),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    ControlBox = false,
                    TopMost = true,
                    ShowInTaskbar = true
                };
                splash.Controls.Add(new Label
                {
                    Text = "Preparing setup...\nVerifying and extracting files.",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Font = new System.Drawing.Font("Segoe UI", 11)
                });
                splash.Show();
                splash.Refresh();
                return splash;
            }
            catch { return null; }
        }

        private static void CloseSplash(ref Form splash)
        {
            if (splash == null) return;
            try { splash.Close(); splash.Dispose(); } catch { }
            splash = null;
        }

        private static string Quote(string a)
        {
            if (a == null) return "\"\"";
            if (a.EndsWith("\\")) a += "\\";
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        private static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        private static void ExtractZipSafely(string zipPath, string extractionRoot)
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

        private static void DeleteWithRetry(string dir)
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
                catch { Thread.Sleep(500); }
            }
        }

        private static void PurgeOldExtractionFolders(bool admin)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), UserTempPrefix + "*", SearchOption.TopDirectoryOnly))
                    try { if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-12)) Directory.Delete(dir, true); } catch { }
                if (admin)
                {
                    foreach (var dir in Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ElevatedPrefix + "*", SearchOption.TopDirectoryOnly))
                        try { if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1)) Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
        }
    }
}

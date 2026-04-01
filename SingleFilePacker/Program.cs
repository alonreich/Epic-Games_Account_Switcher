using System;
using System.IO;
using System.IO.Compression;
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
            if (args.Length > 0 && args[0] == "--pack")
            {
                Pack();
                return;
            }

            string runDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic-Games_Account_Switcher", "Runtime");
            ExtractAndRun(runDir, args);
        }

        static void Pack()
        {
            try
            {
                string sourceDir = Path.GetFullPath("Payload");
                string zipPath = Path.GetFullPath("SingleFilePacker\\Payload.zip");
                
                if (!Directory.Exists(sourceDir))
                {
                    Console.WriteLine("Error: Payload directory not found at " + sourceDir);
                    return;
                }

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(sourceDir, zipPath);
                Console.WriteLine("Payload packed successfully into " + zipPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Packing failed: " + ex.Message);
            }
        }

        static void ExtractAndRun(string runDir, string[] args)
        {
            try 
            {
                if (Directory.Exists(runDir))
                {
                    try { Directory.Delete(runDir, true); } catch { }
                }
                Directory.CreateDirectory(runDir);
                
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "SingleFilePacker.Payload.zip";
                
                using (Stream resStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resStream == null)
                    {
                        MessageBox.Show($"Internal Error: Resource '{resourceName}' not found in assembly.", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    using (ZipArchive archive = new ZipArchive(resStream))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string completeFileName = Path.Combine(runDir, entry.FullName);
                            string directory = Path.GetDirectoryName(completeFileName);
                            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                            if (!string.IsNullOrEmpty(entry.Name)) entry.ExtractToFile(completeFileName, true);
                        }
                    }
                }

                string exePath = Path.Combine(runDir, "Epic-Games_Account_Switcher.exe");
                if (!File.Exists(exePath))
                {
                    MessageBox.Show("Internal Error: Main executable not found in extracted payload.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string cmdArgs = string.Join(" ", args);
                ProcessStartInfo startInfo = new ProcessStartInfo(exePath, cmdArgs)
                {
                    WorkingDirectory = runDir,
                    UseShellExecute = true
                };
                
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Fatal Launch Error:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

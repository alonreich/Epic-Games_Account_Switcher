using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Globals
{
    public enum UpdateCheckStatus { UpToDate, UpdateAvailable, Offline, Failed }

    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string TagName { get; set; }
        public string Title { get; set; }
        public string ReleaseNotes { get; set; }
        public string ReleasePageUrl { get; set; }
        public string InstallerUrl { get; set; }
        public long InstallerSize { get; set; }
        public string ChecksumUrl { get; set; }
        public DateTime PublishedUtc { get; set; }
    }

    public sealed class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; set; }
        public UpdateInfo Info { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// GitHub Releases based updater.
    /// - Check: one small JSON request (~5 KB), 10 s timeout, silent when offline / DNS fails.
    /// - Download: only when the user clicks "Update now"; reuses an already verified download instead of re-downloading.
    /// - Verify: SHA-256 from the release's .sha256 asset AND (when a signing thumbprint is pinned at build time)
    ///   an intact Authenticode signature made by exactly that certificate.
    /// - Install: only on explicit user action; the installer runs in --update mode and keeps all account data.
    /// </summary>
    public static class UpdateService
    {
        public const string RepositoryOwner = "alonreich";
        public const string RepositoryName = "Epic-Games_Account_Switcher";
        public const string InstallerAssetName = "Epic-Games_Account_Switcher.exe";
        public const string ChecksumAssetName = "Epic-Games_Account_Switcher.exe.sha256";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
        private const long MinimumFreeSpaceBytes = 64L * 1024L * 1024L;

        public static string UpdatesFolder => Path.Combine(Globals.UserDataFolder, "Updates");

        public static string PinnedSigningThumbprint
        {
            get
            {
                try
                {
                    var attr = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => string.Equals(a.Key, "SigningThumbprint", StringComparison.OrdinalIgnoreCase));
                    var value = attr?.Value;
                    return string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "[^0-9A-Fa-f]", "").ToUpperInvariant();
                }
                catch { return null; }
            }
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Epic-Games-Account-Switcher/" + Globals.Version);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return new UpdateCheckResult { Status = UpdateCheckStatus.Offline, Message = "You appear to be offline." };
            try
            {
                using (var client = CreateClient(CheckTimeout))
                using (var response = await client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate, Message = "No published releases yet." };
                    if (!response.IsSuccessStatusCode)
                        return new UpdateCheckResult { Status = UpdateCheckStatus.Failed, Message = "Update server answered " + (int)response.StatusCode + ". Try again later." };

                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var tag = (string)json["tag_name"];
                    var version = AppVersion.Normalize(tag);
                    if (!AppVersion.IsValid(version))
                        return new UpdateCheckResult { Status = UpdateCheckStatus.Failed, Message = "Latest release has an unrecognized version: " + tag };

                    if (!AppVersion.IsNewer(version, Globals.Version))
                        return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate, Message = "You have the latest version (" + Globals.Version + ")." };

                    var assets = (json["assets"] as JArray) ?? new JArray();
                    JToken installer = assets.FirstOrDefault(a => string.Equals((string)a["name"], InstallerAssetName, StringComparison.OrdinalIgnoreCase));
                    JToken checksum = assets.FirstOrDefault(a => string.Equals((string)a["name"], ChecksumAssetName, StringComparison.OrdinalIgnoreCase));
                    var info = new UpdateInfo
                    {
                        Version = version,
                        TagName = tag,
                        Title = (string)json["name"],
                        ReleaseNotes = (string)json["body"] ?? "",
                        ReleasePageUrl = (string)json["html_url"],
                        InstallerUrl = installer == null ? null : (string)installer["browser_download_url"],
                        InstallerSize = installer == null ? 0 : (long?)installer["size"] ?? 0,
                        ChecksumUrl = checksum == null ? null : (string)checksum["browser_download_url"],
                        PublishedUtc = (DateTime?)json["published_at"] ?? DateTime.UtcNow
                    };
                    return new UpdateCheckResult { Status = UpdateCheckStatus.UpdateAvailable, Info = info, Message = "Version " + version + " is available." };
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new UpdateCheckResult { Status = UpdateCheckStatus.Offline, Message = "The update server did not respond in time." };
            }
            catch (HttpRequestException ex)
            {
                Globals.WriteToLog("[UPDATE] Check failed (network)", ex);
                return new UpdateCheckResult { Status = UpdateCheckStatus.Offline, Message = "Could not reach the update server." };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Globals.WriteToLog("[UPDATE] Check failed", ex);
                return new UpdateCheckResult { Status = UpdateCheckStatus.Failed, Message = "Update check failed: " + ex.Message };
            }
        }

        /// <summary>Downloads (or reuses) the installer for the given release and verifies hash and signature. Returns the verified path.</summary>
        public static async Task<string> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (string.IsNullOrWhiteSpace(info.InstallerUrl)) throw new InvalidOperationException("This release has no installer attached. Please download it from the release page.");
            if (string.IsNullOrWhiteSpace(info.ChecksumUrl)) throw new InvalidOperationException("This release has no checksum file, so it cannot be verified. Please download it from the release page.");

            var folder = Path.Combine(UpdatesFolder, info.Version);
            Directory.CreateDirectory(folder);
            var finalPath = Path.Combine(folder, InstallerAssetName);
            var partialPath = finalPath + ".partial";

            using (var client = CreateClient(TimeSpan.FromMinutes(10)))
            {
                var expectedHash = await DownloadChecksumAsync(client, info.ChecksumUrl, cancellationToken).ConfigureAwait(false);

                if (File.Exists(finalPath))
                {
                    if (string.Equals(ComputeSha256(finalPath), expectedHash, StringComparison.OrdinalIgnoreCase) && VerifySignature(finalPath, out _))
                    {
                        progress?.Report(1.0);
                        return finalPath;
                    }
                    TryDelete(finalPath);
                }

                EnsureFreeSpace(folder, Math.Max(info.InstallerSize, 4L * 1024L * 1024L));

                using (var response = await client.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? info.InstallerSize;
                    using (var sha = SHA256.Create())
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await target.WriteAsync(buffer, 0, n, cancellationToken).ConfigureAwait(false);
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            read += n;
                            if (total > 0) progress?.Report(Math.Min(1.0, (double)read / total));
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        var actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                        target.Flush(true);
                        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            target.Dispose();
                            TryDelete(partialPath);
                            throw new CryptographicException("The downloaded update is corrupted or was tampered with (checksum mismatch). Nothing was installed.");
                        }
                    }
                }
            }

            string signatureProblem;
            if (!VerifySignature(partialPath, out signatureProblem))
            {
                TryDelete(partialPath);
                throw new CryptographicException("The downloaded update failed its digital signature check (" + signatureProblem + "). Nothing was installed.");
            }

            TryDelete(finalPath);
            File.Move(partialPath, finalPath);
            CleanupOldDownloads(info.Version);
            progress?.Report(1.0);
            return finalPath;
        }

        /// <summary>Starts the verified installer in update mode (keeps account data, no setup questions).</summary>
        public static void LaunchInstaller(string verifiedInstallerPath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = verifiedInstallerPath,
                Arguments = "--installing --update",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(verifiedInstallerPath)
            });
        }

        public static void CleanupOldDownloads(string keepVersion = null)
        {
            try
            {
                if (!Directory.Exists(UpdatesFolder)) return;
                foreach (var dir in Directory.GetDirectories(UpdatesFolder))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(keepVersion) && string.Equals(name, keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
                    if (keepVersion == null && AppVersion.IsNewer(name, Globals.Version)) continue;
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
        }

        private static async Task<string> DownloadChecksumAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            var text = await client.GetStringAsync(url).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var match = Regex.Match(text ?? "", "\\b[0-9a-fA-F]{64}\\b");
            if (!match.Success) throw new InvalidDataException("The release checksum file is not valid.");
            return match.Value.ToUpperInvariant();
        }

        private static void EnsureFreeSpace(string folder, long payloadBytes)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                var drive = new DriveInfo(root);
                var needed = payloadBytes * 2 + MinimumFreeSpaceBytes;
                if (drive.IsReady && drive.AvailableFreeSpace < needed)
                    throw new IOException("Not enough free disk space on " + root + " to download the update. Free up at least " + (needed / (1024 * 1024)) + " MB and try again.");
            }
            catch (IOException) { throw; }
            catch { }
        }

        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        /// <summary>
        /// When a signing thumbprint is pinned, the file must carry an intact Authenticode signature from that exact certificate.
        /// A self-signed (untrusted-root) chain is accepted because the certificate itself is pinned; a modified file is always rejected.
        /// </summary>
        public static bool VerifySignature(string path, out string problem)
        {
            problem = null;
            var pinned = PinnedSigningThumbprint;
            if (pinned == null)
            {
                Globals.WriteToLog("[UPDATE] No signing thumbprint pinned in this build; relying on SHA-256 verification only.");
                return true;
            }

            var trust = AuthenticodeNative.Verify(path);
            if (!AuthenticodeNative.IsSignatureIntact(trust))
            {
                problem = "signature missing or file modified, code 0x" + trust.ToString("X8");
                return false;
            }

            try
            {
                var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (!string.Equals(signer.Thumbprint, pinned, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "signed by an unexpected certificate " + signer.Thumbprint;
                    return false;
                }
            }
            catch (Exception ex)
            {
                problem = "could not read signer certificate: " + ex.Message;
                return false;
            }
            return true;
        }

        private static class AuthenticodeNative
        {
            private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
            private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
            private const uint CERT_E_CHAINING = 0x800B010A;
            private const uint CERT_E_UNTRUSTEDTESTROOT = 0x800B010D;
            private static readonly Guid WintrustActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WINTRUST_FILE_INFO
            {
                public uint cbStruct;
                [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct WINTRUST_DATA
            {
                public uint cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint dwUIChoice;
                public uint fdwRevocationChecks;
                public uint dwUnionChoice;
                public IntPtr pFile;
                public uint dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint dwProvFlags;
                public uint dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
            private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

            public static uint Verify(string path)
            {
                var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = path };
                IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                IntPtr pData = IntPtr.Zero;
                try
                {
                    Marshal.StructureToPtr(fileInfo, pFile, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                        dwUIChoice = 2,
                        fdwRevocationChecks = 0,
                        dwUnionChoice = 1,
                        pFile = pFile,
                        dwStateAction = 0,
                        dwProvFlags = 0x00000010 | 0x00001000
                    };
                    pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                    Marshal.StructureToPtr(data, pData, false);
                    return WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, pData);
                }
                catch { return TRUST_E_NOSIGNATURE; }
                finally
                {
                    if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                    Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                    Marshal.FreeHGlobal(pFile);
                }
            }

            public static bool IsSignatureIntact(uint result)
            {
                return result == 0 || result == CERT_E_UNTRUSTEDROOT || result == CERT_E_CHAINING || result == CERT_E_UNTRUSTEDTESTROOT;
            }
        }
    }
}

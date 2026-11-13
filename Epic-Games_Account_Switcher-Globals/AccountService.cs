using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>Account list, profile images and shared paths/helpers.</summary>
    public partial class AccountService
    {
        private static string _lastSwitchFailureMessage;
        private static readonly object AccountsFileLock = new object();
        public static readonly SemaphoreSlim SessionOperationLock = new SemaphoreSlim(1, 1);
        private static string BaseDataPath { get { return Path.Combine(Globals.UserDataFolder, "EG"); } }
        private static string AccountsJsonPath { get { return Path.Combine(BaseDataPath, "accounts.json"); } }
        private static List<string> RegistryKeysToSwap { get { return AppSettings.Instance.RegistryKeysToSwap; } }
        private static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        private static string RoamingAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        private static string CommonAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }
        private static string EpicSavedPath { get { return Path.Combine(LocalAppData, "EpicGamesLauncher", "Saved"); } }
        private static string EpicConfigPath { get { return Path.Combine(EpicSavedPath, "Config"); } }
        private static string ProgramDataManifestsPath { get { return Path.Combine(CommonAppData, "Epic", "EpicGamesLauncher", "Data", "Manifests"); } }

        private static readonly string[] AuthFiles = { "PersistentAuthStorage.json", "Config\\Windows\\PersistentAuthStorage.json" };
        private static readonly string[] AuthBinaryFiles = { "PersistentAuthStorage.bin", "Config\\Windows\\PersistentAuthStorage.bin" };
        private static readonly string[] SessionItemsToCapture = { "Config", "Common", "SocialCache", "WebCaches", "Manifests", "Data", "UserVaultSettings", "PersistentStorage", "Portal", "Overlay", "LoginCache.json", "Engine.ini" };
        private static readonly string[] EOSFoldersToCapture = { "Epic Online Services", "EOSOverlay", "EOSUserHelper", "EpicOnlineServicesUserHelper", "EpicOnlineServices", "EpicGamesLauncher\\Data", "EpicGamesLauncher\\Saved\\SocialCache" };
        private static readonly string[] RegistryRootsToCapture = { @"HKCU\Software\Epic Games\Unreal Engine\Identifiers", @"HKCU\Software\Epic Games\Unreal Engine\EOS", @"HKCU\Software\Epic Games\EOS", @"HKCU\Software\Epic Games\EpicGamesLauncher" };

        public static string LastSwitchFailureMessage { get { return _lastSwitchFailureMessage ?? ""; } }

        public static string GetDefaultProfileImagePath()
        {
            var cands = new[] { Path.Combine(Globals.UserDataFolder, "Skin_Icons", "Default_Skin_Girl.jpg"), Path.Combine(Globals.UserDataFolder, "Skin_Icons", "Default_Skin_Guy.jpg"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", "Default_Skin_Guy.jpg"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", "Lizard_Skin.jpg") };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return cands[0];
        }

        private static string NormalizeProfileImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return GetDefaultProfileImagePath();
            try
            {
                // Older versions kept per-account copies of shipped avatars; point those back at the shipped file.
                var builtIn = SkinLibrary.FindBuiltInFor(path);
                if (builtIn != null && File.Exists(builtIn)) return builtIn;

                var fn = Path.GetFileName(path);
                var candidates = new List<string>();

                if (Path.IsPathRooted(path))
                {
                    candidates.Add(path);
                }
                else
                {
                    candidates.Add(Path.Combine(Globals.UserDataFolder, path));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
                }

                if (!string.IsNullOrWhiteSpace(fn))
                {
                    candidates.Add(Path.Combine(Globals.UserDataFolder, "Skin_Icons", fn));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons", fn));
                }

                foreach (var candidate in candidates)
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);

                var recursive = FindSkinByFileName(fn);
                if (!string.IsNullOrWhiteSpace(recursive)) return recursive;
            }
            catch { }
            return GetDefaultProfileImagePath();
        }

        private static string FindSkinByFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var roots = new[]
            {
                Path.Combine(Globals.UserDataFolder, "Skin_Icons"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons")
            };

            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var match = Directory.GetFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match)) return Path.GetFullPath(match);
                }
                catch { }
            }

            return null;
        }

        public static List<Account> GetAccounts()
        {
            lock (AccountsFileLock)
            {
                if (!File.Exists(AccountsJsonPath)) return new List<Account>();
                try
                {
                    var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(AccountsJsonPath)) ?? new List<Account>();
                    foreach (var a in accs) NormalizeAccount(a);
                    MarkActiveAccount(accs, GetCurrentAccountId());
                    _accountsFileUnreadableAndUnsaved = false;
                    return accs;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[ACC] Failed to load accounts.json", ex);
                    PreserveUnreadableAccountsFile();
                    return new List<Account>();
                }
            }
        }

        // Set while accounts.json exists but can't be read and no safety copy of it could be made: saving would destroy it.
        private static bool _accountsFileUnreadableAndUnsaved;
        private static string _preservedAccountsStamp;
        private static string _pendingAccountsLoadProblem;

        /// <summary>
        /// Returns (once) the path of the safety copy made of an unreadable account list, "" when no copy could be made,
        /// or null when the list was read normally. The UI tells the user so an empty list is never mistaken for "no accounts".
        /// </summary>
        public static string TakeAccountsLoadProblem()
        {
            lock (AccountsFileLock)
            {
                var problem = _pendingAccountsLoadProblem;
                _pendingAccountsLoadProblem = null;
                return problem;
            }
        }

        /// <summary>Copies an unreadable accounts.json aside (once per file version) so the next save can't destroy it.</summary>
        private static void PreserveUnreadableAccountsFile()
        {
            try
            {
                var info = new FileInfo(AccountsJsonPath);
                var stamp = info.Length + "_" + info.LastWriteTimeUtc.Ticks;
                if (stamp == _preservedAccountsStamp && !_accountsFileUnreadableAndUnsaved) return;
                var copy = AccountsJsonPath + ".unreadable_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(AccountsJsonPath, copy, false);
                _preservedAccountsStamp = stamp;
                _accountsFileUnreadableAndUnsaved = false;
                _pendingAccountsLoadProblem = copy;
                Globals.WriteToLog("[ACC] Unreadable accounts.json preserved as " + copy);
            }
            catch (Exception ex)
            {
                _accountsFileUnreadableAndUnsaved = true;
                _pendingAccountsLoadProblem = "";
                Globals.WriteToLog("[ACC] Could not preserve unreadable accounts.json; saving is blocked until it can be read", ex);
            }
        }

        public static void SaveAccounts(List<Account> accounts)
        {
            lock (AccountsFileLock)
            {
                if (_accountsFileUnreadableAndUnsaved && File.Exists(AccountsJsonPath))
                    throw new IOException("The saved account list could not be read, so it was not overwritten.");
                Directory.CreateDirectory(BaseDataPath);
                foreach (var a in accounts) NormalizeAccount(a);
                var tmp = AccountsJsonPath + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmp, JsonConvert.SerializeObject(accounts, Formatting.Indented));
                AtomicReplaceFile(tmp, AccountsJsonPath);
            }
        }

        private static void NormalizeAccount(Account a)
        {
            if (a == null) return;
            if (a.Name == null) a.Name = "Unknown Account";
            if (a.Email == null) a.Email = "Unknown Email";
            if (a.Description == null) a.Description = string.Empty;
            a.AccentColor = string.IsNullOrWhiteSpace(a.AccentColor) ? "#7AA2F7" : a.AccentColor;
            a.ProfileImagePath = NormalizeProfileImagePath(a.ProfileImagePath);
            if (a.RegistryData == null) a.RegistryData = new Dictionary<string, string>();
            if (a.Credentials == null) a.Credentials = new List<CredentialService.SavedCredential>();
            if (a.RememberMeDataByPath == null) a.RememberMeDataByPath = new Dictionary<string, string>();
            if (a.LauncherWindowSettingsByPath == null) a.LauncherWindowSettingsByPath = new Dictionary<string, string>();
        }

        /// <summary>
        /// Removes browser cache from saved profiles (versions before 3.2 saved it; it was ~90% of their size).
        /// Runs in the background at startup, one profile at a time so a switch never waits long.
        /// </summary>
        public static void PruneSavedProfileCaches()
        {
            try
            {
                if (!Directory.Exists(BaseDataPath)) return;
                long freed = 0;
                foreach (var dir in Directory.GetDirectories(BaseDataPath))
                {
                    if (string.Equals(Path.GetFileName(dir), "Skin_Icons", StringComparison.OrdinalIgnoreCase)) continue;
                    SessionOperationLock.Wait();
                    try { freed += CacheFilter.PruneDisposable(dir); }
                    finally { SessionOperationLock.Release(); }
                }
                if (freed > 0) Globals.WriteToLog("[CACHE] Removed browser cache from saved profiles. Freed=" + (freed / (1024 * 1024)) + " MB");
            }
            catch (Exception ex) { Globals.WriteToLog("[CACHE] Saved-profile cache cleanup failed.", ex); }
        }

        private static string GetShortId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        private static void AtomicWriteAllLines(string path, IList<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            File.WriteAllLines(tmp, lines.ToArray());
            AtomicReplaceFile(tmp, path);
        }

        private static void AtomicReplaceFile(string tmp, string target)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(target))
            {
                var backup = target + ".bak_" + Guid.NewGuid().ToString("N");
                File.Replace(tmp, target, backup, true);
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            }
            else { File.Move(tmp, target); }
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static IEnumerable<string> ReadAllLinesSafe(string path)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var lines = new List<string>();
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lines.Add(line);
                    }
                    return lines;
                }
                catch (IOException)
                {
                    try
                    {
                        var tmpCopy = Path.Combine(Path.GetTempPath(), "read_copy_" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.Copy(path, tmpCopy, true);
                        try { return File.ReadAllLines(tmpCopy); }
                        finally { try { File.Delete(tmpCopy); } catch { } }
                    }
                    catch { }
                    Thread.Sleep(100 * (i + 1));
                }
                catch { break; }
            }
            return new string[0];
        }

        public static void DeleteAccount(string id)
        {
            var accs = GetAccounts();
            var t = accs.FirstOrDefault(a => a.Id == id);
            if (t == null) return;
            accs.Remove(t);
            SaveAccounts(accs);
            var f = Path.Combine(BaseDataPath, t.Id);
            if (Directory.Exists(f)) try { Globals.ForceDeleteDirectory(f); } catch { }
        }

        private static string GetDeterministicColor(string input)
        {
            var colors = new[] { "#7AA2F7", "#9ECE6A", "#BB9AF7", "#E0AF68", "#F7768E", "#7DCFFF", "#2AC3DE", "#B4F9F8" };
            var hash = 0;
            foreach (var ch in input ?? string.Empty) hash = (hash << 5) - hash + ch;
            return colors[Math.Abs(hash) % colors.Length];
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Epic_Games_Account_Switcher_Globals
{
    public class AccountService
    {
        private sealed class SessionValidationResult
        {
            public bool IsValid { get; set; }
            public string Message { get; set; }
            public string AccountId { get; set; }
            public DateTime? ExpiresUtc { get; set; }
        }

        private static readonly object AccountsFileLock = new object();
        private static readonly SemaphoreSlim SessionOperationLock = new SemaphoreSlim(1, 1);
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
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons", fn));
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
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons")
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
                    return accs;
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("[ACC] Failed to load accounts.json", ex);
                    return new List<Account>();
                }
            }
        }

        public static void SaveAccounts(List<Account> accounts)
        {
            lock (AccountsFileLock)
            {
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

        private static bool IsSessionValid(string folder)
        {
            return ValidateSession(folder, null).IsValid;
        }

        private static SessionValidationResult ValidateSession(string folder, string expectedAccountId)
        {
            var result = new SessionValidationResult { IsValid = false, Message = "No readable auth state was found. [v1.0.1]" };
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    result.Message = "Session folder does not exist: " + folder + " [v1.0.1]";
                    return result;
                }

                var accountId = expectedAccountId ?? GetCurrentAccountId();
                var hasLegacyJson = AuthFiles.Any(f => File.Exists(Path.Combine(folder, f)));
                var hasModernBinary = !string.IsNullOrEmpty(accountId) && File.Exists(Path.Combine(folder, "Data", "OC_" + accountId + ".dat"));

                if (!hasLegacyJson && !hasModernBinary)
                {
                    result.Message = "Authentication state file is missing. (Checked legacy JSON and modern binary blobs for ID: " + (accountId ?? "NULL") + ") [v1.0.1]";
                    return result;
                }

                if (hasModernBinary)
                {
                    result.IsValid = true;
                    result.AccountId = accountId;
                    result.Message = "Modern binary auth blob detected. [v1.0.1]";
                    return result;
                }

                var authPaths = AuthFiles.Select(f => Path.Combine(folder, f)).Where(File.Exists).ToList();
                var allAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime? bestExpiry = null;
                var unreadable = new List<string>();
                foreach (var p in authPaths)
                {
                    try
                    {
                        var json = File.ReadAllText(p);
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var token = JsonConvert.DeserializeObject<JToken>(json);
                        var expiries = new List<DateTime>();
                        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CollectAuthFacts(token, expiries, accountIds);
                        foreach (var id in accountIds) if (!string.IsNullOrWhiteSpace(id)) allAccountIds.Add(id.Trim());
                        foreach (var exp in expiries)
                        {
                            var utc = exp.Kind == DateTimeKind.Utc ? exp : exp.ToUniversalTime();
                            if (!bestExpiry.HasValue || utc > bestExpiry.Value) bestExpiry = utc;
                        }
                    }
                    catch (Exception ex)
                    {
                        unreadable.Add(Path.GetFileName(p) + ": " + ex.Message);
                    }
                }
                if (!bestExpiry.HasValue)
                {
                    result.Message = "No future auth expiration could be read." + (unreadable.Count > 0 ? " Errors: " + string.Join("; ", unreadable.ToArray()) : "") + " [v1.0.1]";
                    return result;
                }
                result.ExpiresUtc = bestExpiry.Value;
                if (bestExpiry.Value <= DateTime.UtcNow.AddMinutes(5))
                {
                    result.Message = "Auth token expires too soon: " + bestExpiry.Value.ToString("o") + " [v1.0.1]";
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(expectedAccountId) && allAccountIds.Count > 0 && !allAccountIds.Contains(expectedAccountId))
                {
                    result.Message = "Auth blob account id mismatch. Expected " + expectedAccountId + ". Found " + string.Join(",", allAccountIds.ToArray()) + " [v1.0.1]";
                    return result;
                }
                result.IsValid = true;
                result.AccountId = allAccountIds.FirstOrDefault();
                result.Message = "Auth blob is valid until " + bestExpiry.Value.ToString("o") + " [v1.0.1]";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message + " [v1.0.1]";
                Globals.WriteToLog("[AUTH-CHECK] Validation error in " + folder, ex);
                return result;
            }
        }

        private static void CollectAuthFacts(JToken token, List<DateTime> expiries, HashSet<string> accountIds)
        {
            if (token == null) return;
            var prop = token as JProperty;
            if (prop != null)
            {
                var name = prop.Name ?? "";
                var value = prop.Value == null ? null : prop.Value.ToString();
                if (IsAccountIdName(name) && !string.IsNullOrWhiteSpace(value)) accountIds.Add(value.Trim());
                if (IsExpirationName(name))
                {
                    DateTime exp;
                    if (TryParseExpiration(value, out exp)) expiries.Add(exp);
                }
            }
            foreach (var child in token.Children()) CollectAuthFacts(child, expiries, accountIds);
        }

        private static bool IsAccountIdName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
            return n == "accountid" || n == "epicaccountid" || n == "userid";
        }

        private static bool IsExpirationName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
            return n == "expiresat" || n == "expires" || n == "expiration" || n == "expirationtime" || n == "expireat";
        }

        private static bool TryParseExpiration(string value, out DateTime expirationUtc)
        {
            expirationUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            DateTime parsed;
            if (DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                expirationUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                return true;
            }
            long number;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                try
                {
                    if (number > 1000000000000L) expirationUtc = DateTimeOffset.FromUnixTimeMilliseconds(number).UtcDateTime;
                    else if (number > 1000000000L) expirationUtc = DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime;
                    return expirationUtc > DateTime.UtcNow.AddYears(-10);
                }
                catch { }
            }
            return false;
        }

        private static Dictionary<string, string> GetRememberMeDataByPath()
        {
            return CaptureIniValuesByPath("RememberMe", "Data");
        }

        private static Dictionary<string, string> GetLauncherWindowSettingsByPath()
        {
            return CaptureIniValuesByPath("WindowSettings", "MainLauncherWindow");
        }

        private static Dictionary<string, string> CaptureIniValuesByPath(string section, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!Directory.Exists(EpicConfigPath)) return result;
                var files = Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                foreach (var ini in files)
                {
                    var lines = File.ReadAllLines(ini);
                    var val = GetIniValue(lines, section, key);
                    if (!string.IsNullOrWhiteSpace(val)) result[MakeRelativePath(EpicConfigPath, ini)] = val;
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[INI] Failed to capture " + section + ":" + key, ex);
            }
            return result;
        }

        private static bool SetRememberMeDataSurgically(Account account)
        {
            if (account == null) return true;
            var values = account.RememberMeDataByPath ?? new Dictionary<string, string>();
            if (values.Count == 0 && !string.IsNullOrWhiteSpace(account.RememberMeData))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) values[MakeRelativePath(EpicConfigPath, ini)] = account.RememberMeData;
            }
            if (values.Count == 0) return true;
            foreach (var kv in values)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                ApplyRememberMeToIni(ini, kv.Value);
            }
            return VerifyRememberMeApplied(account);
        }

        private static bool SetLauncherWindowSettingsSurgically(Account account)
        {
            if (account == null) return true;
            var values = account.LauncherWindowSettingsByPath ?? new Dictionary<string, string>();
            if (values.Count == 0 && !string.IsNullOrWhiteSpace(account.LauncherWindowSettings))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) values[MakeRelativePath(EpicConfigPath, ini)] = account.LauncherWindowSettings;
            }
            foreach (var kv in values)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                ApplyIniValuesAtomic(ini, "WindowSettings", new Dictionary<string, string> { { "MainLauncherWindow", kv.Value } });
            }
            return true;
        }

        private static void SetEmailSurgically(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            try
            {
                foreach (var ini in GetGameUserSettingsFiles())
                {
                    ApplyIniValuesAtomic(ini, "", new Dictionary<string, string> { { "LastCheckedEmail", email } });
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[INI] Failed to set email metadata", ex);
            }
        }

        private static IEnumerable<string> GetGameUserSettingsFiles()
        {
            if (!Directory.Exists(EpicConfigPath)) return new string[0];
            return Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
        }

        private static void ApplyRememberMeToIni(string ini, string data)
        {
            ApplyIniValuesAtomic(ini, "RememberMe", new Dictionary<string, string> { { "Data", data }, { "Enable", "True" } });
        }

        private static void ApplyIniValuesAtomic(string ini, string section, Dictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(ini) || values == null || values.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ini));
            var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
            foreach (var kv in values) SetIniValue(lines, section, kv.Key, kv.Value);
            AtomicWriteAllLines(ini, lines);
        }

        private static string GetIniValue(IList<string> lines, string section, string key)
        {
            var inSection = string.IsNullOrEmpty(section);
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                if (!string.IsNullOrEmpty(section))
                {
                    if (t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) { inSection = true; continue; }
                    if (inSection && t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)) break;
                }
                if (inSection && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return t.Substring(key.Length + 1).Trim();
            }
            return null;
        }

        private static void SetIniValue(List<string> lines, string section, string key, string value)
        {
            if (lines == null) return;
            var keyPrefix = key + "=";
            if (string.IsNullOrEmpty(section))
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = keyPrefix + value;
                        return;
                    }
                }
                lines.Add(keyPrefix + value);
                return;
            }
            var sectionHeader = "[" + section + "]";
            var sectionIndex = -1;
            var insertIndex = lines.Count;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    sectionIndex = i;
                    insertIndex = i + 1;
                    continue;
                }
                if (sectionIndex != -1 && t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    insertIndex = i;
                    break;
                }
                if (sectionIndex != -1)
                {
                    insertIndex = i + 1;
                    if (t.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = keyPrefix + value;
                        return;
                    }
                }
            }
            if (sectionIndex == -1)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.Add("");
                lines.Add(sectionHeader);
                lines.Add(keyPrefix + value);
            }
            else
            {
                lines.Insert(insertIndex, keyPrefix + value);
            }
        }

        private static bool VerifyRememberMeApplied(Account account)
        {
            if (account == null) return true;
            var expected = account.RememberMeDataByPath ?? new Dictionary<string, string>();
            if (expected.Count == 0 && !string.IsNullOrWhiteSpace(account.RememberMeData))
            {
                expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ini in GetGameUserSettingsFiles()) expected[MakeRelativePath(EpicConfigPath, ini)] = account.RememberMeData;
            }
            if (expected.Count == 0) return true;
            foreach (var kv in expected)
            {
                var ini = Path.Combine(EpicConfigPath, kv.Key);
                if (!File.Exists(ini))
                {
                    Globals.WriteToLog("[VERIFY] RememberMe target missing: " + ini);
                    return false;
                }
                var lines = File.ReadAllLines(ini);
                var data = GetIniValue(lines, "RememberMe", "Data");
                var enable = GetIniValue(lines, "RememberMe", "Enable");
                if (!string.Equals(data, kv.Value, StringComparison.Ordinal) || !string.Equals(enable, "True", StringComparison.OrdinalIgnoreCase))
                {
                    Globals.WriteToLog("[VERIFY] RememberMe mismatch in " + ini);
                    return false;
                }
            }
            return true;
        }

        public static string GetCurrentAccountId()
        {
            var registryId = TryGetCurrentAccountIdFromRegistry();
            if (!string.IsNullOrWhiteSpace(registryId)) return registryId;

            var authStateId = TryGetCurrentAccountIdFromLiveAuthState();
            if (!string.IsNullOrWhiteSpace(authStateId)) return authStateId;

            return null;
        }

        private static string TryGetCurrentAccountIdFromRegistry()
        {
            try
            {
                string id;
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", out id)) return NormalizeRegistryIdentityValue(id);
                if (Globals.TryReadRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId", out id)) return NormalizeRegistryIdentityValue(id);
            }
            catch { }
            return null;
        }

        private static string NormalizeRegistryIdentityValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.StartsWith("sz:", StringComparison.OrdinalIgnoreCase)) v = v.Substring(3);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static string TryGetCurrentAccountIdFromLiveAuthState()
        {
            try
            {
                var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var authPath in AuthFiles.Select(f => Path.Combine(EpicSavedPath, f)).Where(File.Exists))
                {
                    try
                    {
                        var json = Globals.ReadAllText(authPath);
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        var token = JsonConvert.DeserializeObject<JToken>(json);
                        CollectAuthFacts(token, new List<DateTime>(), accountIds);
                    }
                    catch { }
                }

                if (accountIds.Count == 1) return accountIds.First();

                var dataPath = Path.Combine(EpicSavedPath, "Data");
                if (!Directory.Exists(dataPath)) return null;

                var binaryIds = Directory.GetFiles(dataPath, "OC_*.dat", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.StartsWith("OC_", StringComparison.OrdinalIgnoreCase) && n.Length > 3)
                    .Select(n => n.Substring(3))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return binaryIds.Count == 1 ? binaryIds[0] : null;
            }
            catch { return null; }
        }

        private static string GetShortId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        private static async Task<bool> EnsureEpicIsClosed(int timeoutMs = 60000)
        {
            var sw = Stopwatch.StartNew();
            var epicPaths = GetEpicInstallPaths();
            Globals.WriteToLog("[KILL] Starting Epic process cleanup...");
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var active = GetEpicProcesses(epicPaths);
                if (active.Count == 0)
                {
                    if (VerifyWritableStateRoots())
                    {
                        await Task.Delay(2000);
                        return true;
                    }
                    await Task.Delay(1000);
                    continue;
                }
                foreach (var p in active)
                {
                    try
                    {
                        var pid = p.Id;
                        var name = p.ProcessName;
                        var psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid) { CreateNoWindow = true, UseShellExecute = false };
                        Process.Start(psi)?.WaitForExit(5000);
                        if (name.IndexOf("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var psi2 = new ProcessStartInfo("taskkill", "/F /IM EpicGamesLauncher.exe") { CreateNoWindow = true, UseShellExecute = false };
                            Process.Start(psi2)?.WaitForExit(5000);
                        }
                        Globals.WriteToLog("[KILL] Terminated: " + name + " (" + pid + ")");
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                try { Process.Start(new ProcessStartInfo("taskkill", "/F /IM EpicGamesLauncher.exe /IM EpicWebHelper.exe /IM EOSUserHelper.exe") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000); } catch { }
                await Task.Delay(1000);
            }
            return false;
        }

        private static List<string> GetEpicInstallPaths()
        {
            var result = new List<string>();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EpicGamesLauncher"))
                {
                    var loc = key == null ? null : key.GetValue("InstallLocation");
                    if (loc != null) result.Add(loc.ToString().TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant());
                }
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EpicGamesLauncher"))
                {
                    var loc = key == null ? null : key.GetValue("InstallLocation");
                    if (loc != null) result.Add(loc.ToString().TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant());
                }
            }
            catch { }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<Process> GetEpicProcesses(List<string> epicPaths)
        {
            var active = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName.ToLowerInvariant();
                    if (n.Contains("epicgames") || n.Contains("epicwebhelper") || n.Contains("epiconlineservices") || n.Contains("eosuserhelper") || n.Contains("eosoverlay") || n.Contains("fortnite"))
                    {
                        active.Add(p);
                        continue;
                    }
                    var path = "";
                    try { if (p.MainModule != null) path = p.MainModule.FileName; } catch { }
                    var lower = string.IsNullOrWhiteSpace(path) ? "" : path.ToLowerInvariant();
                    if (epicPaths.Any(ep => lower.StartsWith(ep, StringComparison.OrdinalIgnoreCase))) active.Add(p);
                    else p.Dispose();
                }
                catch { try { p.Dispose(); } catch { } }
            }
            return active;
        }

        private static bool VerifyWritableStateRoots()
        {
            var roots = new List<string> { EpicSavedPath, Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher") };
            if (Directory.Exists(ProgramDataManifestsPath)) roots.Add(ProgramDataManifestsPath);
            foreach (var rel in EOSFoldersToCapture)
            {
                roots.Add(Path.Combine(LocalAppData, "Epic Games", rel));
                roots.Add(Path.Combine(LocalAppData, rel));
            }
            var ok = true;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                ok = ok && ProbeDirectoryWritable(root);
            }
            return ok;
        }

        private static bool ProbeDirectoryWritable(string root)
        {
            try
            {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".epic_switcher_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                return true;
            }
            catch { return false; }
        }

        private static async Task<bool> SafeDeleteDirectoryAsync(string path, int retries = 10)
        {
            if (!Directory.Exists(path)) return true;
            await Globals.RetryActionAsync(() => { Globals.ForceDeleteDirectory(path); return Task.FromResult(0); }, retries, 500, true);
            return !Directory.Exists(path);
        }

        private static async Task<bool> SafeReplaceDirectoryAsync(string source, string target)
        {
            if (!Directory.Exists(source)) return true;
            var tmp = target + "_t" + GetShortId();
            await SafeDeleteDirectoryAsync(tmp);
            await CopyDirectoryStrictAsync(source, tmp);
            await SafeDeleteDirectoryAsync(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Directory.Move(tmp, target);
            return true;
        }

        private static async Task SafeReplaceFileAsync(string source, string target)
        {
            if (!File.Exists(source)) return;
            await Globals.RetryActionAsync(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var tmp = target + ".t" + GetShortId();
                File.Copy(source, tmp, true);
                AtomicReplaceFile(tmp, target);
                return Task.FromResult(0);
            }, 5, 500, true);
        }

        private static async Task CopyDirectoryStrictAsync(string source, string dest)
        {
            var success = await Globals.RetryAsync(async () =>
            {
                return await Globals.CopyFilesRecursiveAsync(source, dest, true);
            }, 5, 1000, true);
            if (!success) throw new IOException("Recursive copy failed from " + source + " to " + dest + ". [MAX_PATH? LOCK?]");
        }

        private static async Task CaptureSessionToFolder(string source, string dest)
        {
            await SafeDeleteDirectoryAsync(dest);
            Directory.CreateDirectory(dest);
            foreach (var f in AuthFiles.Concat(AuthBinaryFiles))
            {
                var s = Path.Combine(source, f);
                if (File.Exists(s)) await SafeReplaceFileAsync(s, Path.Combine(dest, f));
            }
            foreach (var item in SessionItemsToCapture)
            {
                var sPath = Path.Combine(source, item);
                var dPath = Path.Combine(dest, item);
                if (Directory.Exists(sPath)) await CopyDirectoryStrictAsync(sPath, dPath);
                else if (File.Exists(sPath)) await SafeReplaceFileAsync(sPath, dPath);
            }
            var eosDest = Path.Combine(dest, "EOS_Data");
            foreach (var rel in EOSFoldersToCapture)
            {
                var sPath = Path.Combine(LocalAppData, "Epic Games", rel);
                var isRoot = false;
                if (!Directory.Exists(sPath)) { sPath = Path.Combine(LocalAppData, rel); isRoot = true; }
                if (Directory.Exists(sPath))
                {
                    var dPath = Path.Combine(eosDest, rel);
                    await CopyDirectoryStrictAsync(sPath, dPath);
                    if (isRoot) File.WriteAllText(dPath + ".is_root", "");
                }
            }
            var rDest = Path.Combine(dest, "Roaming_Data");
            var rSrc = Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher");
            if (Directory.Exists(rSrc)) await CopyDirectoryStrictAsync(rSrc, Path.Combine(rDest, "EpicGamesLauncher"));
            if (Directory.Exists(ProgramDataManifestsPath)) await CopyDirectoryStrictAsync(ProgramDataManifestsPath, Path.Combine(dest, "ProgramData_Data", "Manifests"));
        }

        public static async Task<Tuple<bool, string>> CaptureCurrentAccount(string nameOverride = null, string emailOverride = null, string imagePathOverride = null, string descriptionOverride = null, string passwordOverride = null, Action<string> progress = null, Func<string, Task<bool>> confirmOverwrite = null, bool forceOverwrite = false)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                _lastDetected = new Tuple<string, string, string>(null, "N/A", "N/A");
                var det = await GetDetectedMetadataAsync();
                if (string.IsNullOrWhiteSpace(det.Item1)) return new Tuple<bool, string>(false, "No Epic Games account is currently logged in.");
                progress?.Invoke("Closing Epic Games...");
                if (!await EnsureEpicIsClosed()) return new Tuple<bool, string>(false, "Could not close all Epic Games processes. Please close the Launcher manually and try again.");
                var liveValidation = ValidateSession(EpicSavedPath, det.Item1);
                if (!liveValidation.IsValid) return new Tuple<bool, string>(false, "Current session is invalid: " + liveValidation.Message);
                var accounts = GetAccounts();
                var existing = accounts.FirstOrDefault(a => a.Id == det.Item1);
                if (existing != null && !forceOverwrite)
                {
                    if (confirmOverwrite == null || !await confirmOverwrite(existing.Name)) return new Tuple<bool, string>(false, "Capture cancelled by user.");
                }
                var acc = existing ?? new Account(det.Item1, nameOverride ?? (det.Item2 == "N/A" ? "Epic Account" : det.Item2));
                if (!string.IsNullOrWhiteSpace(nameOverride)) acc.Name = nameOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(det.Item2) && det.Item2 != "N/A") acc.Name = det.Item2;
                if (emailOverride != null) acc.Email = emailOverride.Trim();
                else if (!string.IsNullOrWhiteSpace(det.Item3) && det.Item3 != "N/A") acc.Email = det.Item3;
                if (descriptionOverride != null) acc.Description = descriptionOverride;
                if (passwordOverride != null) acc.Password = passwordOverride;
                if (!string.IsNullOrWhiteSpace(imagePathOverride)) acc.ProfileImagePath = imagePathOverride;
                acc.ProfileImagePath = NormalizeProfileImagePath(acc.ProfileImagePath);
                acc.AccentColor = GetDeterministicColor(acc.Name ?? acc.Id);
                acc.LastPlayed = DateTime.Now;
                progress?.Invoke("Capturing session...");
                var target = Path.Combine(BaseDataPath, acc.Id);
                var tmp = target + "_n" + GetShortId();
                await CaptureSessionToFolder(EpicSavedPath, tmp);
                var capturedValidation = ValidateSession(tmp, acc.Id);
                if (!capturedValidation.IsValid) { await SafeDeleteDirectoryAsync(tmp); return new Tuple<bool, string>(false, "Failed to verify captured session: " + capturedValidation.Message); }
                acc.RegistryData = CaptureRegistryData();
                acc.Credentials = CredentialService.CaptureCredentials();
                acc.RememberMeDataByPath = GetRememberMeDataByPath();
                acc.RememberMeData = acc.RememberMeDataByPath.Values.FirstOrDefault();
                acc.LauncherWindowSettingsByPath = GetLauncherWindowSettingsByPath();
                acc.LauncherWindowSettings = acc.LauncherWindowSettingsByPath.Values.FirstOrDefault();
                if (Directory.Exists(target)) await SafeDeleteDirectoryAsync(target);
                Directory.Move(tmp, target);
                if (existing == null) accounts.Add(acc);
                SaveAccounts(accounts);
                return new Tuple<bool, string>(true, null);
            }
            catch (Exception ex) { Globals.WriteToLog("[CAPTURE] Fatal error", ex); return new Tuple<bool, string>(false, "An unexpected error occurred: " + ex.Message); }
            finally { SessionOperationLock.Release(); }
        }

        private static async Task RefreshSnapshotOfCurrentSavedAccount(List<Account> accounts, string switchingToAccountId, string currentAccountIdHint, Action<string> progress)
        {
            var curId = string.IsNullOrWhiteSpace(currentAccountIdHint) ? GetCurrentAccountId() : currentAccountIdHint;
            if (string.IsNullOrWhiteSpace(curId)) curId = accounts.FirstOrDefault(a => a.IsActive)?.Id;
            if (string.IsNullOrWhiteSpace(curId) || string.Equals(curId, switchingToAccountId, StringComparison.OrdinalIgnoreCase)) return;
            var acc = accounts.FirstOrDefault(a => string.Equals(a.Id, curId, StringComparison.OrdinalIgnoreCase));
            if (acc == null) return;
            var liveValidation = ValidateSession(EpicSavedPath, curId);
            if (!liveValidation.IsValid) return;
            progress?.Invoke("Saving current session...");
            acc.RegistryData = CaptureRegistryData();
            acc.Credentials = CredentialService.CaptureCredentials();
            acc.RememberMeDataByPath = GetRememberMeDataByPath();
            acc.RememberMeData = acc.RememberMeDataByPath.Values.FirstOrDefault();
            acc.LauncherWindowSettingsByPath = GetLauncherWindowSettingsByPath();
            acc.LauncherWindowSettings = acc.LauncherWindowSettingsByPath.Values.FirstOrDefault();
            acc.LastPlayed = DateTime.Now;
            var target = Path.Combine(BaseDataPath, acc.Id);
            var tmp = target + "_r" + GetShortId();
            await CaptureSessionToFolder(EpicSavedPath, tmp);
            var capturedValidation = ValidateSession(tmp, acc.Id);
            if (!capturedValidation.IsValid) { await SafeDeleteDirectoryAsync(tmp); return; }
            if (Directory.Exists(target)) await SafeDeleteDirectoryAsync(target);
            Directory.Move(tmp, target);
            SaveAccounts(accounts);
        }

        private static void MarkActiveAccount(List<Account> accounts, string activeId)
        {
            foreach (var acc in accounts) acc.IsActive = string.Equals(acc.Id, activeId, StringComparison.OrdinalIgnoreCase);
        }

        public static void PrepareLauncherForInitialSync(int maxWaitMs = 8000)
        {
            try
            {
                var waitMs = Math.Max(0, maxWaitMs);
                var beforeId = GetCurrentAccountId();
                var launcherStarted = AutoLaunchEpicLauncher(false);

                if (waitMs == 0 || (!launcherStarted && string.IsNullOrWhiteSpace(beforeId))) return;

                var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                var shortDeadline = DateTime.UtcNow.AddMilliseconds(Math.Min(waitMs, 3000));
                while (DateTime.UtcNow < deadline)
                {
                    var launcherRunning = Globals.IsProcessRunning("EpicGamesLauncher");
                    var currentId = GetCurrentAccountId();
                    if (launcherRunning && !string.IsNullOrWhiteSpace(currentId)) break;
                    if (launcherRunning && !string.IsNullOrWhiteSpace(beforeId) && DateTime.UtcNow >= shortDeadline) break;
                    Thread.Sleep(500);
                }

                Globals.WriteToLog("[SYNC] Initial launcher sync. Before=" + (string.IsNullOrWhiteSpace(beforeId) ? "NULL" : beforeId) + "; After=" + (GetCurrentAccountId() ?? "NULL"));
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[SYNC] Initial launcher sync failed", ex);
            }
        }

        public static bool AutoLaunchEpicLauncher(bool minimized = true)
        {
            if (Globals.IsProcessRunning("EpicGamesLauncher")) return true;
            try
            {
                var exe = ResolveEpicLauncherExecutable();
                if (!string.IsNullOrEmpty(exe))
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, Arguments = minimized ? "-minimized -silent" : "", UseShellExecute = true });
                    return true;
                }
                Process.Start(new ProcessStartInfo { FileName = "com.epicgames.launcher://", UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { Globals.WriteToLog("[LAUNCHER] Failed to launch Epic Games Launcher", ex); return false; }
        }

        private static string ResolveEpicLauncherExecutable()
        {
            try
            {
                var installRoots = GetEpicInstallPaths();
                installRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"));
                installRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"));

                foreach (var root in installRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidates = new[]
                    {
                        Path.Combine(root, "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
                        Path.Combine(root, "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"),
                        Path.Combine(root, "EpicGamesLauncher.exe")
                    };
                    foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        public static async Task<bool> WaitForLaunchedAccountAsync(string expectedAccountId, Action<string> progress = null, int timeoutMs = 45000)
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId)) return false;

            var sw = Stopwatch.StartNew();
            DateTime? healthySince = null;
            var launchRequested = false;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Globals.IsProcessRunning("EpicGamesLauncher"))
                {
                    if (!launchRequested)
                    {
                        progress?.Invoke("Launching Epic Games...");
                        AutoLaunchEpicLauncher(false);
                        launchRequested = true;
                    }
                    healthySince = null;
                    await Task.Delay(500);
                    continue;
                }

                var currentId = GetCurrentAccountId();
                var session = ValidateSession(EpicSavedPath, expectedAccountId);
                if (string.Equals(currentId, expectedAccountId, StringComparison.OrdinalIgnoreCase) && session.IsValid)
                {
                    if (!healthySince.HasValue)
                    {
                        progress?.Invoke("Verifying Epic sign-in...");
                        healthySince = DateTime.UtcNow;
                    }

                    if (DateTime.UtcNow - healthySince.Value >= TimeSpan.FromSeconds(5))
                    {
                        Globals.WriteToLog("[VERIFY] Epic launcher is running with expected account: " + expectedAccountId);
                        return true;
                    }
                }
                else
                {
                    healthySince = null;
                }

                await Task.Delay(1000);
            }

            Globals.WriteToLog("[VERIFY] Timed out waiting for launched account. Expected=" + expectedAccountId + "; Current=" + (GetCurrentAccountId() ?? "NULL"));
            return false;
        }

        public static async Task<bool> SwitchToAccount(string accId, Action<string> progress = null)
        {
            await SessionOperationLock.WaitAsync();
            try
            {
                var accs = GetAccounts();
                var t = accs.FirstOrDefault(a => string.Equals(a.Id, accId, StringComparison.OrdinalIgnoreCase));
                if (t == null) return false;
                NormalizeAccount(t);
                var src = Path.Combine(BaseDataPath, t.Id);
                if (!Directory.Exists(src)) return false;
                var sourceValidation = ValidateSession(src, t.Id);
                if (!sourceValidation.IsValid) return false;
                var curB = GetCurrentAccountId();
                progress?.Invoke("Closing Epic Games...");
                if (!await EnsureEpicIsClosed()) return false;
                await RefreshSnapshotOfCurrentSavedAccount(accs, t.Id, curB, progress);
                var rbB = Path.Combine(BaseDataPath, "_switch_rb");
                var tmpB = Path.Combine(BaseDataPath, "_tmp_res" + GetShortId());
                var registryRollback = CaptureRegistryData();
                var credentialRollback = CredentialService.CaptureCredentials();
                try
                {
                    progress?.Invoke("Preparing session...");
                    if (Directory.Exists(rbB)) await SafeDeleteDirectoryAsync(rbB);
                    Directory.CreateDirectory(rbB);
                    Directory.CreateDirectory(tmpB);
                    var tS = Path.Combine(tmpB, "Saved");
                    Directory.CreateDirectory(tS);
                    foreach (var item in SessionItemsToCapture)
                    {
                        var s = Path.Combine(src, item);
                        var d = Path.Combine(tS, item);
                        if (Directory.Exists(s)) await CopyDirectoryStrictAsync(s, d);
                        else if (File.Exists(s)) await SafeReplaceFileAsync(s, d);
                    }
                    foreach (var f in AuthFiles.Concat(AuthBinaryFiles))
                    {
                        var sf = Path.Combine(src, f);
                        if (File.Exists(sf)) await SafeReplaceFileAsync(sf, Path.Combine(tS, f));
                    }
                    var tE = Path.Combine(tmpB, "EOS_Data");
                    var sE = Path.Combine(src, "EOS_Data");
                    if (Directory.Exists(sE)) await CopyDirectoryStrictAsync(sE, tE);
                    var tR = Path.Combine(tmpB, "Roaming_Data");
                    var sR = Path.Combine(src, "Roaming_Data");
                    if (Directory.Exists(sR)) await CopyDirectoryStrictAsync(sR, tR);
                    var tP = Path.Combine(tmpB, "ProgramData_Data", "Manifests");
                    var sP = Path.Combine(src, "ProgramData_Data", "Manifests");
                    if (Directory.Exists(sP)) await CopyDirectoryStrictAsync(sP, tP);
                    var preparedValidation = ValidateSession(tS, t.Id);
                    if (!preparedValidation.IsValid) throw new InvalidDataException(preparedValidation.Message);
                    await MovePathToRollback(EpicSavedPath, rbB, "Saved");
                    foreach (var rel in EOSFoldersToCapture)
                    {
                        await MovePathToRollback(Path.Combine(LocalAppData, "Epic Games", rel), rbB, "EOS_" + rel.Replace("\\", "_"));
                        await MovePathToRollback(Path.Combine(LocalAppData, rel), rbB, "EOS_R_" + rel.Replace("\\", "_"));
                    }
                    await MovePathToRollback(Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher"), rbB, "Roaming");
                    if (Directory.Exists(tP)) await MovePathToRollback(ProgramDataManifestsPath, rbB, "PD_Manifests");
                    Directory.CreateDirectory(Path.GetDirectoryName(EpicSavedPath));
                    Directory.Move(tS, EpicSavedPath);
                    if (Directory.Exists(tE)) RestoreEOSData(tE);
                    if (Directory.Exists(tR)) RestoreRoamingData(tR);
                    if (Directory.Exists(tP)) { Directory.CreateDirectory(Path.GetDirectoryName(ProgramDataManifestsPath)); Directory.Move(tP, ProgramDataManifestsPath); }
                    RestoreRegistryIdentity(t);
                    CredentialService.DeleteCredentials();
                    if (t.Credentials != null && t.Credentials.Count > 0) CredentialService.RestoreCredentials(t.Credentials);
                    SetEmailSurgically(t.Email);
                    SetRememberMeDataSurgically(t);
                    SetLauncherWindowSettingsSurgically(t);
                    var liveValidation = ValidateRestoredState(t);
                    if (!liveValidation.IsValid) throw new InvalidDataException(liveValidation.Message);
                    t.LastPlayed = DateTime.Now;
                    MarkActiveAccount(accs, t.Id);
                    SaveAccounts(accs);
                    await SafeDeleteDirectoryAsync(rbB);
                    await SafeDeleteDirectoryAsync(tmpB);
                    progress?.Invoke("Launching Epic Games...");
                    AutoLaunchEpicLauncher(false);
                    return true;
                }
                catch
                {
                    await RollbackSwitchAsync(rbB);
                    RestoreRegistrySnapshot(registryRollback);
                    if (CredentialService.DeleteCredentials() && credentialRollback != null) CredentialService.RestoreCredentials(credentialRollback);
                    if (Directory.Exists(tmpB)) await SafeDeleteDirectoryAsync(tmpB);
                    return false;
                }
            }
            catch { return false; }
            finally { SessionOperationLock.Release(); }
        }

        private static async Task MovePathToRollback(string source, string rollbackRoot, string rollbackName)
        {
            if (!Directory.Exists(source) && !File.Exists(source)) return;
            var rb = Path.Combine(rollbackRoot, rollbackName);
            Directory.CreateDirectory(Path.GetDirectoryName(rb));
            if (Directory.Exists(rb)) await SafeDeleteDirectoryAsync(rb);
            if (File.Exists(rb)) File.Delete(rb);
            Directory.Move(source, rb);
        }

        private static void RestoreEOSData(string sourceRoot)
        {
            foreach (var rel in EOSFoldersToCapture)
            {
                var s = Path.Combine(sourceRoot, rel);
                if (!Directory.Exists(s)) continue;
                var dest = File.Exists(s + ".is_root") ? Path.Combine(LocalAppData, rel) : Path.Combine(LocalAppData, "Epic Games", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Directory.Move(s, dest);
            }
        }

        private static void RestoreRoamingData(string sourceRoot)
        {
            foreach (var folder in Directory.GetDirectories(sourceRoot))
            {
                var dest = Path.Combine(RoamingAppData, "Epic Games", Path.GetFileName(folder));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Directory.Move(folder, dest);
            }
        }

        private static async Task RollbackSwitchAsync(string rollbackRoot)
        {
            try
            {
                await RestoreRollbackPath(rollbackRoot, "Saved", EpicSavedPath);
                foreach (var rel in EOSFoldersToCapture)
                {
                    await RestoreRollbackPath(rollbackRoot, "EOS_" + rel.Replace("\\", "_"), Path.Combine(LocalAppData, "Epic Games", rel));
                    await RestoreRollbackPath(rollbackRoot, "EOS_R_" + rel.Replace("\\", "_"), Path.Combine(LocalAppData, rel));
                }
                await RestoreRollbackPath(rollbackRoot, "Roaming", Path.Combine(RoamingAppData, "Epic Games", "EpicGamesLauncher"));
                await RestoreRollbackPath(rollbackRoot, "PD_Manifests", ProgramDataManifestsPath);
            }
            catch { }
        }

        private static async Task RestoreRollbackPath(string rollbackRoot, string name, string target)
        {
            var s = Path.Combine(rollbackRoot, name);
            if (!Directory.Exists(s) && !File.Exists(s)) return;
            if (Directory.Exists(target)) await SafeDeleteDirectoryAsync(target);
            else if (File.Exists(target)) File.Delete(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Directory.Move(s, target);
        }

        private static void RestoreRegistryIdentity(Account target)
        {
            ClearRegistryIdentity(CaptureRegistryData());
            if (target.RegistryData != null)
            {
                foreach (var kv in target.RegistryData) Globals.SetRegistryKey(kv.Key, kv.Value);
            }
            if (!string.IsNullOrWhiteSpace(target.Id))
            {
                Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", "sz:" + target.Id);
                if (target.RegistryData == null || !target.RegistryData.Keys.Any(k => k.EndsWith(":EpicAccountId", StringComparison.OrdinalIgnoreCase)))
                    Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId", "sz:" + target.Id);
            }
        }

        private static void RestoreRegistrySnapshot(Dictionary<string, string> snapshot)
        {
            ClearRegistryIdentity(CaptureRegistryData());
            if (snapshot == null) return;
            foreach (var kv in snapshot) Globals.SetRegistryKey(kv.Key, kv.Value);
        }

        private static void ClearRegistryIdentity(Dictionary<string, string> current)
        {
            current = current ?? new Dictionary<string, string>();
            foreach (var key in current.Keys.Concat(RegistryKeysToSwap).Distinct(StringComparer.OrdinalIgnoreCase)) Globals.DeleteRegistryValue(key);
        }

        private static bool VerifyRegistryIdentity(string expectedAccountId)
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId)) return false;
            var current = GetCurrentAccountId();
            return string.Equals(current, expectedAccountId, StringComparison.OrdinalIgnoreCase);
        }

        private static SessionValidationResult ValidateRestoredState(Account target)
        {
            var live = ValidateSession(EpicSavedPath, target.Id);
            if (!live.IsValid) return live;
            if (!VerifyRegistryIdentity(target.Id)) return new SessionValidationResult { IsValid = false, Message = "Restored registry identity does not match target account." };
            if (!VerifyRememberMeApplied(target)) return new SessionValidationResult { IsValid = false, Message = "Restored RememberMe state does not match target account." };
            var restoredAuthFiles = AuthFiles.Select(f => Path.Combine(EpicSavedPath, f)).Where(File.Exists).ToList();
            var hasModernBinary = File.Exists(Path.Combine(EpicSavedPath, "Data", "OC_" + target.Id + ".dat"));
            if (restoredAuthFiles.Count == 0 && !hasModernBinary) return new SessionValidationResult { IsValid = false, Message = "No restored auth file exists." };
            live.IsValid = true;
            return live;
        }

        private static void AtomicWriteAllLines(string path, IList<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            File.WriteAllLines(tmp, lines.ToArray());
            AtomicReplaceFile(tmp, path);
        }

        private static void AtomicReplaceFile(string tmp, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
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

        private static Tuple<string, string, string> _lastDetected;

        public static async Task<Tuple<string, string, string>> GetDetectedMetadataAsync()
        {
            return await Task.Run(() =>
            {
                var id = GetCurrentAccountId();
                if (!string.IsNullOrEmpty(id) && _lastDetected != null && _lastDetected.Item1 == id && _lastDetected.Item2 != "N/A") return _lastDetected;
                var name = "N/A";
                var email = "N/A";
                try
                {
                    if (Directory.Exists(EpicConfigPath))
                    {
                        var files = Directory.GetFiles(EpicConfigPath, "GameUserSettings.ini", SearchOption.AllDirectories);
                        var ini = files.FirstOrDefault(f => f.IndexOf("WindowsEditor", StringComparison.OrdinalIgnoreCase) >= 0) ?? files.FirstOrDefault();
                        if (ini != null)
                        {
                            var lines = File.ReadAllLines(ini);
                            foreach (var raw in lines)
                            {
                                var line = raw.Trim();
                                if (line.StartsWith("LastCheckedEmail=", StringComparison.OrdinalIgnoreCase)) { var v = line.Substring("LastCheckedEmail=".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) email = v; }
                                else if (line.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase)) { var v = line.Substring("DisplayName=".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; }
                            }
                        }
                    }
                    if (name == "N/A" || email == "N/A")
                    {
                        var logsPath = Path.Combine(EpicSavedPath, "Logs");
                        if (Directory.Exists(logsPath))
                        {
                            var logs = Directory.GetFiles(logsPath, "EpicGamesLauncher*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(2).ToList();
                            foreach (var f in logs)
                            {
                                try
                                {
                                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    using (var reader = new StreamReader(fs))
                                    {
                                        var q = new Queue<string>(1000);
                                        while (!reader.EndOfStream) { if (q.Count >= 1000) q.Dequeue(); q.Enqueue(reader.ReadLine() ?? ""); }
                                        foreach (var line in q.Reverse())
                                        {
                                            if (name == "N/A" && line.Contains("-epicusername=\""))
                                            {
                                                var k = "-epicusername=\"";
                                                var s = line.IndexOf(k, StringComparison.Ordinal);
                                                if (s >= 0) { s += k.Length; var e = line.IndexOf('"', s); if (e > s) { var v = line.Substring(s, e - s).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; } }
                                            }
                                            if (name == "N/A" && line.Contains("DisplayName: "))
                                            {
                                                var idx = line.IndexOf("DisplayName: ", StringComparison.Ordinal);
                                                if (idx >= 0) { var v = line.Substring(idx + "DisplayName: ".Length).Trim(); if (!string.IsNullOrWhiteSpace(v)) name = v; }
                                            }
                                            if (email == "N/A" && line.Contains("@")) { var m = Regex.Match(line, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"); if (m.Success) email = m.Value; }
                                            if (name != "N/A" && email != "N/A") break;
                                        }
                                    }
                                }
                                catch { }
                                if (name != "N/A" && email != "N/A") break;
                            }
                        }
                    }
                }
                catch { }
                _lastDetected = new Tuple<string, string, string>(id, name, email);
                return _lastDetected;
            });
        }

        public static Tuple<string, string, string> GetDetectedMetadata() { return Task.Run(async () => await GetDetectedMetadataAsync()).GetAwaiter().GetResult(); }

        public static async Task<Tuple<string, string, string>> WaitForDetectedMetadata(int sec = 120, int interval = 1500)
        {
            var end = DateTime.UtcNow.AddSeconds(sec);
            while (DateTime.UtcNow < end) { var det = GetDetectedMetadata(); if (!string.IsNullOrWhiteSpace(det.Item1)) return det; await Task.Delay(interval); }
            return new Tuple<string, string, string>(null, "N/A", "N/A");
        }

        public static void BackupAccounts(string zip, Action<string> log = null)
        {
            Directory.CreateDirectory(BaseDataPath);
            var tmp = Path.Combine(Path.GetTempPath(), "e_bak_" + GetShortId());
            try
            {
                Directory.CreateDirectory(tmp);
                var sPath = Path.Combine(Globals.UserDataFolder, "appsettings.json");
                if (File.Exists(sPath)) File.Copy(sPath, Path.Combine(tmp, "appsettings.json"), true);
                var accs = GetAccounts();
                foreach (var a in accs)
                {
                    var f = Path.Combine(BaseDataPath, a.Id);
                    if (Directory.Exists(f)) Globals.CopyFilesRecursive(f, Path.Combine(tmp, a.Id), true);
                }
                CopySkinDirectoryToBackup(Path.Combine(Globals.UserDataFolder, "Skin_Icons"), tmp);
                CopySkinDirectoryToBackup(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons"), tmp);
                CopySkinDirectoryToBackup(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Payload", "Skin_Icons"), tmp);

                var portableAccounts = CloneAccounts(accs);
                foreach (var account in portableAccounts) account.ProfileImagePath = MakePortableProfileImagePath(account, tmp, log);
                File.WriteAllText(Path.Combine(tmp, "accounts.json"), JsonConvert.SerializeObject(portableAccounts, Formatting.Indented));

                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zip));
                if (!string.IsNullOrWhiteSpace(zipDir)) Directory.CreateDirectory(zipDir);
                if (File.Exists(zip)) File.Delete(zip);

                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
                    {
                        string rel = file.Substring(tmp.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var entry = archive.CreateEntry(rel.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
                        using (var entryStream = entry.Open())
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            fs.CopyTo(entryStream);
                        }
                    }
                }
            }
            catch { throw; }
            finally { if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { } }
        }

        public static void RestoreAccounts(string zip)
        {
            if (!File.Exists(zip)) throw new FileNotFoundException("Backup file not found: " + zip);
            var tmp = Path.Combine(Path.GetTempPath(), "e_res_" + GetShortId());
            var rb = BaseDataPath + "_rb";
            try
            {
                if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { }
                Directory.CreateDirectory(tmp);

                try
                {
                    using (var archive = ZipFile.OpenRead(zip))
                    {
                        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                        {
                            string fullPath = GetSafeExtractionPath(tmp, entry.FullName);
                            string longPath = fullPath.StartsWith(@"\\") ? fullPath : @"\\?\" + fullPath;

                            Directory.CreateDirectory(Path.GetDirectoryName(longPath));
                            using (var stream = entry.Open())
                            using (var fs = new FileStream(longPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                        }
                    }
                }
                catch (Exception ex) { throw new IOException("Failed to extract backup safely. [LONG_PATH?] Error: " + ex.Message, ex); }

                var aJson = Path.Combine(tmp, "accounts.json");
                if (!File.Exists(aJson)) throw new InvalidDataException("Backup is invalid: accounts.json is missing.");

                RestoreBackupSkins(tmp);

                var accs = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(aJson)) ?? new List<Account>();
                foreach (var a in accs)
                {
                    a.ProfileImagePath = ResolveRestoredProfileImagePath(a.ProfileImagePath, tmp);
                    NormalizeAccount(a);
                }

                if (Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
                if (Directory.Exists(BaseDataPath)) Directory.Move(BaseDataPath, rb);

                try
                {
                    Directory.CreateDirectory(BaseDataPath);
                    Globals.CopyFilesRecursive(tmp, BaseDataPath, true);

                    var sRestored = Path.Combine(tmp, "appsettings.json");
                    if (File.Exists(sRestored)) { File.Copy(sRestored, Path.Combine(Globals.UserDataFolder, "appsettings.json"), true); AppSettings.LoadSettings(); }

                    RestoreBackupSkins(tmp);
                    SaveAccounts(accs);
                }
                catch
                {
                    if (Directory.Exists(BaseDataPath)) try { Globals.ForceDeleteDirectory(BaseDataPath); } catch { }
                    if (Directory.Exists(rb)) Directory.Move(rb, BaseDataPath);
                    throw;
                }
                if (Directory.Exists(rb)) try { Globals.ForceDeleteDirectory(rb); } catch { }
            }
            finally { if (Directory.Exists(tmp)) try { Globals.ForceDeleteDirectory(tmp); } catch { } }
        }

        private static List<Account> CloneAccounts(List<Account> accounts)
        {
            var clones = new List<Account>();
            foreach (var account in accounts ?? new List<Account>())
            {
                var clone = new Account(account.Id, account.Name)
                {
                    Email = account.Email,
                    Description = account.Description,
                    ProfileImagePath = account.ProfileImagePath,
                    AccentColor = account.AccentColor,
                    IsActive = account.IsActive,
                    RememberMeData = account.RememberMeData,
                    RememberMeDataByPath = account.RememberMeDataByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RememberMeDataByPath),
                    LauncherWindowSettings = account.LauncherWindowSettings,
                    LauncherWindowSettingsByPath = account.LauncherWindowSettingsByPath == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.LauncherWindowSettingsByPath),
                    Credentials = account.Credentials == null ? new List<CredentialService.SavedCredential>() : account.Credentials.Select(c => new CredentialService.SavedCredential
                    {
                        TargetName = c.TargetName,
                        UserName = c.UserName,
                        Blob = c.Blob == null ? null : c.Blob.ToArray(),
                        Persist = c.Persist,
                        Type = c.Type
                    }).ToList(),
                    RegistryData = account.RegistryData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(account.RegistryData),
                    EncryptedPassword = account.EncryptedPassword,
                    LastPlayed = account.LastPlayed
                };
                clones.Add(clone);
            }

            return clones;
        }

        private static void CopySkinDirectoryToBackup(string source, string backupRoot)
        {
            try
            {
                if (!Directory.Exists(source)) return;
                Globals.CopyFilesRecursive(source, Path.Combine(backupRoot, "Skin_Icons"), true);
            }
            catch { }
        }

        private static string MakePortableProfileImagePath(Account account, string backupRoot, Action<string> log)
        {
            var imagePath = account == null ? null : account.ProfileImagePath;
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;

            try
            {
                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;

                if (File.Exists(imagePath))
                {
                    var safeId = MakeSafeFileName(string.IsNullOrWhiteSpace(account.Id) ? account.Name : account.Id);
                    var relative = Path.Combine("Skin_Icons", "Selected", safeId + "_" + fileName);
                    var target = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(imagePath, target, true);
                    return relative;
                }

                if (imagePath.IndexOf("Skin_Icons", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Path.Combine("Skin_Icons", fileName);
            }
            catch (Exception ex)
            {
                log?.Invoke("WARNING: Failed to preserve selected profile image for " + (account == null ? "unknown" : account.Name) + ": " + ex.Message);
            }

            return imagePath;
        }

        private static void RestoreBackupSkins(string extractedRoot)
        {
            try
            {
                var bSkins = Path.Combine(extractedRoot, "Skin_Icons");
                if (!Directory.Exists(bSkins)) return;

                var targetSkins = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                Directory.CreateDirectory(targetSkins);
                Globals.CopyFilesRecursive(bSkins, targetSkins, true);
            }
            catch { }
        }

        private static string ResolveRestoredProfileImagePath(string imagePath, string extractedRoot)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return imagePath;

            try
            {
                if (File.Exists(imagePath)) return Path.GetFullPath(imagePath);

                if (!Path.IsPathRooted(imagePath))
                {
                    var restoredRelative = Path.Combine(Globals.UserDataFolder, imagePath);
                    if (File.Exists(restoredRelative)) return Path.GetFullPath(restoredRelative);

                    var extractedRelative = Path.Combine(extractedRoot, imagePath);
                    if (File.Exists(extractedRelative)) return Path.GetFullPath(extractedRelative);
                }

                var fileName = Path.GetFileName(imagePath);
                if (string.IsNullOrWhiteSpace(fileName)) return imagePath;

                var restoredSkinRoot = Path.Combine(Globals.UserDataFolder, "Skin_Icons");
                if (Directory.Exists(restoredSkinRoot))
                {
                    var restoredMatch = Directory.GetFiles(restoredSkinRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(restoredMatch)) return Path.GetFullPath(restoredMatch);
                }

                var extractedSkinRoot = Path.Combine(extractedRoot, "Skin_Icons");
                if (Directory.Exists(extractedSkinRoot))
                {
                    var extractedMatch = Directory.GetFiles(extractedSkinRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(extractedMatch)) return Path.GetFullPath(extractedMatch);
                }
            }
            catch { }

            return imagePath;
        }

        private static string MakeSafeFileName(string value)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? "account" : value;
            foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return safe;
        }

        private static string GetSafeExtractionPath(string extractionRoot, string entryKey)
        {
            if (string.IsNullOrWhiteSpace(entryKey)) throw new InvalidDataException("Backup contains an empty entry name.");

            var root = Path.GetFullPath(extractionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entryPath = entryKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, entryPath));

            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path: " + entryKey);

            return fullPath;
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

        private static Dictionary<string, string> CaptureRegistryData()
        {
            var data = new Dictionary<string, string>();
            foreach (var r in RegistryRootsToCapture) CaptureRegistryRecursive(r, data);
            return data;
        }

        private static void CaptureRegistryRecursive(string root, Dictionary<string, string> data)
        {
            try
            {
                var exp = ExplodeRegistryKeyForCapture(root + ":D");
                var rKey = exp.Item1;
                var p = exp.Item2;
                if (rKey == null) return;
                using (var key = rKey.OpenSubKey(p))
                {
                    if (key == null) return;
                    foreach (var vName in key.GetValueNames())
                    {
                        string full = root + ":" + vName;
                        string val;
                        if (Globals.TryReadRegistryKey(full, out val)) data[full] = val;
                    }
                    foreach (var sKey in key.GetSubKeyNames()) CaptureRegistryRecursive(root + "\\" + sKey, data);
                }
            }
            catch { }
        }

        private static Tuple<RegistryKey, string, string> ExplodeRegistryKeyForCapture(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.Length < 5) return new Tuple<RegistryKey, string, string>(null, null, null);
                var rKey = path.StartsWith("HKCU") ? Microsoft.Win32.Registry.CurrentUser : (path.StartsWith("HKLM") ? Microsoft.Win32.Registry.LocalMachine : null);
                if (rKey == null) return new Tuple<RegistryKey, string, string>(null, null, null);
                var rem = path.Substring(5);
                var parts = rem.Split(':');
                if (parts.Length < 2) return new Tuple<RegistryKey, string, string>(null, null, null);
                return new Tuple<RegistryKey, string, string>(rKey, parts[0], parts[1]);
            }
            catch { return new Tuple<RegistryKey, string, string>(null, null, null); }
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

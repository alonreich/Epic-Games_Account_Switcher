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
    /// <summary>Session validation, launcher INI values and current-account detection.</summary>
    public partial class AccountService
    {
        private sealed class SessionValidationResult
        {
            public bool IsValid { get; set; }
            public string Message { get; set; }
            public string AccountId { get; set; }
            public DateTime? ExpiresUtc { get; set; }
        }

        private sealed class ModernAuthBlobInfo
        {
            public string AccountId { get; set; }
            public string Path { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public DateTime FreshUntilUtc { get; set; }
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
                var modernBlob = SelectModernAuthBlob(folder, accountId);
                var hasModernBinary = modernBlob != null;

                if (!hasLegacyJson && !hasModernBinary)
                {
                    result.Message = "Authentication state file is missing. (Checked legacy JSON and modern binary blobs for ID: " + (accountId ?? "NULL") + ") [v1.0.1]";
                    return result;
                }

                if (hasModernBinary)
                {
                    if (modernBlob.FreshUntilUtc <= DateTime.UtcNow.AddMinutes(5))
                    {
                        result.Message = "Modern auth blob is stale. Last write: " + modernBlob.LastWriteUtc.ToString("o") + "; freshness window ended: " + modernBlob.FreshUntilUtc.ToString("o") + " [v1.0.1]";
                        return result;
                    }
                    result.AccountId = modernBlob.AccountId;
                    result.ExpiresUtc = modernBlob.FreshUntilUtc;
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
                    if (hasModernBinary)
                    {
                        result.IsValid = true;
                        result.AccountId = modernBlob.AccountId;
                        result.Message = "Modern binary auth blob is fresh until " + modernBlob.FreshUntilUtc.ToString("o") + " [v1.0.1]";
                        return result;
                    }
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
                if (hasModernBinary && !string.Equals(modernBlob.AccountId, expectedAccountId ?? modernBlob.AccountId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "Modern auth blob account id mismatch. Expected " + expectedAccountId + ". Found " + modernBlob.AccountId + " [v1.0.1]";
                    return result;
                }
                result.IsValid = true;
                result.AccountId = allAccountIds.FirstOrDefault() ?? (hasModernBinary ? modernBlob.AccountId : null);
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

        private static ModernAuthBlobInfo SelectModernAuthBlob(string folder, string expectedAccountId)
        {
            try
            {
                var dataPath = Path.Combine(folder, "Data");
                if (!Directory.Exists(dataPath)) return null;

                var maxAgeDays = Math.Max(1, AppSettings.Instance.ModernAuthBlobMaxAgeDays);
                var blobs = Directory.GetFiles(dataPath, "OC_*.dat", SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        var id = Path.GetFileNameWithoutExtension(path);
                        if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("OC_", StringComparison.OrdinalIgnoreCase)) id = id.Substring(3);
                        if (string.IsNullOrWhiteSpace(id)) return null;

                        var lastWrite = File.GetLastWriteTimeUtc(path);
                        if (lastWrite <= DateTime.MinValue.AddYears(1)) lastWrite = File.GetCreationTimeUtc(path);

                        return new ModernAuthBlobInfo
                        {
                            AccountId = id.Trim(),
                            Path = path,
                            LastWriteUtc = lastWrite,
                            FreshUntilUtc = lastWrite.AddDays(maxAgeDays)
                        };
                    })
                    .Where(b => b != null)
                    .ToList();

                if (!string.IsNullOrWhiteSpace(expectedAccountId))
                    return blobs.FirstOrDefault(b => string.Equals(b.AccountId, expectedAccountId, StringComparison.OrdinalIgnoreCase));

                return blobs.Count == 1 ? blobs[0] : null;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[AUTH-CHECK] Failed to inspect modern auth blobs in " + folder, ex);
                return null;
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

        /// <summary>
        /// Writes the launcher window size/position that was live just before the switch into the restored config, so the
        /// launcher reopens where the user left it for every account (AppSettings.KeepLauncherWindowPlacement).
        /// Returns false when there is nothing to keep; the caller then falls back to the account's own saved placement.
        /// </summary>
        private static bool ApplyKeptLauncherWindowSettings(Dictionary<string, string> liveBeforeSwitch)
        {
            if (liveBeforeSwitch == null || liveBeforeSwitch.Count == 0) return false;
            try
            {
                var fallback = liveBeforeSwitch.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (string.IsNullOrWhiteSpace(fallback)) return false;
                var applied = 0;
                foreach (var ini in GetGameUserSettingsFiles())
                {
                    string value;
                    if (!liveBeforeSwitch.TryGetValue(MakeRelativePath(EpicConfigPath, ini), out value) || string.IsNullOrWhiteSpace(value)) value = fallback;
                    ApplyIniValuesAtomic(ini, "WindowSettings", new Dictionary<string, string> { { "MainLauncherWindow", value } });
                    applied++;
                }
                return applied > 0;
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("[INI] Could not keep the launcher window placement", ex);
                return false;
            }
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
    }
}

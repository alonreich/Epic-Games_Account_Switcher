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
    /// <summary>Registry identity capture, switch and rollback.</summary>
    public partial class AccountService
    {
        private static void RestoreRegistryIdentity(Account target)
        {
            var backup = CaptureRegistryData();
            try
            {
                ClearRegistryIdentity(backup);
                if (target.RegistryData != null)
                {
                    foreach (var kv in target.RegistryData)
                    {
                        if (!Globals.SetRegistryKey(kv.Key, kv.Value))
                            throw new InvalidOperationException("Failed to restore registry identity key: " + kv.Key);
                    }
                }
                if (!string.IsNullOrWhiteSpace(target.Id))
                {
                    if (!Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:AccountId", "sz:" + target.Id))
                        throw new InvalidOperationException("Failed to set AccountId registry key.");
                    if (target.RegistryData == null || !target.RegistryData.Keys.Any(k => k.EndsWith(":EpicAccountId", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!Globals.SetRegistryKey(@"HKCU\Software\Epic Games\Unreal Engine\Identifiers:EpicAccountId", "sz:" + target.Id))
                            throw new InvalidOperationException("Failed to set EpicAccountId registry key.");
                    }
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Registry identity modification failed; rolling back.", ex);
                RestoreRegistrySnapshot(backup);
                throw;
            }
        }

        private static void RestoreRegistrySnapshot(Dictionary<string, string> snapshot)
        {
            ClearRegistryIdentity(CaptureRegistryData());
            if (snapshot == null) return;
            foreach (var kv in snapshot)
            {
                for (int r = 0; r < 3; r++)
                {
                    if (Globals.SetRegistryKey(kv.Key, kv.Value)) break;
                    Thread.Sleep(100);
                }
            }
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
    }
}

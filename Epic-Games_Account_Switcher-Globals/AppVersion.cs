using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Product version helpers. The version is defined once in Directory.Build.props and read back from the assembly.
    /// Legacy calendar versions (2026.04.01.1310, v2026.08.05.1, ...) are always treated as OLDER than any SemVer 3.x+ version.
    /// </summary>
    public static class AppVersion
    {
        private static readonly Lazy<string> _current = new Lazy<string>(ReadCurrent);
        public static string Current => _current.Value;

        private static string ReadCurrent()
        {
            try
            {
                var asm = typeof(AppVersion).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    var plus = info.IndexOf('+');
                    return plus > 0 ? info.Substring(0, plus) : info;
                }
                var v = asm.GetName().Version;
                return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            }
            catch { return "0.0.0"; }
        }

        private sealed class Parsed
        {
            public bool Legacy;
            public Version Core;
            public string PreRelease;
        }

        private static Parsed Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            string pre = null;
            var dash = s.IndexOf('-');
            if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;
            var nums = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
            }
            return new Parsed
            {
                Legacy = nums[0] >= 2000,
                Core = new Version(nums[0], nums[1], parts.Length > 2 ? nums[2] : 0, parts.Length > 3 ? nums[3] : 0),
                PreRelease = string.IsNullOrWhiteSpace(pre) ? null : pre
            };
        }

        public static bool IsValid(string s) => Parse(s) != null;

        /// <summary>Returns &lt;0 if a is older than b, 0 if equal, &gt;0 if a is newer. Unparseable versions are treated as oldest.</summary>
        public static int Compare(string a, string b)
        {
            var pa = Parse(a); var pb = Parse(b);
            if (pa == null && pb == null) return 0;
            if (pa == null) return -1;
            if (pb == null) return 1;
            if (pa.Legacy != pb.Legacy) return pa.Legacy ? -1 : 1;
            var c = pa.Core.CompareTo(pb.Core);
            if (c != 0) return c;
            if (pa.PreRelease == null && pb.PreRelease == null) return 0;
            if (pa.PreRelease == null) return 1;
            if (pb.PreRelease == null) return -1;
            return ComparePreRelease(pa.PreRelease, pb.PreRelease);
        }

        private static int ComparePreRelease(string a, string b)
        {
            var xa = a.Split('.'); var xb = b.Split('.');
            for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
            {
                int na, nb;
                bool ia = int.TryParse(xa[i], out na), ib = int.TryParse(xb[i], out nb);
                int c = ia && ib ? na.CompareTo(nb) : ia ? -1 : ib ? 1 : string.CompareOrdinal(xa[i], xb[i]);
                if (c != 0) return c;
            }
            return xa.Length.CompareTo(xb.Length);
        }

        public static bool IsNewer(string candidate, string than) => Compare(candidate, than) > 0;

        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Trim();
            return s.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? s.Substring(1) : s;
        }

        /// <summary>Reads the product version of an installed executable (ProductVersion, falling back to FileVersion).</summary>
        public static string ReadFileVersion(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
                var fvi = FileVersionInfo.GetVersionInfo(exePath);
                var pv = fvi.ProductVersion;
                if (!string.IsNullOrWhiteSpace(pv))
                {
                    var plus = pv.IndexOf('+');
                    pv = plus > 0 ? pv.Substring(0, plus) : pv;
                    if (IsValid(pv)) return pv.Trim();
                }
                return IsValid(fvi.FileVersion) ? fvi.FileVersion.Trim() : null;
            }
            catch { return null; }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.IconLib;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace Epic_Games_Account_Switcher_Globals
{
    public partial class Globals
    {
        [SupportedOSPlatform("windows")]
        public static bool SaveIconFromFile(string path, string output)
        {
            if (!File.Exists(path)) return false;
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(path);
                if (ico == null) return false;
                using var ms = new MemoryStream();
                ico.Save(ms);
                ms.Seek(0, SeekOrigin.Begin);
                var mi = new MultiIcon();
                mi.Load(ms);
                var si = mi.FirstOrDefault();
                if (si == null) return false;
                var icon = si.OrderByDescending(i => i.Size.Height).FirstOrDefault();
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                icon?.Transparent.Save(output);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

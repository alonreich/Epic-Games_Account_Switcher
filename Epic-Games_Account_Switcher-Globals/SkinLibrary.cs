using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Avatar ("skin") collection: images shipped with the app (install folder\Skin_Icons) plus photos the user uploaded
    /// (user data\Skin_Icons\Custom). Uploaded photos are already cropped to a square by the app; cards show them as a circle.
    /// </summary>
    public static class SkinLibrary
    {
        public sealed class SkinFile
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public bool IsCustom { get; set; }
        }

        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

        public static string BuiltInFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skin_Icons");
        public static string UserFolder => Path.Combine(Globals.UserDataFolder, "Skin_Icons");
        public static string CustomFolder => Path.Combine(UserFolder, "Custom");

        public static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path ?? "");
            return ImageExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsBuiltIn(string path) => IsUnder(path, BuiltInFolder);
        public static bool IsCustom(string path) => IsUnder(path, CustomFolder);

        /// <summary>True when a shipped avatar with this file name exists (so a backup does not need to carry it).</summary>
        public static bool IsBuiltInName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            try { return File.Exists(Path.Combine(BuiltInFolder, Path.GetFileName(fileName))); } catch { return false; }
        }

        private static readonly Regex LegacyAccountPrefix = new Regex("^([0-9a-fA-F]{32}_)+", RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns the shipped avatar this path stands for, or null. Recognises older per-account copies such as
        /// "Skin_Icons\Selected\&lt;accountId&gt;_Galaxia_Female_Skin.jpg". Uploaded photos never match.
        /// </summary>
        public static string FindBuiltInFor(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsCustom(path)) return null;
            var fileName = Path.GetFileName(path);
            if (IsBuiltInName(fileName)) return Path.Combine(BuiltInFolder, fileName);
            var stripped = LegacyAccountPrefix.Replace(fileName, "");
            return stripped != fileName && IsBuiltInName(stripped) ? Path.Combine(BuiltInFolder, stripped) : null;
        }

        /// <summary>Shipped avatars first, then uploaded photos, then older user images (not the per-account "Selected" copies).</summary>
        public static List<SkinFile> GetAll()
        {
            var result = new List<SkinFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFrom(result, seen, BuiltInFolder, false);
            AddFrom(result, seen, CustomFolder, true);
            AddFrom(result, seen, UserFolder, false);
            return result;
        }

        private static void AddFrom(List<SkinFile> result, HashSet<string> seen, string folder, bool custom)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var f in Directory.GetFiles(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    if (!IsImageFile(f) || !seen.Add(Path.GetFileName(f))) continue;
                    result.Add(new SkinFile { Name = DisplayName(f), FullPath = Path.GetFullPath(f), IsCustom = custom });
                }
            }
            catch (Exception ex) { Globals.WriteToLog("[SKINS] Could not list " + folder, ex); }
        }

        public static string DisplayName(string path) => Path.GetFileNameWithoutExtension(path ?? "").Replace("_", " ").Trim();

        /// <summary>Saves an already-cropped image (encoded bytes) into the uploaded-photos folder and returns its path.</summary>
        public static string SaveCustom(byte[] encodedImage, string suggestedName, string extension = ".jpg")
        {
            if (encodedImage == null || encodedImage.Length == 0) throw new ArgumentException("Image is empty.", nameof(encodedImage));
            Directory.CreateDirectory(CustomFolder);
            var baseName = MakeSafeName(string.IsNullOrWhiteSpace(suggestedName) ? "Photo" : suggestedName);
            var target = Path.Combine(CustomFolder, baseName + extension);
            for (var i = 2; File.Exists(target) || IsBuiltInName(Path.GetFileName(target)); i++)
                target = Path.Combine(CustomFolder, baseName + "_" + i + extension);
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, encodedImage);
            File.Move(tmp, target);
            return target;
        }

        public static bool DeleteCustom(string path)
        {
            if (!IsCustom(path) || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public static string MakeSafeName(string value)
        {
            var safe = new string((value ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            return string.IsNullOrWhiteSpace(safe) ? "Photo" : safe;
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                var full = Path.GetFullPath(path);
                var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}

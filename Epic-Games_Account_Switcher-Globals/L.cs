using System;
using System.Globalization;
using System.Resources;
using System.Text;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Localization entry point. The English source text is the lookup key (gettext style): L.T("Switch to {0}", name).
    /// Strings.resx lists every source text (regenerate it with developer_tools\Extract-Strings.ps1); a translation is a
    /// Strings.&lt;culture&gt;.resx copy with translated values. Text without a translation is shown in English.
    /// The first argument must be ONE string literal so the extractor can find it.
    /// Resource names are case-insensitive, so texts that differ only in case ("Later" / "LATER") share one entry:
    /// the translation is written once in normal case and shown in capitals where the English text is all capitals.
    /// </summary>
    public static class L
    {
        private static readonly ResourceManager Resources = new ResourceManager("Epic_Games_Account_Switcher_Globals.Strings", typeof(L).Assembly) { IgnoreCase = true };

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string value;
            try { value = Resources.GetString(text, CultureInfo.CurrentUICulture); }
            catch { return text; }
            // No translation (or the English entry of a case variant): the source text is the answer, in its own case.
            if (string.IsNullOrEmpty(value) || string.Equals(value, text, StringComparison.OrdinalIgnoreCase)) return text;
            return IsAllCaps(text) ? ToUpperOutsidePlaceholders(value) : value;
        }

        public static string T(string text, params object[] args)
        {
            var format = T(text);
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException)
            {
                // A broken translation must never take the app down: fall back to the English source text.
                try { return string.Format(CultureInfo.CurrentCulture, text, args); } catch (FormatException) { return text; }
            }
        }

        private static bool IsAllCaps(string text)
        {
            var letters = false;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                if (char.IsLower(c)) return false;
                letters = true;
            }
            return letters;
        }

        private static string ToUpperOutsidePlaceholders(string value)
        {
            var result = new StringBuilder(value.Length);
            var depth = 0;
            foreach (var c in value)
            {
                if (c == '{') depth++;
                else if (c == '}' && depth > 0) depth--;
                result.Append(depth > 0 ? c : char.ToUpper(c, CultureInfo.CurrentUICulture));
            }
            return result.ToString();
        }
    }
}

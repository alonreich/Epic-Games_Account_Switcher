using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>XAML side of <see cref="L"/>: Text="{local:T 'Select an account'}". The English text is the lookup key.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public class TExtension : MarkupExtension
    {
        public TExtension() { }
        public TExtension(string text) { Text = text; }

        [ConstructorArgument("text")]
        public string Text { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text ?? ""); // extractor: passthrough
    }

    /// <summary>
    /// Follows Windows contrast themes: while one is active, every brush token of App.xaml is replaced by the matching
    /// system colour (window, text, highlight), so text, outlines and focus rings use the colours the user picked.
    /// The normal palette comes back when the contrast theme is turned off.
    /// </summary>
    internal static class ThemeManager
    {
        private enum Role { Window, Text, Highlight, HighlightText, Gray, Overlay }

        private static readonly Dictionary<string, Role> Roles = new Dictionary<string, Role>
        {
            { "HeaderbarBackground", Role.Window }, { "MainBackground", Role.Window }, { "BtnBackground", Role.Window },
            { "BtnBackgroundHover", Role.Window }, { "BtnBackgroundActive", Role.Window }, { "MinBackground", Role.Window },
            { "CardBackground", Role.Window }, { "CardHoverBackground", Role.Window }, { "DialogBottomBackground", Role.Window },
            { "WarningHoverBackground", Role.Window }, { "WarningPressedBackground", Role.Window },
            { "OverlayBackground", Role.Overlay },

            { "BtnForeground", Role.Text }, { "PrimaryTextForeground", Role.Text }, { "SubTextForeground", Role.Text },
            { "AccentTextForeground", Role.Text }, { "WarningForeground", Role.Text }, { "BtnBorder", Role.Text },
            { "InputBorder", Role.Text }, { "ScrollThumbBrush", Role.Text }, { "ActiveIndicatorInactive", Role.Gray },
            { "InputTextHint", Role.Gray },

            { "IconFill", Role.Highlight }, { "BtnBorderHover", Role.Highlight }, { "CardActiveBorder", Role.Highlight },
            { "ActiveIndicatorActive", Role.Highlight }, { "SuccessBackground", Role.Highlight }, { "FocusRingBrush", Role.Highlight },
            { "ScrollThumbHoverBrush", Role.Highlight }, { "MinBackgroundHover", Role.Highlight }, { "MinBackgroundActive", Role.Highlight },
            { "CloseBackgroundHover", Role.Highlight }, { "CloseBackgroundActive", Role.Highlight },

            { "OnAccentForeground", Role.HighlightText }, { "ChromeHoverForeground", Role.HighlightText }
        };

        private static readonly Dictionary<string, object> Originals = new Dictionary<string, object>();
        private static bool _forced;
        private static bool _applied;
        private static bool _listening;

        public static bool IsHighContrast => _forced || SystemParameters.HighContrast;

        /// <summary>Applies the right palette now and keeps following the Windows setting. <paramref name="force"/> is a test switch.</summary>
        public static void Initialize(bool force)
        {
            _forced = force;
            Apply();
            if (_listening) return;
            _listening = true;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }

        private static void OnSystemParametersChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Normal);
        }

        private static void Apply()
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            try
            {
                if (IsHighContrast)
                {
                    foreach (var pair in Roles)
                    {
                        if (!Originals.ContainsKey(pair.Key)) Originals[pair.Key] = resources[pair.Key];
                        resources[pair.Key] = BrushFor(pair.Value);
                    }
                    _applied = true;
                }
                else if (_applied)
                {
                    foreach (var pair in Originals) resources[pair.Key] = pair.Value;
                    _applied = false;
                }
            }
            catch (Exception ex) { Globals.WriteToLog("[THEME] Could not apply the contrast theme colours", ex); }
        }

        private static Brush BrushFor(Role role)
        {
            Color color;
            switch (role)
            {
                case Role.Text: color = SystemColors.WindowTextColor; break;
                case Role.Highlight: color = SystemColors.HighlightColor; break;
                case Role.HighlightText: color = SystemColors.HighlightTextColor; break;
                case Role.Gray: color = SystemColors.GrayTextColor; break;
                case Role.Overlay: color = SystemColors.WindowColor; color.A = 0xE6; break;
                default: color = SystemColors.WindowColor; break;
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    internal static class ClipboardHelper
    {
        private static DispatcherTimer _clearTimer;

        /// <summary>
        /// Copies a secret so that it stays out of the Windows clipboard history and cloud clipboard, and removes it again
        /// after <paramref name="clearAfterSeconds"/> (or when the app closes) unless something else was copied meanwhile.
        /// </summary>
        public static bool CopySensitive(string text, int clearAfterSeconds = 30)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, false);
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Could not copy to the clipboard: " + ex.Message);
                return false;
            }

            if (_clearTimer != null) _clearTimer.Stop();
            _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, clearAfterSeconds)) };
            _clearTimer.Tick += (s, e) =>
            {
                ((DispatcherTimer)s).Stop();
                try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
            };
            _clearTimer.Start();
            return true;
        }

        public static bool CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (Exception ex) { Globals.WriteToLog("Could not copy to the clipboard: " + ex.Message); return false; }
        }
    }
}

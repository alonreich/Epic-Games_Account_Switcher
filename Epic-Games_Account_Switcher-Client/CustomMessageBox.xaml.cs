using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace Epic_Games_Account_Switcher_Client
{
    /// <summary>Danger = destructive confirmation: the primary button is styled as a warning and is NOT the default; Enter picks the safe choice.</summary>
    public enum MessageBoxVisualStyle { Neutral, Success, Error, Danger }
    public enum CustomDialogResult { None, Primary, Secondary, Cancel }

    public partial class CustomMessageBox : Window
    {
        public CustomDialogResult Result { get; private set; } = CustomDialogResult.None;

        public CustomMessageBox(string title, string message, MessageBoxVisualStyle style = MessageBoxVisualStyle.Neutral) : this(title, message, Epic_Games_Account_Switcher_Globals.L.T("OK"), null, null, style) { }
        public CustomMessageBox(string title, string message, string primaryBtnText, string secondaryBtnText = null, string cancelBtnText = null, MessageBoxVisualStyle style = MessageBoxVisualStyle.Neutral)
        {
            InitializeComponent();
            ConfigureOwner();
            DialogWindowHelper.FitToScreen(this);
            HeaderInstruction.Content = title.ToUpper();
            Title = title;
            Message.Text = message;
            System.Windows.Automation.AutomationProperties.SetHelpText(this, message ?? "");
            ButtonOk.Content = primaryBtnText;
            bool hasSecondary = !string.IsNullOrEmpty(secondaryBtnText);
            bool hasCancel = !string.IsNullOrEmpty(cancelBtnText);
            if (!hasSecondary) { ButtonNo.Visibility = Visibility.Collapsed; }
            else { ButtonNo.Content = secondaryBtnText; ButtonNo.Visibility = Visibility.Visible; }
            if (!hasCancel) { ButtonCancel.Visibility = Visibility.Collapsed; }
            else { ButtonCancel.Content = cancelBtnText; ButtonCancel.Visibility = Visibility.Visible; }
            ButtonNo.IsCancel = hasSecondary && !hasCancel;
            ButtonCancel.IsCancel = hasCancel;
            // A plain alert with only one button closes with Esc as well as Enter.
            ButtonOk.IsCancel = !hasSecondary && !hasCancel;

            Brush accent;
            switch (style)
            {
                case MessageBoxVisualStyle.Success:
                    accent = ThemeBrush("SuccessBackground", Color.FromRgb(158, 206, 106));
                    HeaderBg.Background = accent; HeaderInstruction.Foreground = ThemeBrush("OnAccentForeground", Colors.Black);
                    IconGlyph.Text = "";
                    break;
                case MessageBoxVisualStyle.Error:
                case MessageBoxVisualStyle.Danger:
                    accent = ThemeBrush("WarningForeground", Color.FromRgb(247, 118, 142));
                    HeaderBg.Background = accent; HeaderInstruction.Foreground = ThemeBrush("OnAccentForeground", Colors.Black);
                    IconGlyph.Text = style == MessageBoxVisualStyle.Danger ? "" : "";
                    break;
                default:
                    accent = ThemeBrush("IconFill", Color.FromRgb(122, 162, 247));
                    HeaderBg.Background = ThemeBrush("HeaderbarBackground", Color.FromRgb(20, 21, 30)); HeaderInstruction.Foreground = ThemeBrush("PrimaryTextForeground", Colors.White);
                    IconGlyph.Text = hasSecondary || hasCancel ? "" : "";
                    break;
            }
            IconGlyph.Foreground = accent;

            if (style == MessageBoxVisualStyle.Danger && (hasSecondary || hasCancel))
            {
                ButtonOk.Style = (Style)FindResource("DangerButton");
                ButtonOk.IsDefault = false;
                var safe = hasSecondary ? ButtonNo : ButtonCancel;
                safe.IsDefault = true;
                Loaded += (s, e) => safe.Focus();
            }
            else
            {
                Loaded += (s, e) => ButtonOk.Focus();
            }
        }

        private static Brush ThemeBrush(string key, Color fallback)
        {
            return (Application.Current?.TryFindResource(key) as Brush) ?? new SolidColorBrush(fallback);
        }

        private void ConfigureOwner()
        {
            try
            {
                var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => !ReferenceEquals(w, this) && w.IsActive && w.IsVisible);
                if (owner == null && Application.Current.MainWindow != null && !ReferenceEquals(Application.Current.MainWindow, this) && Application.Current.MainWindow.IsVisible)
                    owner = Application.Current.MainWindow;

                if (owner != null)
                {
                    Owner = owner;
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    Topmost = true;
                    ShowInTaskbar = true;
                }
            }
            catch
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Topmost = true;
                ShowInTaskbar = true;
            }
        }
        private void DragWindow(object sender, MouseButtonEventArgs e) { DialogWindowHelper.DragMove(this, e); }

        /// <summary>Like standard Windows message boxes: Ctrl+C with nothing selected copies the title and the whole message.</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || Message.SelectionLength > 0) return;
            if (ClipboardHelper.CopyText(Title + Environment.NewLine + Environment.NewLine + Message.Text)) e.Handled = true;
        }
        private void BtnExit(object sender, RoutedEventArgs e) { Result = CustomDialogResult.Cancel; Close(); }
        private void ButtonOk_OnClick(object sender, RoutedEventArgs e) { Result = CustomDialogResult.Primary; DialogResult = true; Close(); }
        private void ButtonNo_OnClick(object sender, RoutedEventArgs e) { Result = CustomDialogResult.Secondary; DialogResult = false; Close(); }
        private void ButtonCancel_OnClick(object sender, RoutedEventArgs e) { Result = CustomDialogResult.Cancel; Close(); }
    }

    /// <summary>Shared behaviour for the borderless dialogs: drag to move, and never larger than the screen.</summary>
    internal static class DialogWindowHelper
    {
        /// <summary>The dashboard zoom the user chose (set by the main view model). Dialogs, the Settings popup and menus follow it when it is above 100%.</summary>
        public static double UiZoom { get; set; } = 1.0;

        /// <summary>Zoom for dialogs, popups and menus: they grow with the dashboard zoom but are never shrunk below 100%.</summary>
        public static double PopupZoom => Math.Max(1.0, Math.Min(1.75, UiZoom));

        /// <summary>
        /// Caps the dialog at the work area of the screen its owner is on (content inside scrolls instead of going off-screen)
        /// and applies the user's zoom to its content, reduced when the zoomed dialog would not fit that screen.
        /// </summary>
        public static void FitToScreen(Window dialog)
        {
            dialog.SourceInitialized += (s, e) =>
            {
                double areaWidth, areaHeight;
                try
                {
                    var anchor = dialog.Owner ?? dialog;
                    var handle = new System.Windows.Interop.WindowInteropHelper(anchor).Handle;
                    var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    var source = PresentationSource.FromVisual(dialog);
                    var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                    areaWidth = area.Width / scaleX - 16;
                    areaHeight = area.Height / scaleY - 16;
                }
                catch
                {
                    areaWidth = SystemParameters.WorkArea.Width;
                    areaHeight = SystemParameters.WorkArea.Height;
                }

                try { ApplyZoom(dialog, areaWidth, areaHeight); } catch { }
                dialog.MaxWidth = Math.Min(dialog.MaxWidth, Math.Max(Math.Min(dialog.MinWidth, areaWidth), areaWidth));
                dialog.MaxHeight = Math.Min(dialog.MaxHeight, Math.Max(200, areaHeight));
                if (dialog.MinWidth > dialog.MaxWidth) dialog.MinWidth = dialog.MaxWidth;
                if (dialog.MinHeight > dialog.MaxHeight) dialog.MinHeight = dialog.MaxHeight;
            };
        }

        private static void ApplyZoom(Window dialog, double areaWidth, double areaHeight)
        {
            var zoom = PopupZoom;
            var root = dialog.Content as FrameworkElement;
            if (root == null || zoom < 1.01) return;

            // Natural (100%) size of the content, wrapped at the dialog's own maximum width.
            root.Measure(new Size(double.IsInfinity(dialog.MaxWidth) ? double.PositiveInfinity : dialog.MaxWidth, double.PositiveInfinity));
            var width = Math.Max(root.DesiredSize.Width, dialog.MinWidth);
            var height = Math.Max(root.DesiredSize.Height, dialog.MinHeight);
            if (width > 0) zoom = Math.Min(zoom, areaWidth / width);
            if (height > 0) zoom = Math.Min(zoom, areaHeight / height);
            if (zoom < 1.01) return;

            root.LayoutTransform = new System.Windows.Media.ScaleTransform(zoom, zoom);
            dialog.MinWidth *= zoom;
            dialog.MinHeight *= zoom;
            if (!double.IsInfinity(dialog.MaxWidth)) dialog.MaxWidth *= zoom;
            if (!double.IsInfinity(dialog.MaxHeight)) dialog.MaxHeight *= zoom;
        }

        /// <summary>Moves the dialog when the user drags an empty area (controls handle their own clicks first).</summary>
        public static void DragMove(Window dialog, MouseButtonEventArgs e)
        {
            if (e.Handled || e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            try { dialog.DragMove(); } catch (InvalidOperationException) { }
        }
    }
}

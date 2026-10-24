using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class PasswordPromptWindow : Window
    {
        private readonly bool _confirmPassword;
        private readonly int _minimumLength;

        public string EnteredPassword { get; private set; }

        public PasswordPromptWindow(string title, string message, bool confirmPassword, int minimumLength = 1)
        {
            InitializeComponent();
            DialogWindowHelper.FitToScreen(this);
            _confirmPassword = confirmPassword;
            _minimumLength = minimumLength < 1 ? 1 : minimumLength;
            TitleText.Text = (title ?? "Backup Password").ToUpperInvariant();
            Title = title ?? "Backup Password";
            MessageText.Text = message ?? "";
            ConfirmPanel.Visibility = confirmPassword ? Visibility.Visible : Visibility.Collapsed;
            OkButton.Content = confirmPassword ? "CREATE BACKUP" : "RESTORE";
            if (_minimumLength > 1) PasswordLabel.Text = "Password (at least " + _minimumLength + " characters)";
            Loaded += (s, e) => PasswordInput.Focus();
        }

        /// <summary>Shows the problem inline (and announces it) instead of opening another dialog.</summary>
        private void ShowError(string text, PasswordBox focusTarget)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
            try { (UIElementAutomationPeer.FromElement(ErrorText) ?? UIElementAutomationPeer.CreatePeerForElement(ErrorText))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); } catch { }
            focusTarget.Focus();
            focusTarget.SelectAll();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var password = PasswordInput.Password ?? "";
            if (password.Length < _minimumLength)
            {
                ShowError(_minimumLength > 1 ? "Use a password with at least " + _minimumLength + " characters." : "Enter the password.", PasswordInput);
                return;
            }

            if (_confirmPassword && password != (ConfirmPasswordInput.Password ?? ""))
            {
                ShowError("The two passwords don't match. Type the same password in both fields.", ConfirmPasswordInput);
                return;
            }

            EnteredPassword = password;
            DialogResult = true;
            Close();
        }

        private void Dialog_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e) => DialogWindowHelper.DragMove(this, e);

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

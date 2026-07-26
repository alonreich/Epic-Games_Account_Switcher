using System.Windows;

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
            _confirmPassword = confirmPassword;
            _minimumLength = minimumLength < 1 ? 1 : minimumLength;
            TitleText.Text = (title ?? "Backup Password").ToUpperInvariant();
            MessageText.Text = message ?? "";
            ConfirmPanel.Visibility = confirmPassword ? Visibility.Visible : Visibility.Collapsed;
            OkButton.Content = confirmPassword ? "CREATE BACKUP" : "RESTORE";
            Loaded += (s, e) => PasswordInput.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var password = PasswordInput.Password ?? "";
            if (password.Length < _minimumLength)
            {
                new CustomMessageBox("Password Required", "Use a password with at least " + _minimumLength + " characters.", MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }

            if (_confirmPassword && password != (ConfirmPasswordInput.Password ?? ""))
            {
                new CustomMessageBox("Password Mismatch", "The backup passwords do not match.", MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }

            EnteredPassword = password;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

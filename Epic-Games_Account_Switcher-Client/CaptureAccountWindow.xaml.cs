using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class CaptureAccountWindow : Window, System.ComponentModel.INotifyPropertyChanged, System.ComponentModel.IDataErrorInfo
    {
        private bool _saved;
        private string _accountName;
        public string AccountName
        {
            get => _accountName;
            set { _accountName = value; OnPropertyChanged(nameof(AccountName)); }
        }

        private string _accountDescription;
        public string AccountDescription
        {
            get => _accountDescription;
            set { _accountDescription = value; OnPropertyChanged(nameof(AccountDescription)); }
        }

        private string _accountEmail;
        public string AccountEmail
        {
            get => _accountEmail;
            set { _accountEmail = value; OnPropertyChanged(nameof(AccountEmail)); }
        }

        public string AccountPassword { get; set; }
        public string ProfileImagePath { get; set; }
        private string _targetAccountId;
        private bool _enforceStrictRequiredFields;
        private bool _submitAttempted;
        private readonly HashSet<string> _touchedFields = new HashSet<string>();
        // Read once per dialog: duplicate checks must not hit the disk on every keystroke.
        private readonly List<Account> _otherAccounts;
        // What the dialog opened with, to tell whether closing would lose anything.
        private readonly string _initialName, _initialEmail, _initialDescription, _initialPassword, _initialImage;
        private DispatcherTimer _copiedTimer;
        public string EmailLabel => _enforceStrictRequiredFields ? L.T("Epic account email *") : L.T("Epic account email");
        public bool IsEmailRequired => _enforceStrictRequiredFields;
        public string DialogTitle => _enforceStrictRequiredFields ? L.T("ADD ACCOUNT") : L.T("EDIT ACCOUNT");
        public string SaveButtonText => _enforceStrictRequiredFields ? L.T("SAVE ACCOUNT") : L.T("SAVE CHANGES");

        public CaptureAccountWindow(string name, string desc, string email, string image, string pass, string targetId = null, bool enforceStrictRequiredFields = false, bool blankIdentityInputs = false)
        {
            InitializeComponent();
            DialogWindowHelper.FitToScreen(this);
            _targetAccountId = targetId;
            _enforceStrictRequiredFields = enforceStrictRequiredFields;
            try { _otherAccounts = AccountService.GetAccounts().Where(a => a.Id != _targetAccountId).ToList(); }
            catch (Exception ex) { Globals.WriteToLog("Could not read saved accounts for name checks", ex); _otherAccounts = new List<Account>(); }
            AccountName = blankIdentityInputs ? "" : NormalizeDetectedField(name);
            AccountDescription = desc ?? "";
            AccountEmail = blankIdentityInputs ? "" : NormalizeDetectedField(email);
            ProfileImagePath = image;
            AccountPassword = pass;

            AccountPasswordInput.Password = pass ?? "";
            SetProfilePreview(ProfileImagePath);
            _initialName = AccountName ?? "";
            _initialEmail = AccountEmail ?? "";
            _initialDescription = AccountDescription ?? "";
            _initialPassword = pass ?? "";
            _initialImage = ProfileImagePath ?? "";
            DataContext = this;
            UpdatePasswordButtons();
            Loaded += (s, e) => FocusFirstField();
        }

        private void FocusFirstField()
        {
            var target = AccountNameInput;
            if (!string.IsNullOrWhiteSpace(AccountName) && _enforceStrictRequiredFields && string.IsNullOrWhiteSpace(AccountEmail)) target = AccountEmailInput;
            target.Focus();
            target.SelectAll();
        }

        private void Field_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            // Show a field's error only after the user has left it (or tried to save), not while the form is still empty.
            var property = ReferenceEquals(sender, AccountNameInput) ? nameof(AccountName) : ReferenceEquals(sender, AccountEmailInput) ? nameof(AccountEmail) : null;
            if (property == null || !_touchedFields.Add(property)) return;
            OnPropertyChanged(property);
        }

        private static string NormalizeDetectedField(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var trimmed = value.Trim();
            return string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
        }

        private void TextBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
            {
                tb.SelectAll();
            }
            else if (sender is System.Windows.Controls.PasswordBox pb)
            {
                pb.SelectAll();
            }
        }

        private void Dialog_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e) => DialogWindowHelper.DragMove(this, e);

        #region Saved password (show / hide / copy)

        private bool IsPasswordRevealed => AccountPasswordReveal.Visibility == Visibility.Visible;
        private string CurrentPassword => IsPasswordRevealed ? AccountPasswordReveal.Text : AccountPasswordInput.Password;

        private void Password_Changed(object sender, RoutedEventArgs e) => UpdatePasswordButtons();

        private void UpdatePasswordButtons()
        {
            if (CopyPasswordButton == null) return;
            CopyPasswordButton.IsEnabled = !string.IsNullOrEmpty(CurrentPassword);
        }

        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            var reveal = !IsPasswordRevealed;
            if (reveal)
            {
                AccountPasswordReveal.Text = AccountPasswordInput.Password;
                AccountPasswordReveal.Visibility = Visibility.Visible;
                AccountPasswordInput.Visibility = Visibility.Collapsed;
                AccountPasswordReveal.Focus();
                AccountPasswordReveal.CaretIndex = AccountPasswordReveal.Text.Length;
            }
            else
            {
                AccountPasswordInput.Password = AccountPasswordReveal.Text;
                AccountPasswordInput.Visibility = Visibility.Visible;
                AccountPasswordReveal.Visibility = Visibility.Collapsed;
                AccountPasswordReveal.Text = "";
                AccountPasswordInput.Focus();
            }
            TogglePasswordButton.Content = reveal ? L.T("HIDE") : L.T("SHOW");
            AutomationProperties.SetName(TogglePasswordButton, reveal ? L.T("Hide password") : L.T("Show password"));
            UpdatePasswordButtons();
        }

        private void CopyPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!ClipboardHelper.CopySensitive(CurrentPassword)) return;
            CopyPasswordButton.Content = L.T("COPIED");
            if (_copiedTimer == null)
            {
                _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _copiedTimer.Tick += (s, args) => { _copiedTimer.Stop(); CopyPasswordButton.Content = L.T("COPY"); };
            }
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        #endregion

        private void SelectImage_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var win = new SkinPickerWindow(ProfileImagePath) { Owner = this };
            if (win.ShowDialog() == true) { ProfileImagePath = win.SelectedSkinPath; OnPropertyChanged(nameof(ProfileImagePath)); SetProfilePreview(ProfileImagePath); }
        }

        private void SetProfilePreview(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.None;
                bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ProfileImageBrush.ImageSource = bitmap;
            }
            catch { }
        }

        public string Error => null;
        public string this[string columnName]
        {
            get
            {
                if (!_submitAttempted && !_touchedFields.Contains(columnName)) return null;
                return GetValidationError(columnName);
            }
        }

        private string GetValidationError(string columnName)
        {
            if (columnName == nameof(AccountName))
            {
                if (string.IsNullOrWhiteSpace(AccountName)) return L.T("Enter a display name.");
                var name = AccountName.Trim();
                if (_otherAccounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return L.T("Another saved account already uses this name.");
            }
            else if (columnName == nameof(AccountEmail))
            {
                if (_enforceStrictRequiredFields && string.IsNullOrWhiteSpace(AccountEmail)) return L.T("Enter the account's email address.");
                if (!string.IsNullOrWhiteSpace(AccountEmail))
                {
                    var email = AccountEmail.Trim();
                    if (_otherAccounts.Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)))
                        return L.T("Another saved account already uses this email.");
                }
            }
            return null;
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (_saved) return;
            AccountNameInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            AccountEmailInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            AccountDescriptionInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            _submitAttempted = true;
            AccountName = (AccountName ?? "").Trim();
            AccountEmail = (AccountEmail ?? "").Trim();

            // Errors are shown inline under each field; move the cursor to the first one.
            if (!string.IsNullOrEmpty(GetValidationError(nameof(AccountName))))
            {
                AccountNameInput.Focus();
                AccountNameInput.SelectAll();
                return;
            }

            if (!string.IsNullOrEmpty(GetValidationError(nameof(AccountEmail))))
            {
                AccountEmailInput.Focus();
                AccountEmailInput.SelectAll();
                return;
            }

            AccountPassword = CurrentPassword;
            _saved = true;
            DialogResult = true;
        }

        private bool HasUnsavedChanges()
        {
            return !string.Equals((AccountNameInput.Text ?? "").Trim(), _initialName, StringComparison.Ordinal)
                || !string.Equals((AccountEmailInput.Text ?? "").Trim(), _initialEmail, StringComparison.Ordinal)
                || !string.Equals(AccountDescriptionInput.Text ?? "", _initialDescription, StringComparison.Ordinal)
                || !string.Equals(CurrentPassword ?? "", _initialPassword, StringComparison.Ordinal)
                || !string.Equals(ProfileImagePath ?? "", _initialImage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Esc, CANCEL and Alt+F4 all end here: typed changes are never thrown away without asking.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || _saved || !HasUnsavedChanges()) return;
            var box = new CustomMessageBox(L.T("Discard Changes?"),
                L.T("You changed this account's details but haven't saved them.\n\nDiscard the changes, or keep editing?"),
                L.T("Discard Changes"), L.T("Keep Editing"), null, MessageBoxVisualStyle.Danger);
            box.ShowDialog();
            if (box.Result != CustomDialogResult.Primary) e.Cancel = true;
        }
    }
}

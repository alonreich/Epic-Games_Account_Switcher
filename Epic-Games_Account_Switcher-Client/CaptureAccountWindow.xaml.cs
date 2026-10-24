using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class CaptureAccountWindow : Window, System.ComponentModel.INotifyPropertyChanged, System.ComponentModel.IDataErrorInfo
    {
        private bool _isClosing;
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
        public string EmailLabel => _enforceStrictRequiredFields ? "Epic account email *" : "Epic account email";
        public bool IsEmailRequired => _enforceStrictRequiredFields;
        public string DialogTitle => _enforceStrictRequiredFields ? "ADD ACCOUNT" : "EDIT ACCOUNT";
        public string SaveButtonText => _enforceStrictRequiredFields ? "SAVE ACCOUNT" : "SAVE CHANGES";

        public CaptureAccountWindow(string name, string desc, string email, string image, string pass, string targetId = null, bool enforceStrictRequiredFields = false, bool blankIdentityInputs = false)
        {
            InitializeComponent();
            DialogWindowHelper.FitToScreen(this);
            _targetAccountId = targetId;
            _enforceStrictRequiredFields = enforceStrictRequiredFields;
            try { _otherAccounts = AccountService.GetAccounts().Where(a => a.Id != _targetAccountId).ToList(); }
            catch (Exception ex) { Globals.WriteToLog("Could not read saved accounts for name checks", ex); _otherAccounts = new List<Account>(); }
            AccountName = blankIdentityInputs ? "" : NormalizeDetectedField(name);
            AccountDescription = desc;
            AccountEmail = blankIdentityInputs ? "" : NormalizeDetectedField(email);
            ProfileImagePath = image;
            AccountPassword = pass;

            AccountPasswordInput.Password = pass;
            SetProfilePreview(ProfileImagePath);
            DataContext = this;
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
                if (string.IsNullOrWhiteSpace(AccountName)) return "Enter a display name.";
                var name = AccountName.Trim();
                if (_otherAccounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return "Another saved account already uses this name.";
            }
            else if (columnName == nameof(AccountEmail))
            {
                if (_enforceStrictRequiredFields && string.IsNullOrWhiteSpace(AccountEmail)) return "Enter the account's email address.";
                if (!string.IsNullOrWhiteSpace(AccountEmail))
                {
                    var email = AccountEmail.Trim();
                    if (_otherAccounts.Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)))
                        return "Another saved account already uses this email.";
                }
            }
            return null;
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
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

            AccountPassword = AccountPasswordInput.Password;
            _isClosing = true;
            DialogResult = true;
            Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) 
        { 
            if (_isClosing) return;
            _isClosing = true;
            DialogResult = false; 
            Close(); 
        }
    }
}

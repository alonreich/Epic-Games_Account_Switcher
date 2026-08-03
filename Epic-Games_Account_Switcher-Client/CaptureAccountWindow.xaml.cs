using System;
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
        public string EmailLabel => _enforceStrictRequiredFields ? "Connected Email *" : "Connected Email";

        public CaptureAccountWindow(string name, string desc, string email, string image, string pass, string targetId = null, bool enforceStrictRequiredFields = false, bool blankIdentityInputs = false)
        {
            InitializeComponent();
            _targetAccountId = targetId;
            _enforceStrictRequiredFields = enforceStrictRequiredFields;
            AccountName = blankIdentityInputs ? "" : NormalizeDetectedField(name);
            AccountDescription = desc;
            AccountEmail = blankIdentityInputs ? "" : NormalizeDetectedField(email);
            ProfileImagePath = image;
            AccountPassword = pass;

            AccountPasswordInput.Password = pass;
            SetProfilePreview(ProfileImagePath);
            DataContext = this;
        }

        private static string NormalizeDetectedField(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var trimmed = value.Trim();
            return string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
        }

        private void SelectImage_Click(object sender, RoutedEventArgs e)
        {
            var win = new SkinPickerWindow { Owner = this };
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
                if (columnName == nameof(AccountName))
                {
                    if (string.IsNullOrWhiteSpace(AccountName)) return "Account Name is required.";
                    var other = AccountService.GetAccounts().Where(a => a.Id != _targetAccountId);
                    if (other.Any(a => string.Equals(a.Name, AccountName, StringComparison.OrdinalIgnoreCase)))
                        return "An account with this friendly name already exists.";
                }
                else if (columnName == nameof(AccountEmail))
                {
                    if (_enforceStrictRequiredFields && string.IsNullOrWhiteSpace(AccountEmail)) return "Connected Email is required.";
                    if (!string.IsNullOrWhiteSpace(AccountEmail))
                    {
                        var other = AccountService.GetAccounts().Where(a => a.Id != _targetAccountId);
                        if (other.Any(a => string.Equals(a.Email, AccountEmail, StringComparison.OrdinalIgnoreCase)))
                            return "An account with this email address is already captured.";
                    }
                }
                return null;
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing) return;
            AccountNameInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            AccountEmailInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            AccountDescriptionInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            AccountName = (AccountName ?? "").Trim();
            AccountEmail = (AccountEmail ?? "").Trim();

            if (!string.IsNullOrEmpty(this[nameof(AccountName)]))
            {
                new CustomMessageBox("Input Error", this[nameof(AccountName)], MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }

            if (!string.IsNullOrEmpty(this[nameof(AccountEmail)]))
            {
                new CustomMessageBox("Input Error", this[nameof(AccountEmail)], MessageBoxVisualStyle.Error).ShowDialog();
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

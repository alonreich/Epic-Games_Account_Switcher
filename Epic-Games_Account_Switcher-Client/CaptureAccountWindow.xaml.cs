using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Epic_Games_Account_Switcher_Globals;

namespace Epic_Games_Account_Switcher_Client
{
    public partial class CaptureAccountWindow : Window
    {
        public string AccountName { get; set; }
        public string AccountDescription { get; set; }
        public string AccountEmail { get; set; }
        public string AccountPassword { get; set; }
        public string ProfileImagePath { get; set; }
        private string _targetAccountId;

        public CaptureAccountWindow(string name, string desc, string email, string image, string pass, string targetId = null, bool enforceStrictRequiredFields = false, bool blankIdentityInputs = false)
        {
            InitializeComponent();
            AccountName = name;
            AccountDescription = desc;
            AccountEmail = email;
            ProfileImagePath = image;
            AccountPassword = pass;
            _targetAccountId = targetId;

            AccountNameInput.Text = blankIdentityInputs ? "" : name;
            AccountEmailInput.Text = blankIdentityInputs ? "" : email;
            AccountDescriptionInput.Text = desc;
            AccountPasswordInput.Password = pass;
            if (!string.IsNullOrEmpty(ProfileImagePath)) { try { ProfileImageBrush.ImageSource = new BitmapImage(new Uri(ProfileImagePath)); } catch { } }
        }
        private void SelectImage_Click(object sender, RoutedEventArgs e)
        {
            var win = new SkinPickerWindow { Owner = this };
            if (win.ShowDialog() == true) { ProfileImagePath = win.SelectedSkinPath; ProfileImageBrush.ImageSource = new BitmapImage(new Uri(ProfileImagePath)); }
        }
        private void Capture_Click(object sender, RoutedEventArgs e)
        {
            var newName = AccountNameInput.Text;
            var newEmail = AccountEmailInput.Text;

            if (string.IsNullOrWhiteSpace(newName)) { new CustomMessageBox("Input Error", "Account Name is required.").ShowDialog(); return; }

            var existingAccounts = AccountService.GetAccounts();
            var otherAccounts = existingAccounts.Where(a => a.Id != _targetAccountId).ToList();

            if (otherAccounts.Any(a => string.Equals(a.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                new CustomMessageBox("Duplicate Name", "An account with this friendly name already exists. Please choose a unique name.", MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }

            if (!string.IsNullOrWhiteSpace(newEmail) && otherAccounts.Any(a => string.Equals(a.Email, newEmail, StringComparison.OrdinalIgnoreCase)))
            {
                new CustomMessageBox("Duplicate Email", "An account with this email address is already captured. Please use a unique email.", MessageBoxVisualStyle.Error).ShowDialog();
                return;
            }

            AccountName = newName;
            AccountEmail = newEmail;
            AccountDescription = AccountDescriptionInput.Text;
            AccountPassword = AccountPasswordInput.Password;
            DialogResult = true;
            Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}

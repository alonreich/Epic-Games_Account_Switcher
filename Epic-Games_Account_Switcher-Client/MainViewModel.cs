using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Epic_Games_Account_Switcher_Globals;
using System.Windows;

namespace Epic_Games_Account_Switcher_Client
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public class AccountMetadata
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
            public string ImagePath { get; set; }
        }

        public delegate Task<bool> RequestCaptureEventHandler(AccountMetadata metadata);
        public event RequestCaptureEventHandler RequestCapture;

        private ObservableCollection<Account> _accounts = new ObservableCollection<Account>();
        public ObservableCollection<Account> Accounts 
        { 
            get => _accounts; 
            set { _accounts = value; OnPropertyChanged(); } 
        }

        private bool _isBusy;
        public bool IsBusy 
        { 
            get => _isBusy; 
            set { _isBusy = value; OnPropertyChanged(); } 
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private string _busyStatus;
        public string BusyStatus 
        { 
            get => _busyStatus; 
            set { _busyStatus = value; OnPropertyChanged(); } 
        }

        private bool _isToastVisible;
        public bool IsToastVisible
        {
            get => _isToastVisible;
            set { _isToastVisible = value; OnPropertyChanged(); }
        }

        private string _toastMessage;
        public string ToastMessage
        {
            get => _toastMessage;
            set { _toastMessage = value; OnPropertyChanged(); }
        }

        public bool MinimizeOnSwitchSetting
        {
            get => AppSettings.Instance.MinimizeOnSwitch;
            set { AppSettings.Instance.MinimizeOnSwitch = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public bool PromptOnUnsavedLoginSetting
        {
            get => AppSettings.Instance.PromptOnUnsavedLogin;
            set { AppSettings.Instance.PromptOnUnsavedLogin = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public ICommand SwitchCommand { get; }
        public ICommand AddAccountCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SaveCurrentProfileCommand { get; }
        public ICommand BackupCommand { get; }
        public ICommand RestoreCommand { get; }

        public MainViewModel()
        {
            SwitchCommand = new RelayCommand<Account>(async (acc) => await SwitchToAccount(acc));
            AddAccountCommand = new RelayCommand(async () => await StartCapture());
            EditCommand = new RelayCommand<Account>((acc) => EditAccount(acc));
            DeleteCommand = new RelayCommand<Account>((acc) => DeleteAccount(acc));
            SaveCurrentProfileCommand = new RelayCommand<Account>(async (acc) => await RecaptureAccount(acc));
            BackupCommand = new RelayCommand(() => PerformBackup());
            RestoreCommand = new RelayCommand(async () => await PerformRestore());
        }

        public void Shutdown() { }

        public async Task LoadAccountsAsync()
        {
            IsLoading = true;
            try
            {
                var list = await Task.Run(() => AccountService.GetAccounts());
                Application.Current.Dispatcher.Invoke(new Action(() => {
                    Accounts.Clear();
                    foreach (var acc in list) Accounts.Add(acc);
                }));
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void LoadAccounts()
        {
            Task.Run(async () => await LoadAccountsAsync());
        }

        private async Task SwitchToAccount(Account account)
        {
            if (account == null || IsBusy) return;
            IsBusy = true;
            BusyStatus = "Switching to " + account.Name + "...";
            try
            {
                if (await AccountService.SwitchToAccount(account.Id, (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s))))
                {
                    ShowToast("Switched to " + account.Name);
                    if (AppSettings.Instance.MinimizeOnSwitch)
                    {
                        Application.Current.Dispatcher.Invoke(new Action(() => Application.Current.MainWindow.WindowState = WindowState.Minimized));
                    }
                    await LoadAccountsAsync();
                }
                else
                {
                    new CustomMessageBox("Switch Failed", "Failed to switch to " + account.Name + ". Check logs for details.").ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("CRITICAL UI SWITCH ERROR", ex);
                new CustomMessageBox("Critical Error", "An unexpected error occurred: " + ex.Message + "\n\nSee logs for details.").ShowDialog();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task StartCapture()
        {
            if (IsBusy) return;
            IsBusy = true;
            BusyStatus = "Detecting current login...";
            try
            {
                var detected = await AccountService.GetDetectedMetadataAsync();
                if (RequestCapture != null)
                {
                    await RequestCapture(new AccountMetadata { Id = detected.Item1, Name = detected.Item2, Email = detected.Item3, ImagePath = AccountService.GetDefaultProfileImagePath() });
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RecaptureAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
            var choice = new CustomMessageBox("Recapture Profile", "This will overwrite '" + acc.Name + "' with the session data from the current logged-in Epic account.\n\nContinue?", "Recapture", "Cancel").ShowDialog();
            if (choice != true) return;

            IsBusy = true;
            try
            {
                var result = await AccountService.CaptureCurrentAccount(
                    acc.Name, acc.Email, acc.ProfileImagePath, acc.Description, acc.Password,
                    (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)),
                    null, true);

                if (result.Item1)
                {
                    ShowToast("Profile updated!");
                    await LoadAccountsAsync();
                }
                else if (result.Item2 != null)
                {
                    Application.Current.Dispatcher.Invoke(() => new CustomMessageBox("Capture Failed", result.Item2, MessageBoxVisualStyle.Error).ShowDialog());
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void EditAccount(Account acc)
        {
            if (acc == null) return;
            var win = new CaptureAccountWindow(acc.Name, acc.Description, acc.Email, acc.ProfileImagePath, acc.Password, acc.Id, false, false) { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() == true)
            {
                acc.Name = win.AccountName;
                acc.Description = win.AccountDescription;
                acc.Email = win.AccountEmail;
                acc.ProfileImagePath = win.ProfileImagePath;
                acc.Password = win.AccountPassword;
                AccountService.SaveAccounts(Accounts.ToList());
                ShowToast("Account details updated!");
            }
        }

        private void DeleteAccount(Account acc)
        {
            if (acc == null) return;
            if (new CustomMessageBox("Delete Account", "Are you sure you want to delete '" + acc.Name + "'?", "Delete", "Cancel").ShowDialog() == true)
            {
                AccountService.DeleteAccount(acc.Id);
                Accounts.Remove(acc);
                ShowToast("Account deleted.");
            }
        }

        private void PerformBackup()
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Epic Switcher Backup (*.zip)|*.zip",
                FileName = "Epic_Accounts_Backup_" + DateTime.Now.ToString("yyyyMMdd") + ".zip"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    AccountService.BackupAccounts(sfd.FileName);
                    ShowToast("Backup successful!");
                }
                catch (Exception ex)
                {
                    new CustomMessageBox("Backup Failed", ex.Message).ShowDialog();
                }
            }
        }

        private async Task PerformRestore()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Epic Switcher Backup (*.zip)|*.zip"
            };

            if (ofd.ShowDialog() == true)
            {
                try
                {
                    await Task.Run(() => AccountService.RestoreAccounts(ofd.FileName));
                    await LoadAccountsAsync();
                    ShowToast("Restore successful!");
                }
                catch (Exception ex)
                {
                    new CustomMessageBox("Restore Failed", ex.Message).ShowDialog();
                }
            }
        }

        public void ShowToast(string message)
        {
            ToastMessage = message;
            IsToastVisible = true;
            Task.Run(async () => {
                await Task.Delay(2500);
                Application.Current.Dispatcher.Invoke(new Action(() => IsToastVisible = false));
            });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public RelayCommand(Action execute, Func<bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;
        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object parameter) => _canExecute == null || _canExecute((T)parameter);
        public void Execute(object parameter) => _execute((T)parameter);
        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }
}

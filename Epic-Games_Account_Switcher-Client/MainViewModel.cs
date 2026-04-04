using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Epic_Games_Account_Switcher_Globals;
using System.Windows;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace Epic_Games_Account_Switcher_Client
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<Account> _accounts = new();
        public ObservableCollection<Account> Accounts
        {
            get => _accounts;
            set { _accounts = value; OnPropertyChanged(); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set 
            { 
                _isBusy = value; 
                OnPropertyChanged(); 
                Application.Current.Dispatcher.Invoke(CommandManager.InvalidateRequerySuggested);
            }
        }

        private string _busyStatus;
        public string BusyStatus
        {
            get => _busyStatus;
            set { _busyStatus = value; OnPropertyChanged(); }
        }

        private string _toastMessage;
        public string ToastMessage
        {
            get => _toastMessage;
            set { _toastMessage = value; OnPropertyChanged(); }
        }

        private bool _isToastVisible;
        public bool IsToastVisible
        {
            get => _isToastVisible;
            set { _isToastVisible = value; OnPropertyChanged(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        
        public event System.Func<AccountMetadata, Task<bool>> RequestCapture;

        
        public event System.Func<string, string, Task> RequestInstruction;

        public class AccountMetadata
        {
            public string Name { get; set; }
            public string Email { get; set; }
            public string Description { get; set; }
            public string ImagePath { get; set; }
        }

        private ICommand _addAccountCommand;
        public ICommand AddAccountCommand => _addAccountCommand ??= new AsyncRelayCommand(AddAccountAsync, (obj) => !IsBusy && !IsLoading);

        private async Task AddAccountAsync(object _)
        {
            if (IsBusy || IsLoading) return;

            try
            {
                IsBusy = true;
                BusyStatus = "Detecting current Epic login...";

                var detected = AccountService.GetDetectedMetadata();
                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    BusyStatus = "Opening Epic Games Launcher...";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "com.epicgames.launcher://",
                        UseShellExecute = true
                    });

                    BusyStatus = "Waiting for Epic login...";
                    detected = await AccountService.WaitForDetectedMetadata(120, 1500);
                }

                IsBusy = false;
                BusyStatus = "";

                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    if (RequestInstruction != null)
                        await RequestInstruction("Detection Failed", "Could not detect a logged-in Epic account in time. Please login in Epic Games Launcher, then try Capture again.");
                    return;
                }

                if (RequestCapture != null)
                {
                    var captured = await RequestCapture(new AccountMetadata
                    {
                        Name = "",
                        Email = detected.Email,
                        ImagePath = AccountService.GetDefaultProfileImagePath()
                    });

                    if (captured)
                        await LoadAccountsAsync();
                }
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog("[ERROR] Add account flow failed.", ex);
                if (RequestInstruction != null)
                    await RequestInstruction("Error", "An unexpected error happened while trying to capture account data.");
            }
            finally
            {
                IsBusy = false;
                BusyStatus = "";
            }
        }

        private System.Threading.CancellationTokenSource _toastCts;
        public async void ShowToast(string message, int durationMs = 3000)
        {
            _toastCts?.Cancel();
            _toastCts = new System.Threading.CancellationTokenSource();
            var token = _toastCts.Token;

            ToastMessage = message;
            IsToastVisible = true;
            try
            {
                await Task.Delay(durationMs, token);
                IsToastVisible = false;
            }
            catch (TaskCanceledException) { }
            catch (System.Exception) { IsToastVisible = false; }
        }

        private ICommand _switchCommand;
        public ICommand SwitchCommand => _switchCommand ??= new AsyncRelayCommand(async (obj) =>
        {
            if (obj is Account acc) await SwitchAccount(acc);
        }, (obj) => !IsBusy && !IsLoading);

        private ICommand _editCommand;
        public ICommand EditCommand => _editCommand ??= new RelayCommand((obj) =>
        {
            if (obj is Account acc)
            {
                var captureWin = new CaptureAccountWindow(acc.Name, acc.Description, acc.Email, acc.ProfileImagePath) { Owner = Application.Current.MainWindow };
                if (captureWin.ShowDialog() == true)
                {
                    acc.Name = captureWin.AccountName;
                    acc.Description = captureWin.AccountDescription;
                    acc.Email = captureWin.AccountEmail;
                    acc.ProfileImagePath = captureWin.ProfileImagePath;
                    AccountService.SaveAccounts(new List<Account>(Accounts));
                    LoadAccounts();
                    ShowToast("Account updated!");
                }
            }
        }, (obj) => !IsBusy && !IsLoading);

        private ICommand _backupCommand;
        public ICommand BackupCommand => _backupCommand ??= new RelayCommand((obj) =>
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Account Backup (*.zip)|*.zip",
                FileName = "Epic_Accounts_Full_Backup.zip"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    AccountService.BackupAccounts(saveDialog.FileName);
                    ShowToast("Full backup created successfully!");
                }
                catch (System.Exception ex) { MessageBox.Show("Backup failed: " + ex.Message); }
            }
        }, (obj) => !IsBusy && !IsLoading);

        private ICommand _restoreCommand;
        public ICommand RestoreCommand => _restoreCommand ??= new AsyncRelayCommand(RestoreAccountsAsync, (obj) => !IsBusy && !IsLoading);

        private async Task RestoreAccountsAsync(object _)
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Account Backup (*.zip, *.json)|*.zip;*.json"
            };

            if (openDialog.ShowDialog() != true) return;

            try
            {
                if (openDialog.FileName.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                {
                    AccountService.RestoreAccounts(openDialog.FileName);
                    await LoadAccountsAsync();
                    ShowToast("Full restore completed!");
                    return;
                }

                
                var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(openDialog.FileName));
                if (imported == null) return;

                var current = AccountService.GetAccounts();
                bool changed = false;

                foreach (var imp in imported)
                {
                    var existing = current.FirstOrDefault(a => a.Id == imp.Id);
                    if (existing != null)
                    {
                        var res = MessageBox.Show($"Account '{imp.Name}' ({imp.Email}) already exists.\n\nDo you want to overwrite it with the backup data?", "Duplicate Detected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (res == MessageBoxResult.No) continue;

                        current.Remove(existing);
                    }

                    current.Add(imp);
                    changed = true;
                }

                if (changed)
                {
                    AccountService.SaveAccounts(current);
                    await LoadAccountsAsync();
                    ShowToast("JSON restore completed!");
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Restore failed: " + ex.Message);
            }
        }

        private ICommand _deleteCommand;
        public ICommand DeleteCommand => _deleteCommand ??= new RelayCommand((obj) =>
        {
            if (obj is Account acc)
            {
                var res = MessageBox.Show($"Are you sure you want to delete {acc.Name}?", "Delete Account", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes)
                {
                    AccountService.DeleteAccount(acc.Id);
                    LoadAccounts();
                    ShowToast($"Account {acc.Name} deleted.");
                }
            }
        }, (obj) => !IsBusy && !IsLoading);

        public MainViewModel()
        {
            
        }

        public void LoadAccounts() => _ = LoadAccountsAsync();

        public async Task LoadAccountsAsync()
        {
            Globals.WriteToLog("[UI] Loading accounts list from disk...");
            IsLoading = true;
            try
            {
                var list = await Task.Run(() => AccountService.GetAccounts());
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Accounts = new ObservableCollection<Account>(list);
                });
                Globals.WriteToLog($"[UI] Successfully loaded {list.Count} accounts.");
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog("[ERROR] Failed to load accounts list.", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task SwitchAccount(Account acc)
        {
            if (acc == null) return;
            Globals.WriteToLog($"[USER] Requested switch to account: {acc.Name} ({acc.Id})");
            
            if (IsBusy) 
            {
                Globals.WriteToLog("[UI] Switch ignored: UI is currently busy.");
                return;
            }

            
            var currentId = AccountService.GetCurrentAccountId();
            Globals.WriteToLog($"[CHECK] Current Active ID in registry: {currentId ?? "None"}");

            if (!string.IsNullOrEmpty(currentId) && !Accounts.Any(a => a.Id == currentId))
            {
                Globals.WriteToLog("[UI] Unsaved login detected. Prompting user for capture.");
                var res = MessageBox.Show("Your current login is not saved.\n\nWould you like to CAPTURE it before switching? Otherwise, your current login session will be lost.", "Unsaved Login Detected", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                
                if (res == MessageBoxResult.Cancel) 
                {
                    Globals.WriteToLog("[CANCEL] User cancelled the switch during unsaved login prompt.");
                    return;
                }

                if (res == MessageBoxResult.Yes)
                {
                    Globals.WriteToLog("[USER] Chose to CAPTURE unsaved login.");
                    var detected = AccountService.GetDetectedMetadata();
                    bool captureSuccess = false;
                    
                    if (RequestCapture != null)
                    {
                        captureSuccess = await RequestCapture(new AccountMetadata 
                        { 
                            Name = "", 
                            Email = detected.Email,
                            ImagePath = AccountService.GetDefaultProfileImagePath()
                        });
                    }
                    
                    if (!captureSuccess) 
                    {
                        Globals.WriteToLog("[ERROR] Capture failed or was cancelled. Aborting switch to prevent data loss.");
                        MessageBox.Show("Capture failed or cancelled. Switch aborted to prevent data loss.");
                        return;
                    }
                    await LoadAccountsAsync();
                }
                else
                {
                    Globals.WriteToLog("[USER] Chose to DISCARD unsaved login.");
                }
            }

            IsBusy = true;
            BusyStatus = "Preparing switch...";
            try
            {
                var success = await AccountService.SwitchToAccount(acc.Id, (status) => 
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        BusyStatus = status;
                        Globals.WriteToLog($"[SWITCH] {status}");
                    });
                });

                if (!success)
                {
                    Globals.WriteToLog($"[ERROR] Switch to {acc.Name} failed.");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        new CustomMessageBox("Switch Failed", "Could not switch accounts. Please ensure the Epic Games Launcher is not locked and try again.").ShowDialog();
                    });
                }
                else
                {
                    Globals.WriteToLog($"[SUCCESS] Switched to {acc.Name}.");
                    ShowToast($"Switched to {acc.Name}!");
                    if (AppSettings.MinimizeOnSwitch)
                    {
                        Globals.WriteToLog("[UI] Minimizing window as per settings.");
                        await Task.Delay(1000);
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Application.Current.MainWindow.WindowState = WindowState.Minimized;
                        });
                    }
                }
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog("[FATAL] Exception during account switch.", ex);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    new CustomMessageBox("Error", "An unexpected error occurred during account switch.").ShowDialog();
                });
            }
            finally
            {
                IsBusy = false;
                BusyStatus = "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly System.Action<object> _execute;
        private readonly System.Predicate<object> _canExecute;

        public RelayCommand(System.Action<object> execute, System.Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        public event System.EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly System.Func<object, Task> _execute;
        private readonly System.Predicate<object> _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(System.Func<object, Task> execute, System.Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return !_isExecuting && (_canExecute == null || _canExecute(parameter));
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;

            _isExecuting = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event System.EventHandler CanExecuteChanged;

        private void RaiseCanExecuteChanged()
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                CanExecuteChanged?.Invoke(this, System.EventArgs.Empty);
                CommandManager.InvalidateRequerySuggested();
            });
        }
    }
}

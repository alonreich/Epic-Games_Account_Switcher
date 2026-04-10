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

        private string _lastActiveAccountId;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        
        public event System.Func<AccountMetadata, Task<bool>> RequestCapture;

        private System.Timers.Timer _monitorTimer;

        private void StartBackgroundMonitoring()
        {
            _monitorTimer = new System.Timers.Timer(5000);
            _monitorTimer.Elapsed += async (s, e) => await UpdateActiveAccountStatus();
            _monitorTimer.AutoReset = true;
            _monitorTimer.Enabled = true;
        }

        private async Task UpdateActiveAccountStatus()
        {
            if (!Globals.IsProcessRunning("EpicGamesLauncher")) 
            {
                if (_lastActiveAccountId == null) return;
                _lastActiveAccountId = null;
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (Accounts == null) return;
                    foreach (var acc in Accounts) acc.IsActive = false;
                });
                return;
            }

            var currentId = await Task.Run(() => AccountService.GetCurrentAccountId());
            if (currentId == _lastActiveAccountId) return;

            var detected = await Task.Run(() => AccountService.GetDetectedMetadata());
            _lastActiveAccountId = detected.Id;
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (Accounts == null) return;
                foreach (var acc in Accounts)
                {
                    acc.IsActive = !string.IsNullOrEmpty(detected.Id) && acc.Id == detected.Id;
                }
            });
        }

        public class AccountMetadata
        {
            public string Name { get; set; }
            public string Email { get; set; }
            public string Description { get; set; }
            public string ImagePath { get; set; }
        }

        private ICommand _addAccountCommand;
        public ICommand AddAccountCommand => _addAccountCommand ??= new AsyncRelayCommand(AddAccountAsync, (obj) => !IsBusy && !IsLoading);

        private bool _isDetecting;
        public bool IsDetecting
        {
            get => _isDetecting;
            set { _isDetecting = value; OnPropertyChanged(); }
        }

        private async Task AddAccountAsync(object _)
        {
            if (IsBusy || IsLoading || IsDetecting) return;

            try
            {
                IsDetecting = true;
                BusyStatus = "Smart Detection Active: Log in to Epic Games Launcher...";

                var detected = AccountService.GetDetectedMetadata();
                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    AccountService.AutoLaunchEpicLauncher();
                    // Wait for login with high-frequency polling for responsiveness
                    detected = await AccountService.WaitForDetectedMetadata(300, 1000); 
                }

                if (string.IsNullOrWhiteSpace(detected.Id))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        new CustomMessageBox("Detection Timeout", "We waited 5 minutes but didn't see a login.\n\nPlease try again when you are ready to log in.").ShowDialog();
                    });
                    return;
                }

                IsDetecting = false;
                BusyStatus = "Account detected! Capturing...";

                if (RequestCapture != null)
                {
                    var captured = await RequestCapture(new AccountMetadata
                    {
                        Name = detected.Name == "N/A" ? "" : detected.Name,
                        Email = detected.Email == "N/A" ? "" : detected.Email,
                        ImagePath = AccountService.GetDefaultProfileImagePath()
                    });

                    if (captured)
                        await LoadAccountsAsync();
                }
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog("[ERROR] Add account flow failed.", ex);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    new CustomMessageBox("Error", "Something went wrong while waiting for your Epic login.").ShowDialog();
                });
            }
            finally
            {
                IsDetecting = false;
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

        private ICommand _saveCurrentProfileCommand;
        public ICommand SaveCurrentProfileCommand => _saveCurrentProfileCommand ??= new AsyncRelayCommand(async (obj) =>
        {
            if (obj is Account acc) await SaveCurrentProfileAsync(acc);
        }, (obj) => !IsBusy && !IsLoading);

        private async Task SaveCurrentProfileAsync(Account acc)
        {
            if (acc == null || IsBusy || IsLoading) return;

            Globals.WriteToLog($"=== CAPTAIN'S LOG: User requested 'SAVE CURRENT PROFILE' for: {acc.Name} ({acc.Id}) ===");
            IsBusy = true;
            BusyStatus = $"Recapturing session for {acc.Name}...";

            try
            {
                var success = await AccountService.CaptureCurrentAccount(
                    nameOverride: acc.Name,
                    emailOverride: acc.Email,
                    imagePathOverride: acc.ProfileImagePath,
                    descriptionOverride: acc.Description,
                    progress: (status) => Application.Current.Dispatcher.Invoke(() => BusyStatus = status)
                );

                if (success)
                {
                    Globals.WriteToLog($"=== CAPTAIN'S LOG: Successfully recaptured session data into existing profile: {acc.Name} ===");
                    ShowToast($"Profile '{acc.Name}' updated with current session!");
                    await LoadAccountsAsync();
                }
                else
                {
                    Globals.WriteToLog($"=== CAPTAIN'S LOG: Failed to recapture session for {acc.Name}. Likely detection error. ===");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        new CustomMessageBox("Capture Failed", "Could not detect a logged-in account to save into this profile.\n\nMake sure you are logged into the correct account in the Epic Games Launcher first.").ShowDialog();
                    });
                }
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog($"=== CAPTAIN'S LOG: FATAL ERROR during 'SAVE CURRENT PROFILE' for {acc.Name} ===", ex);
                ShowToast("Capture failed! Check logs.");
            }
            finally
            {
                IsBusy = false;
                BusyStatus = "";
            }
        }

        private ICommand _editCommand;
        public ICommand EditCommand => _editCommand ??= new RelayCommand((obj) =>
        {
            if (obj is Account acc)
            {
                Globals.WriteToLog($"[USER] Requested edit for account: {acc.Name}");
                var captureWin = new CaptureAccountWindow(acc.Name, acc.Description, acc.Email, acc.ProfileImagePath, acc.Password) { Owner = Application.Current.MainWindow };
                if (captureWin.ShowDialog() == true)
                {
                    acc.Name = captureWin.AccountName;
                    acc.Description = captureWin.AccountDescription;
                    acc.Email = captureWin.AccountEmail;
                    acc.Password = captureWin.AccountPassword;
                    acc.ProfileImagePath = captureWin.ProfileImagePath;
                    AccountService.SaveAccounts(new List<Account>(Accounts));
                    LoadAccounts();
                    ShowToast("Account updated!");
                    Globals.WriteToLog($"[UI] Account {acc.Name} updated successfully.");
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
                Globals.WriteToLog($"[USER] Requested full backup to: {saveDialog.FileName}");
                try
                {
                    AccountService.BackupAccounts(saveDialog.FileName);
                    ShowToast("Full backup created successfully!");
                }
                catch (System.Exception ex) 
                { 
                    Globals.WriteToLog("[ERROR] Manual backup failed.", ex);
                    new CustomMessageBox("Backup Failed", "Error: " + ex.Message).ShowDialog();
                }
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
            Globals.WriteToLog($"[USER] Requested restore from: {openDialog.FileName}");

            try
            {
                if (openDialog.FileName.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                {
                    AccountService.RestoreAccounts(openDialog.FileName);
                    await LoadAccountsAsync();
                    ShowToast("Full restore completed!");
                    return;
                }

                Globals.WriteToLog("[UI] Restoring from JSON file.");
                var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(openDialog.FileName));
                if (imported == null) return;

                var current = AccountService.GetAccounts();
                bool changed = false;

                foreach (var imp in imported)
                {
                    var existing = current.FirstOrDefault(a => a.Id == imp.Id);
                    if (existing != null)
                    {
                        var res = new CustomMessageBox("Duplicate Detected", $"Account '{imp.Name}' ({imp.Email}) already exists.\n\nDo you want to overwrite it with the backup data?", "Overwrite", "Skip").ShowDialog();
                        if (res != true) continue;

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
                    Globals.WriteToLog("[UI] JSON restore complete.");
                }
            }
            catch (System.Exception ex)
            {
                Globals.WriteToLog("[ERROR] Manual restore failed.", ex);
                new CustomMessageBox("Restore Failed", "Error: " + ex.Message).ShowDialog();
            }
        }

        private ICommand _deleteCommand;
        public ICommand DeleteCommand => _deleteCommand ??= new RelayCommand((obj) =>
        {
            if (obj is Account acc)
            {
                Globals.WriteToLog($"[USER] Requested deletion of account: {acc.Name}");
                var res = new CustomMessageBox("Delete Account", $"Are you sure you want to delete {acc.Name}?", "Delete", "Cancel").ShowDialog();
                if (res == true)
                {
                    AccountService.DeleteAccount(acc.Id);
                    LoadAccounts();
                    ShowToast($"Account {acc.Name} deleted.");
                    Globals.WriteToLog($"[UI] Account {acc.Name} deleted successfully.");
                }
            }
        }, (obj) => !IsBusy && !IsLoading);

        public MainViewModel()
        {
            Globals.WriteToLog("[UI] Initializing MainViewModel...");
            StartBackgroundMonitoring();
            Globals.WriteToLog("[UI] Starting AutoLoginService...");
            AutoLoginService.Start();
            AccountService.AutoLaunchEpicLauncher();
        }

        private ICommand _rescueAutoLoginCommand;
        public ICommand RescueAutoLoginCommand => _rescueAutoLoginCommand ??= new AsyncRelayCommand(async (obj) => 
        {
            Globals.WriteToLog("[USER] Manual Rescue triggered.");
            ShowToast("Manual Rescue: Searching for Login Screen...");
            await AutoLoginService.CheckAndPerformLogin();
        });

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

            if (AppSettings.PromptOnUnsavedLogin && !string.IsNullOrEmpty(currentId) && !Accounts.Any(a => a.Id == currentId))
            {
                Globals.WriteToLog("[UI] Unsaved login detected. Prompting user for capture.");
                var res = new CustomMessageBox("Unsaved Login Detected", "Your current login is not saved.\n\nWould you like to CAPTURE it before switching? Otherwise, your current login session will be lost.", "Capture", "Discard", "Cancel").ShowDialog();
                
                if (res == null) 
                {
                    Globals.WriteToLog("[CANCEL] User cancelled the switch during unsaved login prompt.");
                    return;
                }

                if (res == true)
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
                        new CustomMessageBox("Capture Failed", "Capture failed or cancelled. Switch aborted to prevent data loss.").ShowDialog();
                        return;
                    }
                    await LoadAccountsAsync();
                }
                else
                {
                    Globals.WriteToLog("[USER] Chose to DISCARD unsaved login.");
                    // Optional: we could ask "Don't ask again?" but for now just respect the setting.
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
                        new CustomMessageBox("Switch Failed", "An error occurred while switching the account. Check the log file for details.").ShowDialog();
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
                    new CustomMessageBox("Switch Error", ex.Message).ShowDialog();
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

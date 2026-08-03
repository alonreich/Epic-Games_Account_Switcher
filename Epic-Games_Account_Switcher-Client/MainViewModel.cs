using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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

        private CancellationTokenSource _operationCancellation;
        private int _toastGeneration;
        private NotifyCollectionChangedEventHandler _accountsChangedHandler;
        private ObservableCollection<Account> _accounts = new ObservableCollection<Account>();
        public ObservableCollection<Account> Accounts
        {
            get => _accounts;
            set
            {
                if (_accounts != null && _accountsChangedHandler != null) _accounts.CollectionChanged -= _accountsChangedHandler;
                _accounts = value;
                if (_accounts != null && _accountsChangedHandler != null) _accounts.CollectionChanged += _accountsChangedHandler;
                OnPropertyChanged();
                NotifyAccountCollectionStateChanged();
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
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

        private bool _canUndoReorder;
        public bool CanUndoReorder
        {
            get => _canUndoReorder;
            set { _canUndoReorder = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        private string _toastMessage;
        public string ToastMessage
        {
            get => _toastMessage;
            set { _toastMessage = value; OnPropertyChanged(); }
        }

        private AccountMetadata _unsavedLoginSuggestion;
        public AccountMetadata UnsavedLoginSuggestion
        {
            get => _unsavedLoginSuggestion;
            set
            {
                _unsavedLoginSuggestion = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnsavedLoginSuggestion));
                OnPropertyChanged(nameof(HasNoUnsavedLoginSuggestion));
                OnPropertyChanged(nameof(UnsavedLoginName));
                OnPropertyChanged(nameof(UnsavedLoginEmail));
                OnPropertyChanged(nameof(UnsavedLoginHint));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateSubtitle));
                OnPropertyChanged(nameof(UsageTrackerSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasUnsavedLoginSuggestion => UnsavedLoginSuggestion != null;
        public bool HasNoUnsavedLoginSuggestion => UnsavedLoginSuggestion == null;
        public string UnsavedLoginName => string.IsNullOrWhiteSpace(UnsavedLoginSuggestion?.Name) ? "Detected Epic Login" : UnsavedLoginSuggestion.Name;
        public string UnsavedLoginEmail => string.IsNullOrWhiteSpace(UnsavedLoginSuggestion?.Email) ? "Email not detected yet" : UnsavedLoginSuggestion.Email;
        public string UnsavedLoginHint => HasUnsavedLoginSuggestion ? "Seen just now. Save it once and future launches become one tap." : "No unsaved login detected.";

        public string EmptyStateTitle => HasUnsavedLoginSuggestion ? "Current Epic Login Detected" : "No Usage Timeline Yet";
        public string EmptyStateSubtitle => HasUnsavedLoginSuggestion
            ? "Save this live session now to start tracking last played time and switch history."
            : "Open Epic Games Launcher, sign in once, then let the app turn that login into your first fast-switch card.";

        public string UsageTrackerSummary
        {
            get
            {
                if (Accounts == null || Accounts.Count == 0)
                    return HasUnsavedLoginSuggestion ? "Ready to create the first usage card." : "No tracked switches yet.";

                var totalUses = Accounts.Sum(a => Math.Max(0, a.UsageCount));
                var recent = Accounts
                    .Where(a => a != null && a.HasUsageHistory)
                    .OrderByDescending(a => a.LastPlayed)
                    .FirstOrDefault();

                if (recent == null)
                    return Accounts.Count + " saved profiles. Usage tracking starts on the next switch.";

                return Accounts.Count + " profiles. " + totalUses + " tracked switches. Last used: " + recent.Name + " - " + recent.LastPlayedDisplay + ".";
            }
        }

        public bool CloseOnceLaunchedSetting
        {
            get => AppSettings.Instance.CloseOnceLaunched;
            set { AppSettings.Instance.CloseOnceLaunched = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public bool PromptOnUnsavedLoginSetting
        {
            get => AppSettings.Instance.PromptOnUnsavedLogin;
            set { AppSettings.Instance.PromptOnUnsavedLogin = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public bool OpenEpicLauncherOnStartupSetting
        {
            get => AppSettings.Instance.OpenEpicLauncherOnStartup;
            set { AppSettings.Instance.OpenEpicLauncherOnStartup = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public bool MinimizeToTraySetting
        {
            get => AppSettings.Instance.MinimizeToTray;
            set
            {
                if (AppSettings.Instance.MinimizeToTray == value) return;
                AppSettings.Instance.MinimizeToTray = value;
                AppSettings.SaveSettings();
                OnPropertyChanged();
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (Application.Current.MainWindow is MainWindow win)
                    {
                        win.UpdateTrayIconVisibility();
                    }
                });
            }
        }

        public bool CompareGameSettingsSetting
        {
            get => AppSettings.Instance.CompareGameSettings;
            set { AppSettings.Instance.CompareGameSettings = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public double ZoomFactor
        {
            get => AppSettings.Instance.ZoomFactor;
            set
            {
                var clamped = Math.Round(Math.Max(0.35, Math.Min(2.50, value)), 2);
                if (Math.Abs(AppSettings.Instance.ZoomFactor - clamped) < 0.001) return;
                AppSettings.Instance.ZoomFactor = clamped;
                AppSettings.SaveSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ZoomDisplayLabel));
            }
        }

        public string ZoomDisplayLabel => "ZOOM: " + Math.Round(ZoomFactor * 100) + "% (RESET DEFAULT)";

        public event Action OnAccountsChanged;

        public ICommand SwitchCommand { get; }
        public ICommand AddAccountCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand SaveCurrentProfileCommand { get; }
        public ICommand MoveAccountEarlierCommand { get; }
        public ICommand MoveAccountLaterCommand { get; }
        public ICommand BackupCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand CancelBusyCommand { get; }
        public ICommand UndoReorderCommand { get; }
        public ICommand CaptureUnsavedLoginCommand { get; }
        public ICommand DismissUnsavedLoginCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomResetCommand { get; }

        public bool IsAccountsEmpty => Accounts == null || Accounts.Count == 0;
        private List<Account> _undoAccountOrder;

        public MainViewModel()
        {
            _accountsChangedHandler = (s, e) => NotifyAccountCollectionStateChanged();
            if (Accounts != null) Accounts.CollectionChanged += _accountsChangedHandler;
            SwitchCommand = new AsyncRelayCommand<Account>(SwitchToAccount, acc => acc != null && !IsBusy);
            AddAccountCommand = new AsyncRelayCommand(StartCapture, () => !IsBusy);
            EditCommand = new RelayCommand<Account>(EditAccount, acc => acc != null && !IsBusy);
            DeleteCommand = new RelayCommand<Account>(DeleteAccount, acc => acc != null && !IsBusy);
            SaveCurrentProfileCommand = new AsyncRelayCommand<Account>(RecaptureAccount, acc => acc != null && !IsBusy);
            MoveAccountEarlierCommand = new RelayCommand<Account>(MoveAccountEarlier, acc => CanMoveAccount(acc, -1));
            MoveAccountLaterCommand = new RelayCommand<Account>(MoveAccountLater, acc => CanMoveAccount(acc, 1));
            BackupCommand = new AsyncRelayCommand(PerformBackup, () => !IsBusy);
            RestoreCommand = new AsyncRelayCommand(PerformRestore, () => !IsBusy);
            CancelBusyCommand = new RelayCommand(() => CancelCurrentOperation(), () => IsBusy && _operationCancellation != null && !_operationCancellation.IsCancellationRequested);
            UndoReorderCommand = new RelayCommand(UndoAccountOrder, () => CanUndoReorder && !IsBusy);
            CaptureUnsavedLoginCommand = new AsyncRelayCommand(CaptureSuggestedLogin, () => HasUnsavedLoginSuggestion && !IsBusy);
            DismissUnsavedLoginCommand = new RelayCommand(DismissSuggestedLogin, () => HasUnsavedLoginSuggestion && !IsBusy);
            ZoomInCommand = new RelayCommand(() => ZoomFactor += 0.05);
            ZoomOutCommand = new RelayCommand(() => ZoomFactor -= 0.05);
            ZoomResetCommand = new RelayCommand(() => ZoomFactor = 1.0);
        }

        public void Shutdown() { CancelCurrentOperation(); }

        private void NotifyAccountCollectionStateChanged()
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(NotifyAccountCollectionStateChanged);
                return;
            }
            OnPropertyChanged(nameof(IsAccountsEmpty));
            OnPropertyChanged(nameof(UsageTrackerSummary));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateSubtitle));
            CommandManager.InvalidateRequerySuggested();
            OnAccountsChanged?.Invoke();
        }

        private CancellationToken BeginBusyOperation(string status)
        {
            if (IsBusy) throw new InvalidOperationException("Another operation is already running.");
            _operationCancellation = new CancellationTokenSource();
            BusyStatus = status;
            IsBusy = true;
            return _operationCancellation.Token;
        }

        private void EndBusyOperation()
        {
            var cts = _operationCancellation;
            _operationCancellation = null;
            if (cts != null) cts.Dispose();
            IsBusy = false;
            BusyStatus = "";
        }

        private void CancelCurrentOperation()
        {
            try
            {
                if (_operationCancellation != null && !_operationCancellation.IsCancellationRequested)
                {
                    BusyStatus = "Cancelling...";
                    _operationCancellation.Cancel();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
            catch { }
        }

        public CancellationToken BeginExternalBusyOperation(string status)
        {
            return BeginBusyOperation(status);
        }

        public void EndExternalBusyOperation()
        {
            EndBusyOperation();
        }

        public async Task LoadAccountsAsync()
        {
            IsLoading = true;
            try
            {
                var list = await Task.Run(() => AccountService.GetAccounts());
                Application.Current.Dispatcher.Invoke(new Action(() => {
                    Accounts.Clear();
                    foreach (var acc in list) Accounts.Add(acc);
                    if (UnsavedLoginSuggestion != null && Accounts.Any(a => string.Equals(a.Id, UnsavedLoginSuggestion.Id, StringComparison.OrdinalIgnoreCase)))
                        UnsavedLoginSuggestion = null;
                    NotifyAccountCollectionStateChanged();
                }));
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void LoadAccounts()
        {
            _ = LoadAccountsSafelyAsync();
        }

        private async Task LoadAccountsSafelyAsync()
        {
            try { await LoadAccountsAsync(); }
            catch (Exception ex) { Globals.WriteToLog("Failed to load accounts", ex); }
        }

        public void CompareGameSettingsAndNotify()
        {
            if (!AppSettings.Instance.CompareGameSettings || Accounts.Count < 2) return;
            
            var allSettings = new Dictionary<string, Dictionary<string, string>>();
            foreach (var acc in Accounts)
            {
                var settings = FortniteSettingsParser.ExtractSettings(acc.Id);
                allSettings[acc.Name] = settings;
            }

            var keys = allSettings.SelectMany(x => x.Value.Keys).Distinct().ToList();
            var diffMessage = new System.Text.StringBuilder();

            foreach (var key in keys)
            {
                var values = new HashSet<string>();
                foreach (var accSettings in allSettings.Values)
                {
                    if (accSettings.TryGetValue(key, out var val)) values.Add(val);
                }
                
                if (values.Count > 1)
                {
                    diffMessage.AppendLine($"{key}:");
                    foreach (var kvp in allSettings)
                    {
                        var val = kvp.Value.TryGetValue(key, out var v) ? v : "Not Set";
                        diffMessage.AppendLine($"  - {kvp.Key}: {val}");
                    }
                    diffMessage.AppendLine();
                }
            }

            if (diffMessage.Length > 0)
            {
                Application.Current.Dispatcher.Invoke(() => 
                    new CustomMessageBox("Game Settings Differences", diffMessage.ToString().TrimEnd()).ShowDialog());
            }
            else
            {
                ShowToast("Game settings compared: All profiles match!");
            }
        }

        public async Task PromptForUnsavedLoginIfNeeded()
        {
            if (IsBusy || !AppSettings.Instance.PromptOnUnsavedLogin || RequestCapture == null)
            {
                UnsavedLoginSuggestion = null;
                return;
            }

            try
            {
                var detected = await AccountService.GetDetectedMetadataAsync();
                if (detected == null || string.IsNullOrWhiteSpace(detected.Item1))
                {
                    UnsavedLoginSuggestion = null;
                    return;
                }

                if (Accounts.Any(a => string.Equals(a.Id, detected.Item1, StringComparison.OrdinalIgnoreCase)))
                {
                    RemoveDismissedLoginId(detected.Item1);
                    UnsavedLoginSuggestion = null;
                    return;
                }

                if (IsDismissedLoginId(detected.Item1))
                {
                    UnsavedLoginSuggestion = null;
                    return;
                }

                UnsavedLoginSuggestion = new AccountMetadata
                {
                    Id = detected.Item1,
                    Name = detected.Item2,
                    Email = detected.Item3,
                    ImagePath = AccountService.GetDefaultProfileImagePath()
                };
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Failed to prompt for unsaved login", ex);
            }
        }

        private async Task CaptureSuggestedLogin()
        {
            if (IsBusy || UnsavedLoginSuggestion == null || RequestCapture == null) return;
            var metadata = UnsavedLoginSuggestion;
            try
            {
                var captured = await RequestCapture(metadata);
                if (captured)
                {
                    RemoveDismissedLoginId(metadata.Id);
                    UnsavedLoginSuggestion = null;
                    await LoadAccountsAsync();
                }
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Failed to capture suggested login", ex);
                new CustomMessageBox("Capture Failed", "The detected login could not be saved: " + ex.Message, MessageBoxVisualStyle.Error).ShowDialog();
            }
        }

        private void DismissSuggestedLogin()
        {
            if (!string.IsNullOrWhiteSpace(UnsavedLoginSuggestion?.Id))
            {
                var settings = AppSettings.Instance;
                if (settings.DismissedUnsavedLoginIds == null) settings.DismissedUnsavedLoginIds = new List<string>();
                if (!settings.DismissedUnsavedLoginIds.Any(id => string.Equals(id, UnsavedLoginSuggestion.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.DismissedUnsavedLoginIds.Add(UnsavedLoginSuggestion.Id);
                    AppSettings.SaveSettings();
                }
            }
            UnsavedLoginSuggestion = null;
            ShowToast("Detected login dismissed.");
        }

        private static bool IsDismissedLoginId(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return false;
            var dismissed = AppSettings.Instance.DismissedUnsavedLoginIds;
            return dismissed != null && dismissed.Any(id => string.Equals(id, accountId, StringComparison.OrdinalIgnoreCase));
        }

        private static void RemoveDismissedLoginId(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return;
            var dismissed = AppSettings.Instance.DismissedUnsavedLoginIds;
            if (dismissed == null) return;
            var removed = dismissed.RemoveAll(id => string.Equals(id, accountId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) AppSettings.SaveSettings();
        }

        private async Task SwitchToAccount(Account account)
        {
            if (account == null || IsBusy) return;
            var cancellationToken = BeginBusyOperation("Switching to " + account.Name + "...");
            try
            {
                if (await AccountService.SwitchToAccount(account.Id, (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)), cancellationToken))
                {
                    BusyStatus = "Verifying Epic sign-in...";
                    bool verified = false;
                    try
                    {
                        verified = await AccountService.WaitForLaunchedAccountAsync(account.Id, (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)), 45000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        await LoadAccountsAsync();
                        ShowToast("Switch completed! (Verification skipped)");
                        return;
                    }
                    await LoadAccountsAsync();

                    if (!verified)
                    {
                        new CustomMessageBox("Switch Verification Timed Out", "The session was restored, but Epic Games Launcher did not confirm the target account in time.\n\nThe app will stay open so you can verify manually.").ShowDialog();
                        return;
                    }

                    var closeOnceLaunched = AppSettings.Instance.CloseOnceLaunched;
                    Application.Current.Dispatcher.Invoke(new Action(() => {
                        var mainWindow = Application.Current.MainWindow;
                        if (mainWindow == null) return;
                        mainWindow.WindowState = WindowState.Minimized;
                        if (closeOnceLaunched) mainWindow.Close();
                    }));
                }
                else
                {
                    var detail = AccountService.LastSwitchFailureMessage;
                    new CustomMessageBox("Switch Failed", "Failed to switch to " + account.Name + "." + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\nReason: " + detail) + "\n\nOpen Show Logs for the full trace.").ShowDialog();
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast("Operation cancelled.");
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("CRITICAL UI SWITCH ERROR", ex);
                new CustomMessageBox("Critical Error", "An unexpected error occurred: " + ex.Message + "\n\nSee logs for details.").ShowDialog();
            }
            finally
            {
                EndBusyOperation();
            }
        }

        private async Task StartCapture()
        {
            if (IsBusy) return;
            if (UnsavedLoginSuggestion != null)
            {
                await CaptureSuggestedLogin();
                return;
            }

            var cancellationToken = BeginBusyOperation("Detecting current login...");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detected = await AccountService.GetDetectedMetadataAsync();
                if (detected == null || string.IsNullOrWhiteSpace(detected.Item1))
                {
                    EndBusyOperation();
                    new CustomMessageBox("No Active Login", "No Epic Games account is currently detected. Open Epic Games Launcher, sign in, then capture again.").ShowDialog();
                    return;
                }

                if (RequestCapture != null)
                {
                    EndBusyOperation();
                    await RequestCapture(new AccountMetadata { Id = detected.Item1, Name = detected.Item2, Email = detected.Item3, ImagePath = AccountService.GetDefaultProfileImagePath() });
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast("Operation cancelled.");
            }
            finally
            {
                if (IsBusy) EndBusyOperation();
            }
        }

        private async Task RecaptureAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
            var choice = new CustomMessageBox("Recapture Profile", "This will overwrite '" + acc.Name + "' with the session data from the current logged-in Epic account.\n\nContinue?", "Recapture", "Cancel").ShowDialog();
            if (choice != true) return;

            var cancellationToken = BeginBusyOperation("Recapturing " + acc.Name + "...");
            try
            {
                var result = await AccountService.CaptureCurrentAccount(
                    acc.Name, acc.Email, acc.ProfileImagePath, acc.Description, acc.Password,
                    (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)),
                    null, true, cancellationToken, acc.Id);

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
            catch (OperationCanceledException)
            {
                ShowToast("Operation cancelled.");
            }
            finally
            {
                EndBusyOperation();
            }
        }

        private void EditAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
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
            if (acc == null || IsBusy) return;
            if (new CustomMessageBox("Delete Account", "Are you sure you want to delete '" + acc.Name + "'?", "Delete", "Cancel").ShowDialog() == true)
            {
                AccountService.DeleteAccount(acc.Id);
                Accounts.Remove(acc);
                ShowToast("Account deleted.");
            }
        }

        private bool CanMoveAccount(Account acc, int offset)
        {
            if (acc == null || IsBusy || Accounts == null) return false;
            var fromIndex = Accounts.IndexOf(acc);
            var toIndex = fromIndex + offset;
            return fromIndex >= 0 && toIndex >= 0 && toIndex < Accounts.Count;
        }

        private void MoveAccountEarlier(Account acc) => MoveAccount(acc, -1);
        private void MoveAccountLater(Account acc) => MoveAccount(acc, 1);

        private void MoveAccount(Account acc, int offset)
        {
            if (!CanMoveAccount(acc, offset)) return;
            var previousOrder = Accounts.ToList();
            var fromIndex = Accounts.IndexOf(acc);
            Accounts.Move(fromIndex, fromIndex + offset);
            CommitAccountOrderChange(previousOrder);
        }

        public bool CommitAccountOrderChange(List<Account> previousOrder)
        {
            var currentOrder = Accounts == null ? new List<Account>() : Accounts.ToList();
            if (!HasAccountOrderChanged(previousOrder, currentOrder)) return false;

            _undoAccountOrder = previousOrder == null ? null : previousOrder.ToList();
            AccountService.SaveAccounts(currentOrder);
            ShowToast("Account order updated.", _undoAccountOrder != null);
            return true;
        }

        private void UndoAccountOrder()
        {
            if (!CanUndoReorder || _undoAccountOrder == null || Accounts == null) return;
            var restoreOrder = _undoAccountOrder.ToList();
            Accounts.Clear();
            foreach (var account in restoreOrder) Accounts.Add(account);
            AccountService.SaveAccounts(Accounts.ToList());
            _undoAccountOrder = null;
            ShowToast("Account order restored.");
        }

        private static bool HasAccountOrderChanged(IList<Account> previousOrder, IList<Account> currentOrder)
        {
            if (previousOrder == null || currentOrder == null) return false;
            if (previousOrder.Count != currentOrder.Count) return true;
            for (var i = 0; i < previousOrder.Count; i++)
            {
                var previousId = previousOrder[i] == null ? null : previousOrder[i].Id;
                var currentId = currentOrder[i] == null ? null : currentOrder[i].Id;
                if (!string.Equals(previousId, currentId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private async Task PerformBackup()
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Encrypted Epic Switcher Backup (*.egbackup)|*.egbackup",
                FileName = "Epic_Accounts_Backup_" + DateTime.Now.ToString("yyyyMMdd") + ".egbackup"
            };

            if (sfd.ShowDialog() == true)
            {
                var passwordWindow = new PasswordPromptWindow(
                    "Encrypt Backup",
                    "Choose a password for this backup. It will be required to restore the account profiles on any machine.",
                    true,
                    8) { Owner = Application.Current.MainWindow };
                if (passwordWindow.ShowDialog() != true) return;

                var cancellationToken = BeginBusyOperation("Creating backup...");
                try
                {
                    await Task.Run(() => AccountService.BackupAccounts(sfd.FileName, passwordWindow.EnteredPassword, null, cancellationToken), cancellationToken);
                    ShowToast("Backup successful!");
                }
                catch (OperationCanceledException)
                {
                    ShowToast("Operation cancelled.");
                }
                catch (Exception ex)
                {
                    new CustomMessageBox("Backup Failed", ex.Message).ShowDialog();
                }
                finally
                {
                    EndBusyOperation();
                }
            }
        }

        private async Task PerformRestore()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Epic Switcher Backup (*.egbackup;*.zip)|*.egbackup;*.zip|All Files (*.*)|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                var cancellationToken = BeginBusyOperation("Restoring backup...");
                try
                {
                    try
                    {
                        await Task.Run(() => AccountService.RestoreAccounts(ofd.FileName, cancellationToken), cancellationToken);
                    }
                    catch (AccountService.BackupPasswordRequiredException)
                    {
                        var passwordWindow = new PasswordPromptWindow(
                            "Restore Backup",
                            "This backup is password-protected. Enter the backup password to restore it.",
                            false,
                            1) { Owner = Application.Current.MainWindow };
                        if (passwordWindow.ShowDialog() != true) throw new OperationCanceledException();
                        await Task.Run(() => AccountService.RestoreAccounts(ofd.FileName, passwordWindow.EnteredPassword, cancellationToken), cancellationToken);
                    }
                    await LoadAccountsAsync();
                    ShowToast("Restore successful!");
                }
                catch (OperationCanceledException)
                {
                    ShowToast("Operation cancelled.");
                }
                catch (Exception ex)
                {
                    new CustomMessageBox("Restore Failed", ex.Message).ShowDialog();
                }
                finally
                {
                    EndBusyOperation();
                }
            }
        }

        public void ShowToast(string message, bool canUndoReorder = false)
        {
            if (!canUndoReorder) _undoAccountOrder = null;
            CanUndoReorder = canUndoReorder && _undoAccountOrder != null;
            var generation = Interlocked.Increment(ref _toastGeneration);
            ToastMessage = message;
            IsToastVisible = true;
            _ = HideToastLaterAsync(generation);
        }

        private async Task HideToastLaterAsync(int generation)
        {
            try
            {
                await Task.Delay(2500);
                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    if (generation == _toastGeneration) IsToastVisible = false;
                }));
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Failed to hide toast", ex);
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

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return !_isExecuting && (_canExecute == null || _canExecute());
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _execute(); }
            catch (Exception ex)
            {
                Globals.WriteToLog("Unhandled async command error", ex);
                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.Invoke(() => System.Windows.MessageBox.Show("Command execution failed:\n" + ex.Message, "Execution Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error));
                }
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }

    public class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T, Task> _execute;
        private readonly Func<T, bool> _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<T, Task> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            if (_isExecuting) return false;
            if (parameter == null && typeof(T).IsValueType) return false;
            var typed = parameter == null ? default(T) : (T)parameter;
            return _canExecute == null || _canExecute(typed);
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            var typed = parameter == null ? default(T) : (T)parameter;
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _execute(typed); }
            catch (Exception ex)
            {
                Globals.WriteToLog("Unhandled async command error", ex);
                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.Invoke(() => System.Windows.MessageBox.Show("Command execution failed:\n" + ex.Message, "Execution Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error));
                }
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }
}

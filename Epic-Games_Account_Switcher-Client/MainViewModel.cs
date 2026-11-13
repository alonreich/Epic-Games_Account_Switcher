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
    public partial class MainViewModel : INotifyPropertyChanged
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
                OnPropertyChanged(nameof(ShowUnsavedLoginBanner));
                OnPropertyChanged(nameof(UnsavedLoginName));
                OnPropertyChanged(nameof(UnsavedLoginEmail));
                OnPropertyChanged(nameof(UnsavedLoginHint));
                OnPropertyChanged(nameof(UnsavedLoginToolTip));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateSubtitle));
                OnPropertyChanged(nameof(UsageTrackerSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasUnsavedLoginSuggestion => UnsavedLoginSuggestion != null;
        public bool HasNoUnsavedLoginSuggestion => UnsavedLoginSuggestion == null;
        /// <summary>The banner is only shown next to existing accounts; with no accounts the empty state offers the same action.</summary>
        public bool ShowUnsavedLoginBanner => HasUnsavedLoginSuggestion && !IsAccountsEmpty;
        public string UnsavedLoginName => DetectedValue(UnsavedLoginSuggestion?.Name) ?? L.T("Signed-in Epic account");
        public string UnsavedLoginEmail => DetectedValue(UnsavedLoginSuggestion?.Email) ?? L.T("Email not detected yet");
        public string UnsavedLoginHint => L.T("Save it once, then switch to it with one click.");
        public string UnsavedLoginToolTip => L.T("{0} is signed in to Epic Games Launcher but not saved here yet.", UnsavedLoginName);

        /// <summary>Detection reports unknown values as empty or "N/A"; never show those to the user.</summary>
        private static string DetectedValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }

        public string EmptyStateTitle => HasUnsavedLoginSuggestion ? L.T("Save your signed-in Epic account") : L.T("No saved accounts yet");
        public string EmptyStateSubtitle
        {
            get
            {
                if (!HasUnsavedLoginSuggestion)
                    return L.T("Sign in to Epic Games Launcher, then click ADD SIGNED-IN ACCOUNT to save that account. Repeat for every account you want to switch between.");
                var name = DetectedValue(UnsavedLoginSuggestion.Name);
                var email = DetectedValue(UnsavedLoginSuggestion.Email);
                var who = name != null && email != null ? name + " (" + email + ")" : name ?? email ?? L.T("An Epic account");
                return L.T("{0} is signed in to Epic Games Launcher. Save it once, then switch to it with one click.", who);
            }
        }

        public string UsageTrackerSummary
        {
            get
            {
                if (Accounts == null || Accounts.Count == 0)
                    return HasUnsavedLoginSuggestion ? L.T("An Epic account is signed in but not saved yet.") : L.T("No accounts saved yet.");

                var totalUses = Accounts.Sum(a => Math.Max(0, a.UsageCount));
                var recent = Accounts
                    .Where(a => a != null && a.HasUsageHistory)
                    .OrderByDescending(a => a.LastPlayed)
                    .FirstOrDefault();

                var countText = Accounts.Count == 1 ? L.T("1 saved account") : L.T("{0} saved accounts", Accounts.Count);
                if (recent == null)
                    return L.T("{0}. Click an account to switch to it.", countText);

                var usesText = totalUses == 1 ? L.T("1 switch") : L.T("{0} switches", totalUses);
                return L.T("{0} · {1} · Last used: {2} ({3})", countText, usesText, recent.Name, recent.LastPlayedDisplay);
            }
        }

        public bool HasAccounts => !IsAccountsEmpty;

        public string CloseButtonToolTip => AppSettings.Instance.MinimizeToTray ? L.T("Close (keeps running in the notification area)") : L.T("Close");

        /// <summary>Set by the view while the pointer or keyboard focus is on the toast, so it stays visible.</summary>
        public bool IsToastHeld { get; set; }

        /// <summary>Raised after every switch attempt (true = switched). Used for tray notifications.</summary>
        public event Action<Account, bool> SwitchCompleted;

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
                OnPropertyChanged(nameof(CloseButtonToolTip));
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (Application.Current.MainWindow is MainWindow win)
                    {
                        win.UpdateTrayIconVisibility();
                    }
                });
            }
        }



        public bool KeepLauncherWindowPlacementSetting
        {
            get => AppSettings.Instance.KeepLauncherWindowPlacement;
            set { AppSettings.Instance.KeepLauncherWindowPlacement = value; AppSettings.SaveSettings(); OnPropertyChanged(); }
        }

        public string BackupToolTip => HasAccounts
            ? L.T("Save all your accounts to a password-protected backup file.")
            : L.T("Nothing to back up yet. Add an account first.");

        private const double MinZoom = 0.5;
        private const double MaxZoom = 1.75;
        private const double ZoomStep = 0.05;

        /// <summary>The zoom the user chose. What is shown is <see cref="EffectiveZoom"/>, which also respects the window size.</summary>
        public double ZoomFactor
        {
            get => Math.Max(MinZoom, Math.Min(MaxZoom, AppSettings.Instance.ZoomFactor));
            set
            {
                var clamped = Math.Round(Math.Max(MinZoom, Math.Min(MaxZoom, value)), 2);
                if (Math.Abs(AppSettings.Instance.ZoomFactor - clamped) < 0.001) return;
                AppSettings.Instance.ZoomFactor = clamped;
                AppSettings.SaveSettings();
                DialogWindowHelper.UiZoom = clamped;
                OnPropertyChanged();
                NotifyZoomChanged();
            }
        }

        private double _zoomFitLimit = MaxZoom;
        /// <summary>
        /// Largest zoom at which the dashboard still fits the current window (set by the view whenever the window is resized).
        /// A zoomed layout that no longer fits would push every control, including the zoom buttons, out of view.
        /// </summary>
        public double ZoomFitLimit
        {
            get => _zoomFitLimit;
            set
            {
                var limit = Math.Max(MinZoom, value);
                if (Math.Abs(limit - _zoomFitLimit) < 0.005) return;
                _zoomFitLimit = limit;
                OnPropertyChanged();
                NotifyZoomChanged();
            }
        }

        /// <summary>The zoom actually applied to the dashboard: the user's choice, reduced while the window is too small for it.</summary>
        public double EffectiveZoom => Math.Max(MinZoom, Math.Min(ZoomFactor, ZoomFitLimit));

        /// <summary>Zoom of the Settings popup (same rule as dialogs and menus: follows the zoom, never below 100%).</summary>
        public double PopupZoom => DialogWindowHelper.PopupZoom;

        public string ZoomDisplayLabel => Math.Round(EffectiveZoom * 100) + "%";

        private bool CanZoomIn => EffectiveZoom + ZoomStep <= Math.Min(MaxZoom, ZoomFitLimit) + 0.001;

        public string ZoomInToolTip => CanZoomIn || EffectiveZoom >= MaxZoom - 0.001
            ? L.T("Zoom in (Ctrl + Plus, or Ctrl + mouse wheel up)")
            : L.T("Make the window larger to zoom in further.");

        private void NotifyZoomChanged()
        {
            OnPropertyChanged(nameof(EffectiveZoom));
            OnPropertyChanged(nameof(PopupZoom));
            OnPropertyChanged(nameof(ZoomDisplayLabel));
            OnPropertyChanged(nameof(ZoomInToolTip));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ZoomIn()
        {
            if (EffectiveZoom >= MaxZoom - 0.001) return;
            if (!CanZoomIn)
            {
                ShowToast(L.T("Make the window larger to zoom in further."));
                return;
            }
            ZoomFactor = Math.Round(EffectiveZoom + ZoomStep, 2);
        }

        // Steps down from what is shown, so the first click is visible even while the window size limits the zoom.
        private void ZoomOut() => ZoomFactor = Math.Round(EffectiveZoom - ZoomStep, 2);

        public event Action OnAccountsChanged;

        public ICommand SwitchCommand { get; }
        public ICommand AddAccountCommand { get; }
        public ICommand FastLoginCommand { get; }
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
        public ICommand CopyPasswordCommand { get; }
        public ICommand ReloadSignInCommand { get; }

        public bool IsAccountsEmpty => Accounts == null || Accounts.Count == 0;
        private List<Account> _undoAccountOrder;

        public MainViewModel()
        {
            _accountsChangedHandler = (s, e) => NotifyAccountCollectionStateChanged();
            if (Accounts != null) Accounts.CollectionChanged += _accountsChangedHandler;
            SwitchCommand = new AsyncRelayCommand<Account>(SwitchToAccount, acc => acc != null && !IsBusy);
            AddAccountCommand = new AsyncRelayCommand(StartCapture, () => !IsBusy);
            FastLoginCommand = new RelayCommand(StartFastLogin, () => !IsBusy);
            EditCommand = new AsyncRelayCommand<Account>(EditAccount, acc => acc != null && !IsBusy);
            DeleteCommand = new AsyncRelayCommand<Account>(DeleteAccount, acc => acc != null && !IsBusy);
            SaveCurrentProfileCommand = new AsyncRelayCommand<Account>(RecaptureAccount, acc => acc != null && !IsBusy);
            MoveAccountEarlierCommand = new RelayCommand<Account>(MoveAccountEarlier, acc => CanMoveAccount(acc, -1));
            MoveAccountLaterCommand = new RelayCommand<Account>(MoveAccountLater, acc => CanMoveAccount(acc, 1));
            BackupCommand = new AsyncRelayCommand(PerformBackup, () => !IsBusy && HasAccounts);
            RestoreCommand = new AsyncRelayCommand(PerformRestore, () => !IsBusy);
            CancelBusyCommand = new RelayCommand(() => CancelCurrentOperation(), () => IsBusy && _operationCancellation != null && !_operationCancellation.IsCancellationRequested);
            UndoReorderCommand = new RelayCommand(UndoAccountOrder, () => CanUndoReorder && !IsBusy);
            CaptureUnsavedLoginCommand = new AsyncRelayCommand(CaptureSuggestedLogin, () => HasUnsavedLoginSuggestion && !IsBusy);
            DismissUnsavedLoginCommand = new RelayCommand(DismissSuggestedLogin, () => HasUnsavedLoginSuggestion && !IsBusy);
            ZoomInCommand = new RelayCommand(ZoomIn, () => CanZoomIn);
            ZoomOutCommand = new RelayCommand(ZoomOut, () => EffectiveZoom > MinZoom + 0.001);
            ZoomResetCommand = new RelayCommand(() => ZoomFactor = 1.0);
            CopyPasswordCommand = new RelayCommand<Account>(CopyPassword, acc => acc != null && acc.HasSavedPassword);
            ReloadSignInCommand = new AsyncRelayCommand<Account>(ReloadSignIn, acc => acc != null && !IsBusy);
            DialogWindowHelper.UiZoom = ZoomFactor;
        }

        /// <summary>Copies an account's saved password so it can be pasted when Epic Games asks to sign in again.</summary>
        private void CopyPassword(Account acc)
        {
            if (acc == null) return;
            var password = acc.Password;
            if (string.IsNullOrEmpty(password))
            {
                ShowToast(acc.HasSavedPassword
                    ? L.T("The saved password of '{0}' can't be read on this PC. Enter it again with EDIT.", acc.Name)
                    : L.T("No password is saved for '{0}'. Add one with EDIT.", acc.Name));
                return;
            }
            ShowToast(ClipboardHelper.CopySensitive(password)
                ? L.T("Password of '{0}' copied. It is removed from the clipboard after 30 seconds.", acc.Name)
                : L.T("The password could not be copied. Try again."));
        }

        /// <summary>Called every minute by the window so relative times on cards and in the summary stay current.</summary>
        public void RefreshRelativeTimes()
        {
            if (Accounts == null) return;
            foreach (var account in Accounts) account?.RefreshTimeLabels();
            OnPropertyChanged(nameof(UsageTrackerSummary));
        }

        public void Shutdown()
        {
            CancelCurrentOperation();
            try { _pendingAccountWrite.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        private Task _pendingAccountWrite = Task.FromResult(true);
        private readonly object _accountWriteLock = new object();

        /// <summary>Runs account file writes off the UI thread, one at a time and in order. Returns false (after logging) on failure.</summary>
        private Task<bool> QueueAccountWrite(Action write, bool reportFailure = true)
        {
            lock (_accountWriteLock)
            {
                var next = _pendingAccountWrite.ContinueWith(_ =>
                {
                    try { write(); return true; }
                    catch (Exception ex)
                    {
                        Globals.WriteToLog("Failed to write account data", ex);
                        if (reportFailure)
                        {
                            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                                new CustomMessageBox(L.T("Changes Not Saved"), L.T("Your latest account changes could not be saved. Check that the drive has free space, then try again.\n\nUse SHOW LOGS for details."), MessageBoxVisualStyle.Error).ShowDialog()));
                        }
                        return false;
                    }
                }, TaskScheduler.Default);
                _pendingAccountWrite = next;
                return next;
            }
        }

        private void QueueSaveAccounts()
        {
            var snapshot = Accounts == null ? new List<Account>() : Accounts.ToList();
            QueueAccountWrite(() => AccountService.SaveAccounts(snapshot));
        }

        private void NotifyAccountCollectionStateChanged()
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(NotifyAccountCollectionStateChanged);
                return;
            }
            OnPropertyChanged(nameof(IsAccountsEmpty));
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(BackupToolTip));
            OnPropertyChanged(nameof(ShowUnsavedLoginBanner));
            OnPropertyChanged(nameof(UsageTrackerSummary));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateSubtitle));
            CommandManager.InvalidateRequerySuggested();
            OnAccountsChanged?.Invoke();
        }

        private CancellationToken BeginBusyOperation(string status, bool cancellable = true)
        {
            if (IsBusy) throw new InvalidOperationException("Another operation is already running.");
            _operationCancellation = cancellable ? new CancellationTokenSource() : null;
            if (!cancellable)
            {
                BusyStatus = status;
                IsBusy = true;
                return CancellationToken.None;
            }
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
                    BusyStatus = L.T("Cancelling…");
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
                await _pendingAccountWrite;
                var list = await Task.Run(() => AccountService.GetAccounts());
                Application.Current.Dispatcher.Invoke(new Action(() => {
                    Accounts.Clear();
                    foreach (var acc in list) Accounts.Add(acc);
                    if (UnsavedLoginSuggestion != null && Accounts.Any(a => string.Equals(a.Id, UnsavedLoginSuggestion.Id, StringComparison.OrdinalIgnoreCase)))
                        UnsavedLoginSuggestion = null;
                    NotifyAccountCollectionStateChanged();
                }));
                var problem = AccountService.TakeAccountsLoadProblem();
                if (problem != null) _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => ShowAccountsLoadProblem(problem)));
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>An unreadable account list must never look like "no accounts yet": say so and offer a restore.</summary>
        private void ShowAccountsLoadProblem(string preservedCopy)
        {
            var kept = string.IsNullOrEmpty(preservedCopy)
                ? L.T("The file was left untouched, and changes can't be saved until it can be read or you restore a backup.")
                : L.T("A copy of the file was kept here:\n{0}", preservedCopy);
            var box = new CustomMessageBox(L.T("Saved Accounts Couldn't Be Read"),
                L.T("Your saved account list could not be read, so no accounts are shown. {0}\n\nRestore a backup to get your accounts back. Setup also keeps automatic backups in your Downloads folder.", kept),
                L.T("Restore Backup"), L.T("Close"), null, MessageBoxVisualStyle.Error);
            box.ShowDialog();
            if (box.Result == CustomDialogResult.Primary && RestoreCommand.CanExecute(null)) RestoreCommand.Execute(null);
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
                new CustomMessageBox(L.T("Account Not Saved"), L.T("The signed-in account could not be saved. Make sure Epic Games Launcher is still signed in, then try again.\n\nUse SHOW LOGS for details."), MessageBoxVisualStyle.Error).ShowDialog();
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
            ShowToast(L.T("OK, we won't offer to save that account again. Use ADD SIGNED-IN ACCOUNT any time."));
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

        /// <summary>
        /// Card / tray click. The account that is already signed in must not cost a launcher restart: it only brings the
        /// launcher up. The full restore stays available on the active card's menu (RELOAD SAVED SIGN-IN).
        /// </summary>
        private async Task SwitchToAccount(Account account)
        {
            if (account == null || IsBusy) return;
            string liveId = null;
            try { liveId = await Task.Run(() => AccountService.GetCurrentAccountId()); }
            catch (Exception ex) { Globals.WriteToLog("Could not read the signed-in account before switching", ex); }

            if (!string.IsNullOrWhiteSpace(liveId) && string.Equals(liveId, account.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (IsBusy) return;
                if (Globals.IsProcessRunning("EpicGamesLauncher"))
                {
                    ShowToast(L.T("'{0}' is already the active account in Epic Games Launcher.", account.Name));
                    return;
                }
                ShowToast(L.T("'{0}' is already the active account. Opening Epic Games Launcher…", account.Name));
                var opened = await Task.Run(() => AccountService.AutoLaunchEpicLauncher(false));
                if (!opened) ShowToast(L.T("Epic Games Launcher could not be opened. Open it yourself, then try again."));
                return;
            }

            await SwitchToAccountCore(account);
        }

        private async Task ReloadSignIn(Account acc)
        {
            if (acc == null || IsBusy) return;
            var choice = new CustomMessageBox(L.T("Reload Saved Sign-In?"),
                L.T("This closes Epic Games Launcher and restores the saved sign-in of '{0}' again.\n\nUse it when the launcher asks you to sign in although this account is shown as active.", acc.Name),
                L.T("Reload Sign-In"), L.T("Cancel"));
            if (choice.ShowDialog() != true || IsBusy) return;
            await SwitchToAccountCore(acc);
        }

        private async Task SwitchToAccountCore(Account account)
        {
            if (account == null || IsBusy) return;
            var cancellationToken = BeginBusyOperation(L.T("Switching to {0}…", account.Name));
            var switched = false;
            try
            {
                if (await AccountService.SwitchToAccount(account.Id, (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)), cancellationToken))
                {
                    BusyStatus = L.T("Verifying Epic sign-in…");
                    bool verified = false;
                    try
                    {
                        verified = await AccountService.WaitForLaunchedAccountAsync(account.Id, (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)), 45000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        await LoadAccountsAsync();
                        ShowToast(L.T("Switched to {0}. The sign-in check was skipped, so confirm the account in Epic Games Launcher.", account.Name));
                        SwitchCompleted?.Invoke(account, true);
                        return;
                    }
                    await LoadAccountsAsync();

                    if (!verified)
                    {
                        SwitchCompleted?.Invoke(account, false);
                        new CustomMessageBox(L.T("Switch Not Confirmed"), L.T("The saved sign-in for '{0}' was restored, but Epic Games Launcher didn't confirm that account within 45 seconds.\n\nCheck which account Epic Games Launcher shows. The app stays open so you can switch again if needed.", account.Name)).ShowDialog();
                        return;
                    }

                    SwitchCompleted?.Invoke(account, true);
                    switched = true;
                }
                else
                {
                    var detail = AccountService.LastSwitchFailureMessage;
                    SwitchCompleted?.Invoke(account, false);
                    new CustomMessageBox(L.T("Switch Failed"), (string.IsNullOrWhiteSpace(detail)
                        ? L.T("Couldn't switch to '{0}'.\n\nTry again. If it keeps failing, use SHOW LOGS and include the log when reporting the problem.", account.Name)
                        : L.T("Couldn't switch to '{0}'.\n\nReason: {1}\n\nTry again. If it keeps failing, use SHOW LOGS and include the log when reporting the problem.", account.Name, detail)), MessageBoxVisualStyle.Error).ShowDialog();
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast(L.T("Switch cancelled."));
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("CRITICAL UI SWITCH ERROR", ex);
                SwitchCompleted?.Invoke(account, false);
                new CustomMessageBox(L.T("Switch Failed"), L.T("Something unexpected stopped the switch to '{0}'.\n\nTry again. If it keeps happening, use SHOW LOGS for details.", account.Name), MessageBoxVisualStyle.Error).ShowDialog();
            }
            finally
            {
                EndBusyOperation();
            }

            if (!switched) return;
            // Show the result before the window gets out of the way, so the user knows the switch worked.
            ShowToast(L.T("Switched to {0}. Epic Games Launcher is signed in.", account.Name));
            await Task.Delay(1500);
            if (IsBusy) return;
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            // In frame mode the window stays: it is the frame around the launcher that is about to reopen.
            if (mainWindow.IsVisible && !AppSettings.Instance.ExperimentalLauncherFrame) mainWindow.WindowState = WindowState.Minimized;
            if (AppSettings.Instance.CloseOnceLaunched) mainWindow.Close();
        }

        private void StartFastLogin()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var loginWin = new EmbeddedLoginWindow { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow };
                if (loginWin.ShowDialog() == true)
                {
                    _ = LoadAccountsAsync();
                    ShowToast(L.T("Account saved. Click its card to switch to it."));
                }
            });
        }

        private async Task StartCapture()
        {
            if (IsBusy) return;
            if (UnsavedLoginSuggestion != null)
            {
                await CaptureSuggestedLogin();
                return;
            }

            var cancellationToken = BeginBusyOperation(L.T("Looking for the account signed in to Epic Games Launcher…"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detected = await AccountService.GetDetectedMetadataAsync();
                if (detected == null || string.IsNullOrWhiteSpace(detected.Item1))
                {
                    EndBusyOperation();
                    var choice = new CustomMessageBox(L.T("No Signed-In Account Found"),
                        L.T("No account is signed in to Epic Games Launcher right now.\n\nSign in here in a secure window, or open Epic Games Launcher, sign in there, and click ADD SIGNED-IN ACCOUNT again."),
                        L.T("Sign In Here"), L.T("Open Epic Games Launcher"), L.T("Cancel"));
                    choice.ShowDialog();
                    if (choice.Result == CustomDialogResult.Primary)
                    {
                        StartFastLogin();
                    }
                    else if (choice.Result == CustomDialogResult.Secondary)
                    {
                        var opened = await Task.Run(() => AccountService.AutoLaunchEpicLauncher(false));
                        ShowToast(opened ? L.T("Sign in to Epic Games Launcher, then click ADD SIGNED-IN ACCOUNT.") : L.T("Epic Games Launcher could not be opened. Open it yourself, then try again."));
                    }
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
                ShowToast(L.T("Operation cancelled."));
            }
            finally
            {
                if (IsBusy) EndBusyOperation();
            }
        }

        private async Task RecaptureAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
            string signedIn = null;
            try
            {
                var detected = await AccountService.GetDetectedMetadataAsync();
                if (detected != null) signedIn = string.IsNullOrWhiteSpace(detected.Item2) ? detected.Item3 : detected.Item2;
            }
            catch (Exception ex) { Globals.WriteToLog("Could not detect the signed-in account before replacing a saved sign-in", ex); }
            var question = string.IsNullOrWhiteSpace(signedIn)
                ? L.T("Replace the saved sign-in of '{0}' with the account currently signed in to Epic Games Launcher?", acc.Name)
                : L.T("Replace the saved sign-in of '{0}' with '{1}' (currently signed in to Epic Games Launcher)?", acc.Name, signedIn);
            var choice = new CustomMessageBox(L.T("Replace Saved Sign-In"),
                question + "\n\n" + L.T("The old saved sign-in of '{0}' is overwritten and can't be brought back. Its name, avatar and notes are kept.", acc.Name),
                L.T("Replace"), L.T("Cancel"), null, MessageBoxVisualStyle.Danger).ShowDialog();
            if (choice != true || IsBusy) return;

            var cancellationToken = BeginBusyOperation(L.T("Replacing the saved sign-in of {0}…", acc.Name));
            try
            {
                var result = await AccountService.CaptureCurrentAccount(
                    acc.Name, acc.Email, acc.ProfileImagePath, acc.Description, acc.Password,
                    (s) => Application.Current.Dispatcher.Invoke(new Action(() => BusyStatus = s)),
                    null, true, cancellationToken, acc.Id);

                if (result.Item1)
                {
                    ShowToast(L.T("Saved sign-in of '{0}' replaced.", acc.Name));
                    await LoadAccountsAsync();
                }
                else if (result.Item2 != null)
                {
                    Application.Current.Dispatcher.Invoke(() => new CustomMessageBox(L.T("Sign-In Not Replaced"), result.Item2, MessageBoxVisualStyle.Error).ShowDialog());
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast(L.T("Cancelled. The saved sign-in was not changed."));
            }
            finally
            {
                EndBusyOperation();
            }
        }

        private async Task EditAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
            var win = new CaptureAccountWindow(acc.Name, acc.Description, acc.Email, acc.ProfileImagePath, acc.Password, acc.Id, false, false) { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true) return;

            var before = new { acc.Name, acc.Description, acc.Email, acc.ProfileImagePath, acc.Password };
            acc.Name = win.AccountName;
            acc.Description = win.AccountDescription;
            acc.Email = win.AccountEmail;
            acc.ProfileImagePath = win.ProfileImagePath;
            acc.Password = win.AccountPassword;
            var snapshot = Accounts.ToList();
            // Only confirm once the file is really written; on failure the card shows the stored values again.
            if (await QueueAccountWrite(() => AccountService.SaveAccounts(snapshot), false))
            {
                ShowToast(L.T("Changes to '{0}' saved.", acc.Name));
                return;
            }
            acc.Name = before.Name;
            acc.Description = before.Description;
            acc.Email = before.Email;
            acc.ProfileImagePath = before.ProfileImagePath;
            acc.Password = before.Password;
            new CustomMessageBox(L.T("Changes Not Saved"), L.T("Your changes to '{0}' could not be saved, so the previous details were kept. Check that the drive has free space, then try again.\n\nUse SHOW LOGS for details.", win.AccountName), MessageBoxVisualStyle.Error).ShowDialog();
        }

        private async Task DeleteAccount(Account acc)
        {
            if (acc == null || IsBusy) return;
            var confirm = new CustomMessageBox(L.T("Delete Account"),
                L.T("Delete '{0}'?\n\nThis permanently removes the account and its saved Epic sign-in from this PC. Your Epic Games account itself is not affected.\n\nThis can't be undone unless you restore a backup.", acc.Name),
                L.T("Delete"), L.T("Cancel"), null, MessageBoxVisualStyle.Danger);
            if (confirm.ShowDialog() != true || IsBusy) return;

            BeginBusyOperation(L.T("Deleting {0}…", acc.Name), false);
            try
            {
                var id = acc.Id;
                if (await QueueAccountWrite(() => AccountService.DeleteAccount(id), false))
                {
                    Accounts.Remove(acc);
                    ShowToast(L.T("'{0}' deleted.", acc.Name));
                }
                else
                {
                    new CustomMessageBox(L.T("Delete Failed"), L.T("'{0}' could not be deleted. Close any program that might be using its files, then try again.\n\nUse SHOW LOGS for details.", acc.Name), MessageBoxVisualStyle.Error).ShowDialog();
                }
            }
            finally
            {
                EndBusyOperation();
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
            QueueAccountWrite(() => AccountService.SaveAccounts(currentOrder));
            ShowToast(L.T("Account order updated."), _undoAccountOrder != null);
            return true;
        }

        private void UndoAccountOrder()
        {
            if (!CanUndoReorder || _undoAccountOrder == null || Accounts == null) return;
            var restoreOrder = _undoAccountOrder.ToList();
            Accounts.Clear();
            foreach (var account in restoreOrder) Accounts.Add(account);
            QueueSaveAccounts();
            _undoAccountOrder = null;
            ShowToast(L.T("Account order restored."));
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

        private static string GetBackupStartFolder()
        {
            var last = AppSettings.Instance.LastBackupFolder;
            if (!string.IsNullOrWhiteSpace(last) && System.IO.Directory.Exists(last)) return last;
            return App.GetDownloadsFolder();
        }

        private static void RememberBackupFolder(string filePath)
        {
            try
            {
                var folder = System.IO.Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(folder)) return;
                AppSettings.Instance.LastBackupFolder = folder;
                AppSettings.SaveSettings();
            }
            catch (Exception ex) { Globals.WriteToLog("Could not remember the backup folder", ex); }
        }

        /// <summary>Plain-language reason for a backup/restore failure (technical details go to the log).</summary>
        private static string DescribeBackupError(Exception ex)
        {
            if (ex is AccountService.BackupPasswordInvalidException) return L.T("The password is not correct for this backup. Passwords are case-sensitive.");
            if (ex is System.IO.FileNotFoundException) return L.T("The backup file could not be found. It may have been moved or deleted.");
            if (ex is System.IO.InvalidDataException) return L.T("This file is not a valid Epic Switcher backup, or it is damaged.");
            if (ex is UnauthorizedAccessException) return L.T("Windows denied access to the file or folder. Choose a folder you can write to, such as Documents or Downloads.");
            if (ex is System.IO.IOException) return L.T("The file could not be read or written. Make sure it isn't open in another program and that the drive has free space.");
            return L.T("Something unexpected went wrong.");
        }

        private async Task PerformBackup()
        {
            // An empty backup would wipe the account list if it were restored later.
            if (!HasAccounts)
            {
                ShowToast(L.T("Nothing to back up yet. Add an account first."));
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L.T("Encrypted Epic Switcher Backup") + " (*.egbackup)|*.egbackup",
                FileName = "Epic_Accounts_Backup_" + DateTime.Now.ToString("yyyyMMdd") + ".egbackup",
                InitialDirectory = GetBackupStartFolder()
            };

            if (sfd.ShowDialog() == true)
            {
                RememberBackupFolder(sfd.FileName);
                var passwordWindow = new PasswordPromptWindow(
                    L.T("Encrypt Backup"),
                    L.T("Choose a password for this backup (at least 8 characters). You'll need it to restore these accounts on any PC, and it can't be recovered if you forget it."),
                    true,
                    8) { Owner = Application.Current.MainWindow };
                if (passwordWindow.ShowDialog() != true) return;

                var cancellationToken = BeginBusyOperation(L.T("Creating backup…"));
                var succeeded = false;
                int verifiedCount = 0;
                try
                {
                    await Task.Run(() => AccountService.BackupAccounts(sfd.FileName, passwordWindow.EnteredPassword, null, cancellationToken), cancellationToken);
                    // Never report a backup that cannot be read back (project rule 4.6).
                    verifiedCount = await Task.Run(() => AccountService.VerifyBackupFile(sfd.FileName, passwordWindow.EnteredPassword), cancellationToken);
                    succeeded = true;
                }
                catch (OperationCanceledException)
                {
                    ShowToast(L.T("Backup cancelled."));
                }
                catch (Exception ex)
                {
                    Globals.WriteToLog("Backup failed", ex);
                    new CustomMessageBox(L.T("Backup Failed"), L.T("The backup could not be created. {0}\n\nUse SHOW LOGS for details.", DescribeBackupError(ex)), MessageBoxVisualStyle.Error).ShowDialog();
                }
                finally
                {
                    EndBusyOperation();
                }

                if (!succeeded) return;
                var count = verifiedCount;
                var done = new CustomMessageBox(L.T("Backup Saved"),
                    (count == 1
                        ? L.T("1 account was saved to:\n{0}\n\nKeep the password somewhere safe. Without it this backup can't be restored.", sfd.FileName)
                        : L.T("{0} accounts were saved to:\n{1}\n\nKeep the password somewhere safe. Without it this backup can't be restored.", count, sfd.FileName)),
                    L.T("Open Folder"), L.T("Close"), null, MessageBoxVisualStyle.Success);
                done.ShowDialog();
                if (done.Result == CustomDialogResult.Primary)
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + sfd.FileName + "\""); }
                    catch (Exception ex) { Globals.WriteToLog("Could not open the backup folder", ex); }
                }
            }
        }

        private async Task PerformRestore()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L.T("Epic Switcher Backup") + " (*.egbackup;*.zip)|*.egbackup;*.zip|" + L.T("All Files") + " (*.*)|*.*",
                InitialDirectory = GetBackupStartFolder()
            };

            if (ofd.ShowDialog() != true) return;
            RememberBackupFolder(ofd.FileName);

            var currentCount = Accounts == null ? 0 : Accounts.Count;
            if (currentCount > 0)
            {
                var savedText = currentCount == 1 ? L.T("1 saved account") : L.T("{0} saved accounts", currentCount);
                var confirm = new CustomMessageBox(L.T("Replace Saved Accounts?"),
                    L.T("Restoring replaces all your accounts on this PC ({0}) with the accounts in:\n{1}\n\nAccounts you added or changed after this backup was made will be lost. Click BACKUP first if you want to keep them.", savedText, System.IO.Path.GetFileName(ofd.FileName)),
                    L.T("Replace My Accounts"), L.T("Cancel"), null, MessageBoxVisualStyle.Danger);
                if (confirm.ShowDialog() != true) return;
            }

            var cancellationToken = BeginBusyOperation(L.T("Restoring backup…"));
            try
            {
                try
                {
                    await Task.Run(() => AccountService.RestoreAccounts(ofd.FileName, cancellationToken), cancellationToken);
                }
                catch (AccountService.BackupPasswordRequiredException)
                {
                    var message = L.T("This backup is password-protected. Enter the password that was chosen when it was created.");
                    while (true)
                    {
                        var passwordWindow = new PasswordPromptWindow(L.T("Restore Backup"), message, false, 1) { Owner = Application.Current.MainWindow };
                        if (passwordWindow.ShowDialog() != true) throw new OperationCanceledException();
                        try
                        {
                            await Task.Run(() => AccountService.RestoreAccounts(ofd.FileName, passwordWindow.EnteredPassword, cancellationToken), cancellationToken);
                            break;
                        }
                        catch (AccountService.BackupPasswordInvalidException)
                        {
                            message = L.T("That password is not correct for this backup (passwords are case-sensitive). Try again, or cancel.");
                        }
                    }
                }
                await LoadAccountsAsync();
                var restored = Accounts == null ? 0 : Accounts.Count;
                ShowToast(restored == 1 ? L.T("Restore complete: 1 account restored.") : L.T("Restore complete: {0} accounts restored.", restored));
            }
            catch (OperationCanceledException)
            {
                ShowToast(L.T("Restore cancelled. Your saved accounts were not changed."));
            }
            catch (Exception ex)
            {
                Globals.WriteToLog("Restore failed", ex);
                new CustomMessageBox(L.T("Restore Failed"), L.T("The backup could not be restored. {0}\n\nUse SHOW LOGS for details.", DescribeBackupError(ex)), MessageBoxVisualStyle.Error).ShowDialog();
            }
            finally
            {
                EndBusyOperation();
            }
        }

        public void ShowToast(string message, bool canUndoReorder = false)
        {
            if (!canUndoReorder) _undoAccountOrder = null;
            CanUndoReorder = canUndoReorder && _undoAccountOrder != null;
            var generation = Interlocked.Increment(ref _toastGeneration);
            ToastMessage = message;
            IsToastVisible = true;
            // Long enough to read (and to reach UNDO); grows with the message length.
            var durationMs = CanUndoReorder ? 8000 : Math.Min(10000, 4000 + (message ?? "").Length * 50);
            _ = HideToastLaterAsync(generation, durationMs);
        }

        private async Task HideToastLaterAsync(int generation, int durationMs)
        {
            try
            {
                await Task.Delay(durationMs);
                while (IsToastHeld && generation == _toastGeneration) await Task.Delay(400);
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
        internal static string CommandErrorMessage => L.T("That action couldn't be completed because of an unexpected problem. Try again. If it keeps happening, use SHOW LOGS for details.");

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
                    Application.Current.Dispatcher.Invoke(() => new CustomMessageBox(L.T("Action Failed"), AsyncRelayCommand.CommandErrorMessage, MessageBoxVisualStyle.Error).ShowDialog());
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
                    Application.Current.Dispatcher.Invoke(() => new CustomMessageBox(L.T("Action Failed"), AsyncRelayCommand.CommandErrorMessage, MessageBoxVisualStyle.Error).ShowDialog());
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

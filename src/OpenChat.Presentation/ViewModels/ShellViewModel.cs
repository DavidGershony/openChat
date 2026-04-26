using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core.Configuration;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.Services;

namespace OpenChat.Presentation.ViewModels;

/// <summary>
/// Top-level ViewModel that owns the login flow and service lifecycle.
/// Login runs BEFORE any user-scoped services exist. After login, the correct
/// profile is set and services are created with the right DB/MLS paths.
/// </summary>
public class ShellViewModel : ViewModelBase
{
    private readonly ILogger<ShellViewModel> _logger = LoggingConfiguration.CreateLogger<ShellViewModel>();

    // Profile-independent services (created at startup, survive across logins)
    private readonly INostrService _nostrService;
    private readonly IPlatformClipboard _clipboard;
    private readonly IQrCodeGenerator _qrCodeGenerator;
    private readonly IPlatformLauncher _launcher;
    private readonly ISecureStorage _secureStorage;

    // Profile-dependent services (created after login, disposed on logout)
    private IStorageService? _storageService;
    private IMlsService? _mlsService;
    private IMessageService? _messageService;
    private bool _sessionActivated;

    // Multi-account state
    private string? _previousActiveAccountPubKey;

    [Reactive] public bool IsLoggedIn { get; set; }
    [Reactive] public MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// All known accounts from the registry, ordered by last active.
    /// Used by the account switcher UI.
    /// </summary>
    public ObservableCollection<AccountEntry> KnownAccounts { get; } = new();

    /// <summary>
    /// The currently active account entry (for highlighting in the switcher).
    /// </summary>
    [Reactive] public AccountEntry? ActiveAccountEntry { get; set; }

    /// <summary>
    /// True when the login screen was triggered by "Add Account" (shows Cancel button).
    /// </summary>
    [Reactive] public bool IsAddingAccount { get; set; }

    /// <summary>
    /// True when the account switcher overlay is visible.
    /// </summary>
    [Reactive] public bool ShowAccountSwitcher { get; set; }

    public LoginViewModel LoginViewModel { get; }

    /// <summary>
    /// Factory for creating the MLS service. Set by the platform layer (Desktop/Android)
    /// since the implementation depends on the backend (Managed vs Rust).
    /// </summary>
    public Func<IStorageService, IMlsService>? MlsServiceFactory { get; set; }

    public ReactiveCommand<string, Unit> SwitchAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAccountSwitcherCommand { get; }

    public ShellViewModel(
        INostrService nostrService,
        ISecureStorage secureStorage,
        IPlatformClipboard clipboard,
        IQrCodeGenerator qrCodeGenerator,
        IPlatformLauncher launcher)
    {
        _nostrService = nostrService;
        _secureStorage = secureStorage;
        _clipboard = clipboard;
        _qrCodeGenerator = qrCodeGenerator;
        _launcher = launcher;

        // LoginViewModel uses NostrService for crypto and ExternalSignerService for Amber.
        // It does NOT get StorageService — saving happens here after profile is set.
        LoginViewModel = new LoginViewModel(nostrService, qrCodeGenerator);

        // When LoginViewModel produces a user, complete the login flow
        LoginViewModel.WhenAnyValue(x => x.LoggedInUser)
            .Where(user => user != null)
            .Subscribe(user => OnLoginCompleted(user!));

        // Multi-account commands
        SwitchAccountCommand = ReactiveCommand.CreateFromTask<string>(SwitchAccountAsync);
        AddAccountCommand = ReactiveCommand.CreateFromTask(AddAccountAsync);
        CancelAddAccountCommand = ReactiveCommand.CreateFromTask(CancelAddAccountAsync);
        ToggleAccountSwitcherCommand = ReactiveCommand.Create(() => { ShowAccountSwitcher = !ShowAccountSwitcher; });

        // Load the account registry
        AccountRegistryService.Load();
        RefreshKnownAccounts();

        // Try auto-login on startup
        _ = TryAutoLoginAsync();
    }

    private async Task TryAutoLoginAsync()
    {
        try
        {
            // If no profile was set yet (e.g. Android, or desktop without --profile),
            // try to auto-derive from account registry (or legacy last_user.json).
            if (!ProfileConfiguration.IsCustomProfile)
            {
                var activeAccount = AccountRegistryService.GetActiveAccount();
                var lastPubKey = activeAccount?.PublicKeyHex;

                if (lastPubKey != null)
                {
                    ProfileConfiguration.SetProfileForAccount(lastPubKey);
                    _logger.LogInformation("Auto-derived profile {Profile} from account registry", ProfileConfiguration.ProfileName);
                }
            }

            // If profile was set (from registry, auto-derived above, or --profile), check for saved user
            if (ProfileConfiguration.IsCustomProfile)
            {
                _logger.LogInformation("Checking for saved user in profile {Profile}", ProfileConfiguration.ProfileName);
                var storageService = CreateStorageService();
                await storageService.InitializeAsync();
                var savedUser = await storageService.GetCurrentUserAsync();

                if (savedUser != null)
                {
                    _logger.LogInformation("Auto-login: found saved user {Npub}...", savedUser.Npub?[..Math.Min(12, savedUser.Npub.Length)]);
                    await ActivateSession(savedUser, storageService);
                    return;
                }

                _logger.LogInformation("No saved user in profile {Profile}, showing login screen", ProfileConfiguration.ProfileName);
            }

            // No saved user or default profile — show login screen
            ShowLoginView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-login failed, showing login screen");
            ShowLoginView();
        }
    }

    private async void OnLoginCompleted(User user)
    {
        try
        {
            _logger.LogInformation("Login completed for {Npub}..., setting up profile", user.Npub?[..Math.Min(12, user.Npub.Length)]);

            // Derive profile from the user's public key (unless --profile was explicit)
            if (!ProfileConfiguration.WasExplicitlySet)
            {
                ProfileConfiguration.SetProfileForAccount(user.PublicKeyHex);
                _logger.LogInformation("Profile set to {Profile} (derived from npub)", ProfileConfiguration.ProfileName);
            }

            // Create profile-dependent services with the correct DB path
            var storageService = CreateStorageService();
            await storageService.InitializeAsync();
            await storageService.SaveCurrentUserAsync(user);

            // Update the account registry
            AccountRegistryService.AddOrUpdateAccount(new AccountEntry
            {
                PublicKeyHex = user.PublicKeyHex,
                Npub = user.Npub ?? string.Empty,
                DisplayName = user.DisplayName,
                AvatarUrl = user.AvatarUrl,
                IsRemoteSigner = user.IsRemoteSigner,
                AddedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            });
            AccountRegistryService.SetActiveAccount(user.PublicKeyHex);

            IsAddingAccount = false;
            RefreshKnownAccounts();

            await ActivateSession(user, storageService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete login");
            LoginViewModel.ErrorMessage = $"Login failed: {ex.Message}";
            ShowLoginView();
        }
    }

    private async Task ActivateSession(User user, IStorageService storageService)
    {
        if (_sessionActivated)
        {
            _logger.LogWarning("ActivateSession called but session already active — ignoring duplicate (signer auto-login race)");
            return;
        }
        _sessionActivated = true;

        _storageService = storageService;

        // Create MLS service via platform factory
        _mlsService = MlsServiceFactory?.Invoke(storageService)
            ?? new ManagedMlsService(storageService);

        // Create MessageService
        _messageService = new MessageService(storageService, _nostrService, _mlsService);

        // Create MainViewModel with all services
        var mainVm = new MainViewModel(
            _messageService, _nostrService, storageService, _mlsService,
            _clipboard, _qrCodeGenerator, _launcher,
            onLogoutRequested: OnLogoutRequested);

        // Pass the external signer if the user logged in via Amber
        mainVm.ExternalSigner = LoginViewModel.ExternalSigner;

        // Set the user
        mainVm.CurrentUser = user;
        mainVm.IsLoggedIn = true;

        // Set MainViewModel on the UI thread — triggers XAML visibility switch
        // [Reactive] properties must be set on the UI thread for Avalonia bindings to update reliably
        var tcs = new TaskCompletionSource();
        RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            MainViewModel = mainVm;
            IsLoggedIn = true;
            tcs.SetResult();
            return System.Reactive.Disposables.Disposable.Empty;
        });
        await tcs.Task;

        _logger.LogInformation("Session activated for {Npub}... in profile {Profile}",
            user.Npub?[..Math.Min(12, user.Npub.Length)], ProfileConfiguration.ProfileName);

        ActiveAccountEntry = AccountRegistryService.GetActiveAccount();
        RefreshKnownAccounts();

        // Initialize in the background (relay connections, MLS, etc.)
        await MainViewModel.InitializeAfterLoginAsync();
    }

    /// <summary>
    /// Tears down the current session without touching the account registry.
    /// Used as the common path for logout, account switch, and add-account.
    /// </summary>
    private async Task TeardownSessionAsync()
    {
        _logger.LogInformation("Tearing down current session");

        try
        {
            // Update account metadata in registry before teardown (display name/avatar may have changed)
            if (MainViewModel?.CurrentUser is { } currentUser)
            {
                AccountRegistryService.AddOrUpdateAccount(new AccountEntry
                {
                    PublicKeyHex = currentUser.PublicKeyHex,
                    Npub = currentUser.Npub ?? string.Empty,
                    DisplayName = currentUser.DisplayName,
                    AvatarUrl = currentUser.AvatarUrl,
                    IsRemoteSigner = currentUser.IsRemoteSigner,
                    AddedAt = AccountRegistryService.GetActiveAccount()?.AddedAt ?? DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow
                });
            }

            // Clear external signer from NostrService
            _nostrService.SetExternalSigner(null);
            ChatViewModel.MediaUploadService?.SetExternalSigner(null);

            // Reset MLS state
            if (_mlsService != null)
                await _mlsService.ResetAsync();

            // Disconnect relays
            await _nostrService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session teardown");
        }

        // Dispose session-scoped services
        (_messageService as IDisposable)?.Dispose();
        _storageService = null;
        _mlsService = null;
        _messageService = null;
        _sessionActivated = false;

        // Update UI on main thread
        var tcs = new TaskCompletionSource();
        RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            MainViewModel = null;
            IsLoggedIn = false;
            ActiveAccountEntry = null;
            tcs.SetResult();
            return System.Reactive.Disposables.Disposable.Empty;
        });
        await tcs.Task;
    }

    /// <summary>
    /// Full logout: tears down session, clears active marker (account stays in registry).
    /// Shows login screen or account switcher if other accounts exist.
    /// </summary>
    private async void OnLogoutRequested()
    {
        _logger.LogInformation("Logout requested");

        // Clear current user from DB before teardown
        try
        {
            if (_storageService != null)
                await _storageService.ClearCurrentUserAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear current user from DB");
        }

        await TeardownSessionAsync();

        // Clear active account but keep in registry for easy re-login
        AccountRegistryService.ClearActiveAccount();
        ProfileConfiguration.SetProfile(null);

        RefreshKnownAccounts();
        ShowLoginView();
    }

    /// <summary>
    /// Switches to a different known account. Tears down current session,
    /// sets new profile, and activates the target account.
    /// </summary>
    public async Task SwitchAccountAsync(string targetPubKeyHex)
    {
        if (ActiveAccountEntry != null &&
            string.Equals(ActiveAccountEntry.PublicKeyHex, targetPubKeyHex, StringComparison.OrdinalIgnoreCase))
        {
            ShowAccountSwitcher = false;
            return;
        }

        _logger.LogInformation("Switching account to {PubKey}...", targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
        ShowAccountSwitcher = false;

        if (IsLoggedIn)
            await TeardownSessionAsync();

        // Set profile for the target account
        ProfileConfiguration.SetProfileForAccount(targetPubKeyHex);
        AccountRegistryService.SetActiveAccount(targetPubKeyHex);

        try
        {
            var storageService = CreateStorageService();
            await storageService.InitializeAsync();
            var savedUser = await storageService.GetCurrentUserAsync();

            if (savedUser != null)
            {
                await ActivateSession(savedUser, storageService);
                return;
            }

            // User not found in DB — profile may be corrupted
            _logger.LogWarning("No saved user found for account {PubKey}..., removing from registry",
                targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
            AccountRegistryService.RemoveAccount(targetPubKeyHex);
            RefreshKnownAccounts();
            ProfileConfiguration.SetProfile(null);
            ShowLoginView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch to account {PubKey}...", targetPubKeyHex[..Math.Min(16, targetPubKeyHex.Length)]);
            AccountRegistryService.RemoveAccount(targetPubKeyHex);
            RefreshKnownAccounts();
            ProfileConfiguration.SetProfile(null);
            ShowLoginView();
        }
    }

    /// <summary>
    /// Opens the login screen to add a new account. Stores the current account
    /// so it can be restored if the user cancels.
    /// </summary>
    public async Task AddAccountAsync()
    {
        _logger.LogInformation("Add account requested");
        ShowAccountSwitcher = false;

        _previousActiveAccountPubKey = ActiveAccountEntry?.PublicKeyHex;
        IsAddingAccount = true;

        if (IsLoggedIn)
            await TeardownSessionAsync();

        ProfileConfiguration.SetProfile(null);
        LoginViewModel.IsAddAccountMode = true;
        ShowLoginView();
    }

    /// <summary>
    /// Cancels "Add Account" and returns to the previously active account.
    /// </summary>
    public async Task CancelAddAccountAsync()
    {
        _logger.LogInformation("Add account cancelled, returning to previous account");
        IsAddingAccount = false;

        if (_previousActiveAccountPubKey != null)
        {
            await SwitchAccountAsync(_previousActiveAccountPubKey);
            _previousActiveAccountPubKey = null;
        }
        else
        {
            ShowLoginView();
        }
    }

    /// <summary>
    /// Removes an account from the registry. If it's the active account,
    /// tears down the session first and switches to another account or shows login.
    /// </summary>
    public async Task RemoveAccountAsync(string publicKeyHex, bool deleteData = false)
    {
        _logger.LogInformation("Removing account {PubKey}...", publicKeyHex[..Math.Min(16, publicKeyHex.Length)]);

        var isActive = ActiveAccountEntry != null &&
            string.Equals(ActiveAccountEntry.PublicKeyHex, publicKeyHex, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            try
            {
                if (_storageService != null)
                    await _storageService.ClearCurrentUserAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear current user from DB during account removal");
            }
            await TeardownSessionAsync();
            ProfileConfiguration.SetProfile(null);
        }

        AccountRegistryService.RemoveAccount(publicKeyHex, deleteData);
        RefreshKnownAccounts();

        if (isActive)
        {
            // Switch to the most recently active remaining account, or show login
            var accounts = AccountRegistryService.GetAccounts();
            if (accounts.Count > 0)
            {
                await SwitchAccountAsync(accounts[0].PublicKeyHex);
            }
            else
            {
                ShowLoginView();
            }
        }
    }

    private void RefreshKnownAccounts()
    {
        var accounts = AccountRegistryService.GetAccounts();
        KnownAccounts.Clear();
        foreach (var account in accounts)
            KnownAccounts.Add(account);
    }

    private void ShowLoginView()
    {
        LoginViewModel.Reset();
    }

    private IStorageService CreateStorageService()
    {
        _logger.LogInformation("Creating StorageService for profile {Profile} (DB: {Path})",
            ProfileConfiguration.ProfileName, ProfileConfiguration.DatabasePath);
        return new StorageService(ProfileConfiguration.DatabasePath, _secureStorage);
    }
}

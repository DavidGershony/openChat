using System;
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

    [Reactive] public bool IsLoggedIn { get; set; }
    [Reactive] public MainViewModel? MainViewModel { get; set; }

    public LoginViewModel LoginViewModel { get; }

    /// <summary>
    /// Factory for creating the MLS service. Set by the platform layer (Desktop/Android)
    /// since the implementation depends on the backend (Managed vs Rust).
    /// </summary>
    public Func<IStorageService, IMlsService>? MlsServiceFactory { get; set; }

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

        // Try auto-login on startup
        _ = TryAutoLoginAsync();
    }

    private async Task TryAutoLoginAsync()
    {
        try
        {
            // If profile was already set (from last_user.json or --profile), check for saved user
            if (ProfileConfiguration.IsCustomProfile)
            {
                _logger.LogInformation("Checking for saved user in profile {Profile}", ProfileConfiguration.ProfileName);
                var storageService = CreateStorageService();
                await storageService.InitializeAsync();
                var savedUser = await storageService.GetCurrentUserAsync();

                if (savedUser != null)
                {
                    _logger.LogInformation("Auto-login: found saved user {Npub}", savedUser.Npub);
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
            _logger.LogInformation("Login completed for {Npub}, setting up profile", user.Npub);

            // Derive profile from the user's public key (unless --profile was explicit)
            if (!ProfileConfiguration.WasExplicitlySet)
            {
                var profileName = ProfileConfiguration.DeriveProfileName(user.PublicKeyHex);
                ProfileConfiguration.SetProfile(profileName);
                _logger.LogInformation("Profile set to {Profile} (derived from npub)", profileName);
            }

            // Create profile-dependent services with the correct DB path
            var storageService = CreateStorageService();
            await storageService.InitializeAsync();
            await storageService.SaveCurrentUserAsync(user);

            // Update the last-user registry
            ProfileConfiguration.WriteLastUserPubKey(user.PublicKeyHex);

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

        _logger.LogInformation("Session activated for {Npub} in profile {Profile}",
            user.Npub, ProfileConfiguration.ProfileName);

        // Initialize in the background (relay connections, MLS, etc.)
        await MainViewModel.InitializeAfterLoginAsync();
    }

    private async void OnLogoutRequested()
    {
        _logger.LogInformation("Logout requested — tearing down session");

        try
        {
            // Clear external signer from NostrService
            _nostrService.SetExternalSigner(null);
            ChatViewModel.MediaUploadService?.SetExternalSigner(null);

            // Reset MLS state
            if (_mlsService != null)
                await _mlsService.ResetAsync();

            // Clear current user from DB
            if (_storageService != null)
                await _storageService.ClearCurrentUserAsync();

            // Disconnect relays
            await _nostrService.DisconnectAsync();

            // Clear the last-user registry so next launch shows login
            ProfileConfiguration.ClearLastUser();

            // Reset profile to default
            ProfileConfiguration.SetProfile(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout cleanup");
        }

        // Dispose session-scoped services to unsubscribe from shared NostrService.Events
        (_messageService as IDisposable)?.Dispose();
        _storageService = null;
        _mlsService = null;
        _messageService = null;

        // Update UI on main thread
        RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, __) =>
        {
            MainViewModel = null;
            IsLoggedIn = false;
            LoginViewModel.Reset();
            return System.Reactive.Disposables.Disposable.Empty;
        });
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

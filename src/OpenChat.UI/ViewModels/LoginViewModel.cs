using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using QRCoder;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;

namespace OpenChat.UI.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly ILogger<LoginViewModel> _logger;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IExternalSigner? _externalSigner;

    [Reactive] public string PrivateKeyInput { get; set; } = string.Empty;
    [Reactive] public string BunkerUrl { get; set; } = string.Empty;
    [Reactive] public string? ErrorMessage { get; set; }
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public User? LoggedInUser { get; set; }

    // Generated key display
    [Reactive] public string? GeneratedNsec { get; set; }
    [Reactive] public string? GeneratedNpub { get; set; }
    [Reactive] public bool ShowGeneratedKeys { get; set; }

    // External signer
    [Reactive] public bool ShowExternalSigner { get; set; }
    [Reactive] public bool IsExternalSignerConnecting { get; set; }
    [Reactive] public string ExternalSignerStatus { get; set; } = string.Empty;
    [Reactive] public string? NostrConnectUri { get; set; }
    [Reactive] public Bitmap? NostrConnectQrBitmap { get; set; }

    // Login method selection
    [Reactive] public LoginMethod SelectedLoginMethod { get; set; } = LoginMethod.PrivateKey;

    public ReactiveCommand<Unit, Unit> GenerateNewKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> UseGeneratedKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectExternalSignerCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNsecCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<LoginMethod, Unit> SelectLoginMethodCommand { get; }

    public LoginViewModel(INostrService nostrService, IStorageService storageService, IExternalSigner? externalSigner = null)
    {
        _logger = LoggingConfiguration.CreateLogger<LoginViewModel>();
        _logger.LogDebug("LoginViewModel initializing");

        _nostrService = nostrService;
        _storageService = storageService;
        _externalSigner = externalSigner ?? new ExternalSignerService();

        GenerateNewKeyCommand = ReactiveCommand.Create(GenerateNewKey);

        var canImport = this.WhenAnyValue(
            x => x.PrivateKeyInput,
            x => x.IsLoading,
            (key, loading) => !string.IsNullOrWhiteSpace(key) && !loading);

        ImportKeyCommand = ReactiveCommand.CreateFromTask(ImportKeyAsync, canImport);

        var canUseGenerated = this.WhenAnyValue(
            x => x.GeneratedNsec,
            x => x.IsLoading,
            (nsec, loading) => !string.IsNullOrEmpty(nsec) && !loading);

        UseGeneratedKeyCommand = ReactiveCommand.CreateFromTask(UseGeneratedKeyAsync, canUseGenerated);

        var canConnectSigner = this.WhenAnyValue(
            x => x.BunkerUrl,
            x => x.IsLoading,
            x => x.IsExternalSignerConnecting,
            (url, loading, connecting) => !string.IsNullOrWhiteSpace(url) && !loading && !connecting);

        ConnectExternalSignerCommand = ReactiveCommand.CreateFromTask(ConnectExternalSignerAsync, canConnectSigner);

        CopyNsecCommand = ReactiveCommand.Create(() =>
        {
            // TODO: Copy to clipboard using platform-specific implementation
        });

        CopyNpubCommand = ReactiveCommand.Create(() =>
        {
            // TODO: Copy to clipboard using platform-specific implementation
        });

        SelectLoginMethodCommand = ReactiveCommand.CreateFromTask<LoginMethod>(SelectLoginMethodAsync);

        // Subscribe to external signer status and auto-login on connect
        if (_externalSigner != null)
        {
            _externalSigner.Status
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async status =>
                {
                    ExternalSignerStatus = status.State switch
                    {
                        ExternalSignerState.Connecting => "Connecting to relay...",
                        ExternalSignerState.WaitingForApproval => "Scan the QR code with your signer app...",
                        ExternalSignerState.Connected => "Connected!",
                        ExternalSignerState.Error => $"Error: {status.Error}",
                        _ => ""
                    };

                    IsExternalSignerConnecting = status.State == ExternalSignerState.Connecting ||
                                                 status.State == ExternalSignerState.WaitingForApproval;

                    // Auto-login when signer connects via nostrconnect
                    if (status.State == ExternalSignerState.Connected && status.PublicKeyHex != null)
                    {
                        await HandleSignerConnectedAsync(status.PublicKeyHex);
                    }
                });
        }
    }

    private async Task SelectLoginMethodAsync(LoginMethod method)
    {
        SelectedLoginMethod = method;
        ShowExternalSigner = method == LoginMethod.ExternalSigner;
        ShowGeneratedKeys = false;
        ErrorMessage = null;

        // Auto-generate nostrconnect URI when Extension tab is selected
        if (method == LoginMethod.ExternalSigner && NostrConnectUri == null && _externalSigner != null)
        {
            await GenerateNostrConnectAsync();
        }
    }

    private async Task GenerateNostrConnectAsync()
    {
        try
        {
            var uri = await _externalSigner!.GenerateAndListenForConnectionAsync("wss://relay.damus.io");
            NostrConnectUri = uri;
            NostrConnectQrBitmap?.Dispose();
            NostrConnectQrBitmap = GenerateQrBitmap(uri);
            _logger.LogInformation("Generated nostrconnect QR. URI: {Uri}", uri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate nostrconnect URI");
            ErrorMessage = $"Failed to connect to relay: {ex.Message}";
        }
    }

    private void GenerateNewKey()
    {
        _logger.LogInformation("User requested to generate new key");

        try
        {
            var (_, _, nsec, npub) = _nostrService.GenerateKeyPair();
            GeneratedNsec = nsec;
            GeneratedNpub = npub;
            ShowGeneratedKeys = true;
            ErrorMessage = null;
            _logger.LogInformation("Successfully generated new key. Npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate new key");
            ErrorMessage = $"Failed to generate key: {ex.Message}";
        }
    }

    private async Task ImportKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyInput))
        {
            _logger.LogWarning("ImportKeyAsync called with empty input");
            return;
        }

        _logger.LogInformation("User attempting to import private key");
        _logger.LogDebug("Key input length: {Length}, starts with nsec: {IsNsec}",
            PrivateKeyInput.Length,
            PrivateKeyInput.Trim().StartsWith("nsec"));

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _logger.LogDebug("Calling NostrService.ImportPrivateKey");
            var (privateKeyHex, publicKeyHex, nsec, npub) = _nostrService.ImportPrivateKey(PrivateKeyInput.Trim());
            _logger.LogInformation("Successfully parsed private key. Derived npub: {Npub}", npub);

            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                PrivateKeyHex = privateKeyHex,
                PublicKeyHex = publicKeyHex,
                Nsec = nsec,
                Npub = npub,
                DisplayName = $"User {npub[..12]}...",
                CreatedAt = DateTime.UtcNow,
                IsCurrentUser = true
            };

            _logger.LogDebug("Initializing storage service");
            await _storageService.InitializeAsync();

            _logger.LogDebug("Saving current user to database");
            await _storageService.SaveCurrentUserAsync(user);

            LoggedInUser = user;
            _logger.LogInformation("User successfully logged in with npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import private key");
            ErrorMessage = $"Invalid private key: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task UseGeneratedKeyAsync()
    {
        if (string.IsNullOrEmpty(GeneratedNsec))
        {
            _logger.LogWarning("UseGeneratedKeyAsync called but no generated nsec available");
            return;
        }

        _logger.LogInformation("User continuing with generated key");
        _logger.LogDebug("Setting PrivateKeyInput to generated nsec and calling ImportKeyAsync");
        PrivateKeyInput = GeneratedNsec;
        await ImportKeyAsync();
    }

    private async Task HandleSignerConnectedAsync(string publicKeyHex)
    {
        try
        {
            var npub = _externalSigner!.Npub;
            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                PublicKeyHex = publicKeyHex,
                Npub = npub,
                DisplayName = $"User {npub?[..12]}...",
                CreatedAt = DateTime.UtcNow,
                IsCurrentUser = true,
                PrivateKeyHex = null,
                Nsec = null
            };

            await _storageService.InitializeAsync();
            await _storageService.SaveCurrentUserAsync(user);
            LoggedInUser = user;
            _logger.LogInformation("Auto-logged in via external signer. Npub: {Npub}", npub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete login after signer connected");
            ErrorMessage = $"Failed to complete login: {ex.Message}";
        }
    }

    private static Bitmap GenerateQrBitmap(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var pngCode = new PngByteQRCode(data);
        var pngBytes = pngCode.GetGraphic(8);
        using var ms = new MemoryStream(pngBytes);
        return new Bitmap(ms);
    }

    private async Task ConnectExternalSignerAsync()
    {
        if (string.IsNullOrWhiteSpace(BunkerUrl) || _externalSigner == null) return;

        IsLoading = true;
        IsExternalSignerConnecting = true;
        ErrorMessage = null;

        try
        {
            var success = await _externalSigner.ConnectWithStringAsync(BunkerUrl.Trim());

            if (success && _externalSigner.PublicKeyHex != null)
            {
                await HandleSignerConnectedAsync(_externalSigner.PublicKeyHex);
            }
            else
            {
                ErrorMessage = "Failed to connect to external signer. Please check the URL and try again.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to connect: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsExternalSignerConnecting = false;
        }
    }
}

public enum LoginMethod
{
    PrivateKey,
    GenerateNew,
    ExternalSigner
}

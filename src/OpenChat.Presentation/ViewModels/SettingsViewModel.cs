using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using OpenChat.Core;
using OpenChat.Core.Logging;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.Services;

namespace OpenChat.Presentation.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly INostrService _nostrService;
    private readonly IStorageService _storageService;
    private readonly IMlsService _mlsService;
    private readonly IMessageService _messageService;
    private readonly IPlatformLauncher _launcher;

    [Reactive] public string? PublicKeyHex { get; set; }
    [Reactive] public string? PrivateKeyHex { get; set; }
    [Reactive] public string? Npub { get; set; }
    [Reactive] public string? DisplayName { get; set; }
    [Reactive] public string? Username { get; set; }
    [Reactive] public string? About { get; set; }
    [Reactive] public string? AvatarUrl { get; set; }

    [Reactive] public string NewRelayUrl { get; set; } = string.Empty;

    // Log Viewer
    [Reactive] public bool ShowLogViewer { get; set; }
    [Reactive] public string LogContent { get; set; } = string.Empty;

    // Key Package
    [Reactive] public bool IsPublishingKeyPackage { get; set; }
    [Reactive] public string? KeyPackageStatus { get; set; }
    [Reactive] public bool KeyPackageSuccess { get; set; }

    // Key Package Audit
    [Reactive] public bool IsAuditingKeyPackages { get; set; }
    [Reactive] public string? AuditStatus { get; set; }
    [Reactive] public KeyPackageAuditResult? LastAuditResult { get; set; }

    public ObservableCollection<RelayViewModel> Relays { get; } = new();

    public ReactiveCommand<Unit, Unit> SaveProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRelayCommand { get; }
    public ReactiveCommand<RelayViewModel, Unit> RemoveRelayCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPublicKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyNpubCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseLogViewerCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> PublishKeyPackageCommand { get; }
    public ReactiveCommand<Unit, Unit> AuditKeyPackagesCommand { get; }
    public ReactiveCommand<Unit, Unit> PublishRelayListCommand { get; }
    public ReactiveCommand<RelayViewModel, Unit> CycleRelayUsageCommand { get; }

    public SettingsViewModel(INostrService nostrService, IStorageService storageService, IMlsService mlsService, IMessageService messageService, IPlatformLauncher launcher)
    {
        _logger = LoggingConfiguration.CreateLogger<SettingsViewModel>();
        _nostrService = nostrService;
        _storageService = storageService;
        _mlsService = mlsService;
        _messageService = messageService;
        _launcher = launcher;

        SaveProfileCommand = ReactiveCommand.CreateFromTask(SaveProfileAsync);

        var canAddRelay = this.WhenAnyValue(x => x.NewRelayUrl,
            url => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _));

        AddRelayCommand = ReactiveCommand.CreateFromTask(AddRelayAsync, canAddRelay);
        RemoveRelayCommand = ReactiveCommand.Create<RelayViewModel>(RemoveRelay);

        CopyPublicKeyCommand = ReactiveCommand.Create(() =>
        {
            // TODO: Copy to clipboard
        });

        CopyNpubCommand = ReactiveCommand.Create(() =>
        {
            // TODO: Copy to clipboard
        });

        // Log viewer commands
        ViewLogsCommand = ReactiveCommand.Create(() =>
        {
            LogContent = LoggingConfiguration.GetRecentLogContents(1000);
            ShowLogViewer = true;
        });

        CloseLogViewerCommand = ReactiveCommand.Create(() =>
        {
            ShowLogViewer = false;
        });

        RefreshLogsCommand = ReactiveCommand.Create(() =>
        {
            LogContent = LoggingConfiguration.GetRecentLogContents(1000);
        });

        OpenLogFolderCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                var logDir = LoggingConfiguration.LogDirectory;
                if (Directory.Exists(logDir))
                {
                    _launcher.OpenFolder(logDir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open log folder");
            }
        });

        // Key package commands
        PublishKeyPackageCommand = ReactiveCommand.CreateFromTask(PublishKeyPackageAsync);
        AuditKeyPackagesCommand = ReactiveCommand.CreateFromTask(AuditKeyPackagesAsync);

        // Relay list commands
        PublishRelayListCommand = ReactiveCommand.CreateFromTask(PublishRelayListAsync);
        CycleRelayUsageCommand = ReactiveCommand.CreateFromTask<RelayViewModel>(CycleRelayUsageAsync);

        // Add default relays (will be replaced by saved relays in LoadSettingsAsync)
        foreach (var relay in NostrConstants.DefaultRelays)
            Relays.Add(new RelayViewModel { Url = relay, IsConnected = false });

        LoadSettingsAsync().ConfigureAwait(false);
    }

    private async Task LoadSettingsAsync()
    {
        var user = await _storageService.GetCurrentUserAsync();
        if (user != null)
        {
            PublicKeyHex = user.PublicKeyHex;
            PrivateKeyHex = user.PrivateKeyHex;
            Npub = user.Npub;
            DisplayName = user.DisplayName;
            Username = user.Username;
            About = user.About;
            AvatarUrl = user.AvatarUrl;

            // Load saved relay list
            var savedRelays = await _storageService.GetUserRelayListAsync(user.PublicKeyHex);
            if (savedRelays.Count > 0)
            {
                Relays.Clear();
                foreach (var relay in savedRelays)
                    Relays.Add(new RelayViewModel { Url = relay.Url, Usage = relay.Usage, IsConnected = false });
            }
        }
    }

    private async Task SaveProfileAsync()
    {
        var user = await _storageService.GetCurrentUserAsync();
        if (user != null)
        {
            user.DisplayName = DisplayName ?? "";
            user.Username = Username;
            user.About = About;
            user.AvatarUrl = AvatarUrl;
            user.LastUpdatedAt = DateTime.UtcNow;

            await _storageService.SaveCurrentUserAsync(user);
        }
    }

    private async Task AddRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRelayUrl)) return;

        var relay = new RelayViewModel { Url = NewRelayUrl, IsConnected = false };
        Relays.Add(relay);

        await _nostrService.ConnectAsync(NewRelayUrl);
        NewRelayUrl = string.Empty;
        await SaveRelayListAsync();
    }

    private async void RemoveRelay(RelayViewModel relay)
    {
        Relays.Remove(relay);
        try
        {
            await _nostrService.DisconnectRelayAsync(relay.Url);
            await SaveRelayListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to disconnect relay {Url}", relay.Url);
        }
    }

    private async Task CycleRelayUsageAsync(RelayViewModel relay)
    {
        relay.Usage = relay.Usage switch
        {
            RelayUsage.Both => RelayUsage.Read,
            RelayUsage.Read => RelayUsage.Write,
            RelayUsage.Write => RelayUsage.Both,
            _ => RelayUsage.Both
        };
        await SaveRelayListAsync();
    }

    private async Task SaveRelayListAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex)) return;

        var relayPrefs = Relays.Select(r => new RelayPreference { Url = r.Url, Usage = r.Usage }).ToList();
        await _storageService.SaveUserRelayListAsync(PublicKeyHex, relayPrefs);
        _logger.LogInformation("Saved relay list with {Count} relays", relayPrefs.Count);
    }

    private async Task PublishRelayListAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            _logger.LogWarning("Cannot publish relay list: no keys loaded");
            return;
        }

        try
        {
            var relayPrefs = Relays.Select(r => new RelayPreference { Url = r.Url, Usage = r.Usage }).ToList();
            await _nostrService.PublishRelayListAsync(relayPrefs, PrivateKeyHex);
            await _storageService.SaveUserRelayListAsync(PublicKeyHex, relayPrefs);
            _logger.LogInformation("Published and saved NIP-65 relay list");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish relay list");
        }
    }

    private async Task PublishKeyPackageAsync()
    {
        if (string.IsNullOrEmpty(PublicKeyHex))
        {
            KeyPackageStatus = "Error: No keys loaded. Please log in first.";
            KeyPackageSuccess = false;
            return;
        }

        IsPublishingKeyPackage = true;
        KeyPackageStatus = "Generating key package...";
        KeyPackageSuccess = false;

        try
        {
            _logger.LogInformation("Starting key package generation and publishing");

            // Initialize MLS service if needed
            await _mlsService.InitializeAsync(PrivateKeyHex ?? new string('0', 64), PublicKeyHex);

            // Generate key package
            KeyPackageStatus = "Generating MLS key package...";
            var keyPackage = await _mlsService.GenerateKeyPackageAsync();
            _logger.LogInformation("Generated key package with {DataLength} bytes", keyPackage.Data.Length);

            // Publish to Nostr relays with MDK-provided tags
            KeyPackageStatus = "Publishing to relays...";
            var eventId = await _nostrService.PublishKeyPackageAsync(keyPackage.Data, PrivateKeyHex, keyPackage.NostrTags);
            _logger.LogInformation("Published key package with event ID: {EventId} using {TagCount} MDK tags", eventId, keyPackage.NostrTags.Count);

            // Save key package locally
            await _storageService.SaveKeyPackageAsync(keyPackage);

            KeyPackageStatus = $"Key package published successfully!\nEvent ID: {eventId[..16]}...";
            KeyPackageSuccess = true;
            _logger.LogInformation("Key package published and saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish key package");
            KeyPackageStatus = $"Error: {ex.Message}";
            KeyPackageSuccess = false;
        }
        finally
        {
            IsPublishingKeyPackage = false;
        }
    }
    private async Task AuditKeyPackagesAsync()
    {
        IsAuditingKeyPackages = true;
        AuditStatus = "Fetching KeyPackages from relays...";

        try
        {
            var result = await _messageService.AuditKeyPackagesAsync();
            LastAuditResult = result;

            if (result.TotalOnRelays == 0)
            {
                AuditStatus = "No KeyPackages found on relays. Publish one first.";
            }
            else
            {
                AuditStatus = $"Audit complete: {result.TotalOnRelays} on relays, " +
                              $"{result.ActiveWithKeys} active, {result.Lost} lost, {result.Expired} expired";
            }

            if (result.Lost > 0)
            {
                AuditStatus += $"\n{result.Lost} KeyPackage(s) have lost keys — invites to these cannot be accepted.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to audit KeyPackages");
            AuditStatus = $"Audit failed: {ex.Message}";
        }
        finally
        {
            IsAuditingKeyPackages = false;
        }
    }
}

public class RelayViewModel : ViewModelBase
{
    [Reactive] public string Url { get; set; } = string.Empty;
    [Reactive] public bool IsConnected { get; set; }
    [Reactive] public string? Error { get; set; }
    [Reactive] public RelayUsage Usage { get; set; } = RelayUsage.Both;

    private readonly ObservableAsPropertyHelper<string> _usageLabel;
    public string UsageLabel => _usageLabel.Value;

    public RelayViewModel()
    {
        _usageLabel = this.WhenAnyValue(x => x.Usage)
            .Select(u => u switch
            {
                RelayUsage.Read => "Read",
                RelayUsage.Write => "Write",
                _ => "Read & Write"
            })
            .ToProperty(this, x => x.UsageLabel);
    }
}

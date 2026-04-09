using Moq;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.Services;
using OpenChat.Presentation.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

public class WatchPairingViewModelTests : IDisposable
{
    private readonly Mock<INostrService> _mockNostr;
    private readonly Mock<IStorageService> _mockStorage;
    private readonly Mock<IMlsService> _mockMls;
    private readonly Mock<IMessageService> _mockMessage;
    private readonly Mock<IPlatformLauncher> _mockLauncher;
    private readonly SettingsViewModel _viewModel;

    // Track delegate state
    private bool _generateCodeCalled;
    private bool _unpairCalled;
    private bool _isPaired;

    public WatchPairingViewModelTests()
    {
        _mockNostr = new Mock<INostrService>();
        _mockStorage = new Mock<IStorageService>();
        _mockMls = new Mock<IMlsService>();
        _mockMessage = new Mock<IMessageService>();
        _mockLauncher = new Mock<IPlatformLauncher>();

        // Set up storage to return a user
        _mockStorage.Setup(x => x.GetCurrentUserAsync())
            .ReturnsAsync(new User { PublicKeyHex = "abc123", Npub = "npub1test" });
        _mockStorage.Setup(x => x.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        _mockStorage.Setup(x => x.GetUserRelayListAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<RelayPreference>());
        _mockStorage.Setup(x => x.SaveSettingAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _mockNostr.Setup(x => x.ConnectedRelayUrls).Returns(new List<string>());

        // Set up platform delegates
        _generateCodeCalled = false;
        _unpairCalled = false;
        _isPaired = false;

        SettingsViewModel.OnGeneratePairingCode = () =>
        {
            _generateCodeCalled = true;
            return "123456";
        };

        SettingsViewModel.OnUnpairWatch = () =>
        {
            _unpairCalled = true;
            _isPaired = false;
            return Task.CompletedTask;
        };

        SettingsViewModel.OnCheckWatchPaired = () => _isPaired;

        _viewModel = new SettingsViewModel(
            _mockNostr.Object, _mockStorage.Object, _mockMls.Object,
            _mockMessage.Object, _mockLauncher.Object);
    }

    public void Dispose()
    {
        // Clean up static delegates
        SettingsViewModel.OnGeneratePairingCode = null;
        SettingsViewModel.OnUnpairWatch = null;
        SettingsViewModel.OnCheckWatchPaired = null;
    }

    [Fact]
    public void InitialState_IsNotPaired()
    {
        Assert.False(_viewModel.IsWatchPaired);
        Assert.Null(_viewModel.PairingCode);
        Assert.False(_viewModel.IsPairingCodeVisible);
    }

    [Fact]
    public async Task GeneratePairingCodeCommand_CallsPlatformDelegate()
    {
        // Use a short-circuit: set paired = true so countdown exits early
        SettingsViewModel.OnCheckWatchPaired = () => true;

        _viewModel.GeneratePairingCodeCommand.Execute().Subscribe();
        await Task.Delay(200); // Let the async command start

        Assert.True(_generateCodeCalled);
    }

    [Fact]
    public async Task GeneratePairingCodeCommand_SetsPairingCode()
    {
        SettingsViewModel.OnCheckWatchPaired = () => true;

        _viewModel.GeneratePairingCodeCommand.Execute().Subscribe();
        await Task.Delay(200);

        // The code should have been set before the "paired" check exits the loop
        Assert.Equal("123456", _viewModel.PairingCode);
    }

    [Fact]
    public async Task GeneratePairingCodeCommand_SetsIsPairingCodeVisible()
    {
        // Don't auto-pair so we can check visibility during countdown
        var visibleDuringCountdown = false;

        SettingsViewModel.OnCheckWatchPaired = () =>
        {
            visibleDuringCountdown = _viewModel.IsPairingCodeVisible;
            return true; // Exit countdown on first check
        };

        _viewModel.GeneratePairingCodeCommand.Execute().Subscribe();
        await Task.Delay(1500);

        Assert.True(visibleDuringCountdown, "PairingCode should be visible during countdown");
    }

    [Fact]
    public async Task GeneratePairingCodeCommand_WhenPairingSucceeds_SetsIsWatchPaired()
    {
        SettingsViewModel.OnCheckWatchPaired = () => true;

        _viewModel.GeneratePairingCodeCommand.Execute().Subscribe();
        await Task.Delay(1500);

        Assert.True(_viewModel.IsWatchPaired);
        Assert.False(_viewModel.IsPairingCodeVisible);
    }

    [Fact]
    public async Task GeneratePairingCodeCommand_WhenNoPlatformDelegate_ShowsError()
    {
        SettingsViewModel.OnGeneratePairingCode = null;

        _viewModel.GeneratePairingCodeCommand.Execute().Subscribe();
        await Task.Delay(200);

        Assert.Equal("Watch bridge not available", _viewModel.WatchPairingStatus);
    }

    [Fact]
    public async Task UnpairWatchCommand_CallsPlatformDelegate()
    {
        _viewModel.UnpairWatchCommand.Execute().Subscribe();
        await Task.Delay(200);

        Assert.True(_unpairCalled);
    }

    [Fact]
    public async Task UnpairWatchCommand_SetsIsWatchPairedToFalse()
    {
        _viewModel.IsWatchPaired = true;

        _viewModel.UnpairWatchCommand.Execute().Subscribe();
        await Task.Delay(200);

        Assert.False(_viewModel.IsWatchPaired);
    }

    [Fact]
    public async Task UnpairWatchCommand_SetsStatusMessage()
    {
        _viewModel.UnpairWatchCommand.Execute().Subscribe();
        await Task.Delay(200);

        Assert.Equal("Watch unpaired", _viewModel.WatchPairingStatus);
    }

    [Fact]
    public void LoadSettings_ChecksWatchPairedState()
    {
        _isPaired = true;
        SettingsViewModel.OnCheckWatchPaired = () => _isPaired;

        // Create a new ViewModel to trigger LoadSettingsAsync
        var vm = new SettingsViewModel(
            _mockNostr.Object, _mockStorage.Object, _mockMls.Object,
            _mockMessage.Object, _mockLauncher.Object);

        // Give LoadSettingsAsync time to run
        Thread.Sleep(200);

        Assert.True(vm.IsWatchPaired);
    }
}

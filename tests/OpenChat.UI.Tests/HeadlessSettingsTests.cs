using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
using OpenChat.Core;
using OpenChat.Core.Models;
using OpenChat.Core.Services;
using OpenChat.Presentation.ViewModels;
using Xunit;

namespace OpenChat.UI.Tests;

/// <summary>
/// Headless tests for settings: KeyPackage publishing, audit, and profile save/reload.
/// </summary>
public class HeadlessSettingsTests : HeadlessTestBase
{
    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task PublishKeyPackage_UpdatesStatusInSettings(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var settingsVm = mainVm.SettingsViewModel;

        Assert.NotNull(settingsVm.PublicKeyHex);

        // Publish a KeyPackage
        settingsVm.PublishKeyPackageCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.True(settingsVm.KeyPackageSuccess);
        Assert.NotNull(settingsVm.KeyPackageStatus);
        Assert.Contains("fakekp_", settingsVm.KeyPackageStatus);

        // Verify the KeyPackage was published via NostrService
        ctx.MockNostr.Verify(n => n.PublishKeyPackageAsync(
            It.Is<byte[]>(data => data.Length > 0),
            It.IsAny<string>(),
            It.IsAny<List<List<string>>?>()), Times.AtLeastOnce);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task AuditKeyPackages_ShowsResults(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var settingsVm = mainVm.SettingsViewModel;

        // Run audit (mock returns empty KeyPackages from relay)
        settingsVm.AuditKeyPackagesCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(settingsVm.AuditStatus);
        Assert.NotNull(settingsVm.LastAuditResult);
        Assert.Equal(0, settingsVm.LastAuditResult.TotalOnRelays);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task SaveProfile_PersistsAndReloads(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var settingsVm = mainVm.SettingsViewModel;

        settingsVm.DisplayName = "New Display Name";
        settingsVm.Username = "newuser";
        settingsVm.About = "Test bio";
        Dispatcher.UIThread.RunJobs();

        settingsVm.SaveProfileCommand.Execute().Subscribe();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();

        // Verify persisted by reading from storage directly
        var savedUser = await ctx.Storage.GetCurrentUserAsync();
        Assert.NotNull(savedUser);
        Assert.Equal("New Display Name", savedUser!.DisplayName);
        Assert.Equal("newuser", savedUser.Username);
        Assert.Equal("Test bio", savedUser.About);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task RelayList_AddRemoveCycleUsage_PersistsToStorage(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var settingsVm = mainVm.SettingsViewModel;

        // Default relays should be loaded
        Assert.Equal(NostrConstants.DefaultRelays.Length, settingsVm.Relays.Count);
        Assert.All(settingsVm.Relays, r => Assert.Equal(RelayUsage.Both, r.Usage));

        // Add a new relay
        settingsVm.NewRelayUrl = "wss://custom.relay";
        settingsVm.AddRelayCommand.Execute().Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(NostrConstants.DefaultRelays.Length + 1, settingsVm.Relays.Count);
        Assert.Contains(settingsVm.Relays, r => r.Url == "wss://custom.relay");

        // Cycle usage on the custom relay
        var customRelay = settingsVm.Relays.First(r => r.Url == "wss://custom.relay");
        Assert.Equal(RelayUsage.Both, customRelay.Usage);

        settingsVm.CycleRelayUsageCommand.Execute(customRelay).Subscribe();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RelayUsage.Read, customRelay.Usage);

        settingsVm.CycleRelayUsageCommand.Execute(customRelay).Subscribe();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RelayUsage.Write, customRelay.Usage);

        // Verify persisted to storage
        var savedRelays = await ctx.Storage.GetUserRelayListAsync(ctx.User.PublicKeyHex);
        Assert.Equal(NostrConstants.DefaultRelays.Length + 1, savedRelays.Count);
        var savedCustom = savedRelays.First(r => r.Url == "wss://custom.relay");
        Assert.Equal(RelayUsage.Write, savedCustom.Usage);

        // Remove the custom relay
        settingsVm.RemoveRelayCommand.Execute(customRelay).Subscribe();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(NostrConstants.DefaultRelays.Length, settingsVm.Relays.Count);
        Assert.DoesNotContain(settingsVm.Relays, r => r.Url == "wss://custom.relay");
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task PublishRelayList_SendsNip65Event(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        var settingsVm = mainVm.SettingsViewModel;

        // Publish relay list
        settingsVm.PublishRelayListCommand.Execute().Subscribe();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Verify NIP-65 publish was called
        ctx.MockNostr.Verify(n => n.PublishRelayListAsync(
            It.Is<List<RelayPreference>>(list => list.Count == NostrConstants.DefaultRelays.Length),
            It.IsAny<string?>()), Times.Once);

        // Verify also saved to storage
        var savedRelays = await ctx.Storage.GetUserRelayListAsync(ctx.User.PublicKeyHex);
        Assert.Equal(NostrConstants.DefaultRelays.Length, savedRelays.Count);
    }

    [AvaloniaTheory]
    [InlineData("rust")]
    [InlineData("managed")]
    public async Task Login_WithSavedRelays_UsesThemInsteadOfDefaults(string backend)
    {
        if (ShouldSkip(backend)) return;
        var ctx = await CreateRealContext(backend);
        await ctx.MessageService.InitializeAsync();

        // Pre-save a custom relay list
        var customRelays = new List<RelayPreference>
        {
            new() { Url = "wss://custom1.relay", Usage = RelayUsage.Both },
            new() { Url = "wss://custom2.relay", Usage = RelayUsage.Read }
        };
        await ctx.Storage.SaveUserRelayListAsync(ctx.User.PublicKeyHex, customRelays);

        // Create MainViewModel and log in — this triggers InitializeAfterLoginAsync
        var mainVm = CreateMainViewModel(ctx);
        Dispatcher.UIThread.RunJobs();
        mainVm.LoginViewModel.LoggedInUser = ctx.User;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(500);
        Dispatcher.UIThread.RunJobs();

        // Verify ConnectAsync was called with the custom relays (not defaults)
        ctx.MockNostr.Verify(n => n.ConnectAsync(
            It.Is<IEnumerable<string>>(urls =>
                urls.Contains("wss://custom1.relay") &&
                urls.Contains("wss://custom2.relay"))),
            Times.AtLeastOnce);
    }
}

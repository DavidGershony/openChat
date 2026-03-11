using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Moq;
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
}

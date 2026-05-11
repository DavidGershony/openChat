using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Builder;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Scramble.UI.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Scramble.UI.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseReactiveUI(_ => { })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());

    /// <summary>
    /// ReactiveUI 23 no longer auto-initializes on first use. Plain xunit Facts that
    /// construct ViewModels directly (without going through the Avalonia AppBuilder)
    /// would throw <see cref="InvalidOperationException"/> from
    /// <c>ReactiveNotifyPropertyChangedMixin</c> static ctor. A module initializer
    /// guarantees RxUI is initialized before any test code runs.
    ///
    /// We also pin both schedulers to <see cref="ImmediateScheduler.Instance"/> so
    /// <c>ObserveOn(MainThreadScheduler)</c> runs synchronously in tests. Without
    /// this, RxUI 23 defaults <c>MainThreadScheduler</c> to
    /// <c>DefaultScheduler.Instance</c> (which dispatches via the thread pool),
    /// making subscription handlers race with test assertions.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureReactiveUIInitialized()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
        RxSchedulers.MainThreadScheduler = ImmediateScheduler.Instance;
        RxSchedulers.TaskpoolScheduler = ImmediateScheduler.Instance;
    }
}

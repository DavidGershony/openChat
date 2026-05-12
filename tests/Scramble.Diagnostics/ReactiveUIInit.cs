using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using ReactiveUI;
using ReactiveUI.Builder;

namespace Scramble.Diagnostics;

/// <summary>
/// ReactiveUI 23 no longer auto-initializes on first use. Diagnostic tests
/// construct ViewModels (e.g. MainViewModel, ChatListViewModel) directly to
/// drive end-to-end scenarios against a real relay; without explicit init,
/// <c>ReactiveNotifyPropertyChangedMixin</c>'s static ctor throws
/// <see cref="System.InvalidOperationException"/>.
///
/// Both schedulers are pinned to <see cref="ImmediateScheduler.Instance"/> so
/// <c>ObserveOn(MainThreadScheduler)</c> runs synchronously — the diagnostic
/// host has no Avalonia dispatcher, and DefaultScheduler (background pool)
/// makes test timing non-deterministic.
/// </summary>
internal static class ReactiveUIInit
{
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

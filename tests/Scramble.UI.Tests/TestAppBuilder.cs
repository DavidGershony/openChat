using Avalonia;
using Avalonia.Headless;
using ReactiveUI.Avalonia;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Scramble.UI.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Scramble.UI.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

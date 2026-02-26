using Avalonia;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(OpenChat.UI.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpenChat.UI.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseReactiveUI()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

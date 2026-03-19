using Avalonia;
using Avalonia.ReactiveUI;
using OpenChat.Core.Configuration;
using System;

namespace OpenChat.Desktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Parse CLI arguments before anything else
        string? profileName = null;
        string? mdkBackendArg = null;
        bool allowLocalRelays = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                profileName = args[++i];
            else if (args[i].Equals("--mdk", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                mdkBackendArg = args[++i];
            else if (args[i].Equals("--allow-local-relays", StringComparison.OrdinalIgnoreCase))
                allowLocalRelays = true;
        }

        ProfileConfiguration.SetProfile(profileName);

        if (mdkBackendArg != null)
        {
            var backend = mdkBackendArg.ToLowerInvariant() switch
            {
                "managed" => MdkBackend.Managed,
                "rust" => MdkBackend.Rust,
                _ => throw new ArgumentException($"Unknown --mdk value '{mdkBackendArg}'. Use 'rust' or 'managed'.")
            };
            ProfileConfiguration.SetMdkBackend(backend);
        }

        if (allowLocalRelays)
            ProfileConfiguration.SetAllowLocalRelays(true);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();
}

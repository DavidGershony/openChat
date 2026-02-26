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
        // Parse --profile and --mdk arguments before anything else
        string? profileName = null;
        string? mdkBackendArg = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase))
                profileName = args[i + 1];
            else if (args[i].Equals("--mdk", StringComparison.OrdinalIgnoreCase))
                mdkBackendArg = args[i + 1];
        }

        ProfileConfiguration.SetProfile(profileName);

        // --mdk backend selection disabled until marmut-mdk NuGet packages are published
        // if (mdkBackendArg != null)
        // {
        //     var backend = mdkBackendArg.ToLowerInvariant() switch
        //     {
        //         "managed" => MdkBackend.Managed,
        //         "rust" => MdkBackend.Rust,
        //         _ => throw new ArgumentException($"Unknown --mdk value '{mdkBackendArg}'. Use 'rust' or 'managed'.")
        //     };
        //     ProfileConfiguration.SetMdkBackend(backend);
        // }
        if (mdkBackendArg != null)
        {
            Console.WriteLine("Warning: --mdk argument ignored, managed backend temporarily disabled");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();
}

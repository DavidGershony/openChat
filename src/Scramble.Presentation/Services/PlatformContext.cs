namespace Scramble.Presentation.Services;

/// <summary>
/// Describes the platform capabilities and layout mode of the current app host.
/// Constructed by each platform head (Desktop, Mobile.Android, Apple) and threaded
/// through ShellViewModel → MainViewModel so shared UI logic can adapt without
/// referencing platform-specific types.
/// </summary>
public class PlatformContext
{
    /// <summary>
    /// Whether the app is running in a single-view mobile shell.
    /// Controls navigation affordances such as the chat back button.
    /// </summary>
    public bool IsMobile { get; init; }
}

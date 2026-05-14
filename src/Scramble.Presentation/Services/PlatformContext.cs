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

    /// <summary>
    /// Whether a file/image picker is available on this platform.
    /// Controls attach-file button visibility in the chat input area.
    /// </summary>
    public bool HasFilePicker { get; init; }

    /// <summary>
    /// Whether audio recording (voice messages) is supported on this platform.
    /// Controls the microphone/voice-record button visibility.
    /// </summary>
    public bool HasAudioRecording { get; init; }

    /// <summary>
    /// Whether audio playback (voice messages) is supported on this platform.
    /// Controls the play button on received voice messages.
    /// </summary>
    public bool HasAudioPlayback { get; init; }

    /// <summary>
    /// Whether media upload (Blossom) is supported on this platform.
    /// Required for sending images and voice messages; guards the send path.
    /// </summary>
    public bool HasMediaUpload { get; init; }
}

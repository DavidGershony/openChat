namespace OpenChat.Core.Services;

/// <summary>
/// Cross-platform audio recording interface.
/// Platform implementations: DesktopAudioRecordingService, AndroidAudioRecordingService.
/// </summary>
public interface IAudioRecordingService
{
    bool IsRecording { get; }
    TimeSpan RecordingDuration { get; }

    /// <summary>
    /// Checks if the recording backend and its dependencies are available.
    /// Returns null if ready, or a user-facing message describing what's missing.
    /// </summary>
    string? CheckDependencies();

    /// <summary>
    /// Starts recording audio from the default input device.
    /// </summary>
    Task StartRecordingAsync();

    /// <summary>
    /// Stops recording and returns the raw PCM samples (16-bit, mono, 48kHz).
    /// </summary>
    Task<AudioRecordingResult> StopRecordingAsync();

    /// <summary>
    /// Cancels the current recording without returning data.
    /// </summary>
    Task CancelRecordingAsync();
}

public class AudioRecordingResult
{
    public byte[] PcmData { get; set; } = Array.Empty<byte>();
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 1;
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Cross-platform audio playback interface.
/// </summary>
public interface IAudioPlaybackService
{
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    /// <summary>
    /// Plays decoded PCM audio (16-bit, mono, 48kHz).
    /// </summary>
    Task PlayAsync(byte[] pcmData, int sampleRate = 48000, int channels = 1);

    /// <summary>
    /// Stops playback.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Pauses playback.
    /// </summary>
    Task PauseAsync();

    event EventHandler? PlaybackCompleted;
}

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;
using OpenChat.Core.Services;

namespace OpenChat.UI.Services;

/// <summary>
/// Cross-platform desktop audio recording using system tools.
/// Windows: PowerShell + .NET audio APIs via script
/// Linux: arecord (ALSA) or parecord (PulseAudio)
/// macOS: sox (via Homebrew) or ffmpeg
/// All output 16-bit PCM mono WAV at 48kHz for Opus encoding.
/// </summary>
public class DesktopAudioRecordingService : IAudioRecordingService
{
    private readonly ILogger<DesktopAudioRecordingService> _logger;
    private Process? _recordProcess;
    private string? _tempFilePath;
    private DateTime _recordStartTime;

    public bool IsRecording => _recordProcess != null && !_recordProcess.HasExited;
    public TimeSpan RecordingDuration => IsRecording ? DateTime.UtcNow - _recordStartTime : TimeSpan.Zero;

    public DesktopAudioRecordingService()
    {
        _logger = LoggingConfiguration.CreateLogger<DesktopAudioRecordingService>();
        _logger.LogInformation("DesktopAudioRecordingService initialized (OS: {OS})",
            RuntimeInformation.OSDescription);
    }

    public Task StartRecordingAsync()
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording");

        _tempFilePath = Path.Combine(Path.GetTempPath(), $"openchat_voice_{Guid.NewGuid()}.wav");
        _recordStartTime = DateTime.UtcNow;

        ProcessStartInfo psi;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use ffmpeg on Windows (commonly available, handles WASAPI)
            psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f dshow -i audio=\"Microphone\" -ar 48000 -ac 1 -sample_fmt s16 -t 300 \"{_tempFilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            // Fallback: try Windows' built-in SoundRecorder via PowerShell
            if (!IsCommandAvailable("ffmpeg"))
            {
                _logger.LogInformation("ffmpeg not found, using PowerShell AudioRecord fallback");
                psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"" +
                        $"Add-Type -AssemblyName System.Speech; " +
                        $"$r = New-Object System.Speech.Recognition.SpeechRecognitionEngine; " +
                        $"$r.SetInputToDefaultAudioDevice(); " +
                        $"$r.RecognizeAsync([System.Speech.Recognition.RecognizeMode]::Multiple); " +
                        $"Start-Sleep -Seconds 300\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // PowerShell fallback is limited — ffmpeg is strongly recommended
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Try parecord (PulseAudio) first, fall back to arecord (ALSA)
            var recorder = IsCommandAvailable("parecord") ? "parecord" : "arecord";
            if (recorder == "parecord")
            {
                psi = new ProcessStartInfo
                {
                    FileName = "parecord",
                    Arguments = $"--rate=48000 --channels=1 --format=s16le --file-format=wav \"{_tempFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "arecord",
                    Arguments = $"-f S16_LE -r 48000 -c 1 -t wav \"{_tempFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
            }
            _logger.LogInformation("Using {Recorder} for audio capture on Linux", recorder);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Use sox (rec command) or ffmpeg on macOS
            var recorder = IsCommandAvailable("rec") ? "rec" : "ffmpeg";
            if (recorder == "rec")
            {
                psi = new ProcessStartInfo
                {
                    FileName = "rec",
                    Arguments = $"-r 48000 -c 1 -b 16 \"{_tempFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f avfoundation -i \":0\" -ar 48000 -ac 1 -sample_fmt s16 -t 300 \"{_tempFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
            }
            _logger.LogInformation("Using {Recorder} for audio capture on macOS", recorder);
        }
        else
        {
            throw new PlatformNotSupportedException("Audio recording not supported on this platform");
        }

        try
        {
            _recordProcess = Process.Start(psi);
            _logger.LogInformation("Recording started: {File}", _tempFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio recording process");
            throw new InvalidOperationException(
                "Failed to start audio recording. Ensure ffmpeg, parecord, or arecord is installed.", ex);
        }

        return Task.CompletedTask;
    }

    public async Task<AudioRecordingResult> StopRecordingAsync()
    {
        if (_recordProcess == null || _tempFilePath == null)
            throw new InvalidOperationException("Not recording");

        var duration = DateTime.UtcNow - _recordStartTime;

        // Send interrupt signal to stop recording gracefully
        try
        {
            if (!_recordProcess.HasExited)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows, send 'q' to ffmpeg's stdin or just kill
                    _recordProcess.Kill();
                }
                else
                {
                    // On Unix, send SIGINT for graceful stop
                    _recordProcess.Kill(false); // SIGTERM
                }

                await _recordProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping recording process");
            try { _recordProcess.Kill(); } catch { }
        }
        finally
        {
            _recordProcess.Dispose();
            _recordProcess = null;
        }

        // Read the WAV file
        if (!File.Exists(_tempFilePath) || new FileInfo(_tempFilePath).Length < 44)
        {
            _logger.LogWarning("Recording file missing or too small: {File}", _tempFilePath);
            throw new InvalidOperationException("Recording failed — no audio captured. Check microphone permissions.");
        }

        var wavBytes = await File.ReadAllBytesAsync(_tempFilePath);

        // Clean up temp file
        try { File.Delete(_tempFilePath); } catch { }
        _tempFilePath = null;

        // Strip WAV header (44 bytes) to get raw PCM
        var pcmData = new byte[wavBytes.Length - 44];
        Buffer.BlockCopy(wavBytes, 44, pcmData, 0, pcmData.Length);

        _logger.LogInformation("Recording stopped: {Duration:F1}s, {Size} bytes PCM",
            duration.TotalSeconds, pcmData.Length);

        return new AudioRecordingResult
        {
            PcmData = pcmData,
            SampleRate = 48000,
            Channels = 1,
            Duration = duration
        };
    }

    public Task CancelRecordingAsync()
    {
        if (_recordProcess != null)
        {
            try { _recordProcess.Kill(); } catch { }
            _recordProcess.Dispose();
            _recordProcess = null;
        }

        if (_tempFilePath != null)
        {
            try { File.Delete(_tempFilePath); } catch { }
            _tempFilePath = null;
        }

        _logger.LogInformation("Recording cancelled");
        return Task.CompletedTask;
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo(which, command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(2000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Desktop audio playback using system tools (ffplay/aplay/afplay).
/// Plays raw PCM data by writing a temporary WAV file and invoking the system player.
/// </summary>
public class DesktopAudioPlaybackService : IAudioPlaybackService
{
    private readonly ILogger<DesktopAudioPlaybackService> _logger;
    private Process? _playProcess;
    private TimeSpan _duration;

    public bool IsPlaying => _playProcess != null && !_playProcess.HasExited;
    public TimeSpan Position => TimeSpan.Zero; // Approximate — subprocess doesn't report position
    public TimeSpan Duration => _duration;

    public event EventHandler? PlaybackCompleted;

    public DesktopAudioPlaybackService()
    {
        _logger = LoggingConfiguration.CreateLogger<DesktopAudioPlaybackService>();
    }

    public async Task PlayAsync(byte[] pcmData, int sampleRate = 48000, int channels = 1)
    {
        await StopAsync();

        _duration = TimeSpan.FromSeconds((double)pcmData.Length / (sampleRate * channels * 2));

        // Write PCM as WAV to temp file
        var tempPath = Path.Combine(Path.GetTempPath(), $"openchat_play_{Guid.NewGuid()}.wav");
        await using (var fs = File.Create(tempPath))
        {
            WriteWavHeader(fs, pcmData.Length, sampleRate, channels);
            await fs.WriteAsync(pcmData);
        }

        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use PowerShell SoundPlayer (built-in, no deps)
            psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"(New-Object Media.SoundPlayer '{tempPath}').PlaySync()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            psi = new ProcessStartInfo("afplay", $"\"{tempPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            // Linux: aplay or paplay
            var player = IsCommandAvailablePublic("paplay") ? "paplay" : "aplay";
            psi = new ProcessStartInfo(player, $"\"{tempPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        _playProcess = Process.Start(psi);
        _logger.LogInformation("Playing audio: {Duration:F1}s", _duration.TotalSeconds);

        // Wait for completion in background, clean up
        _ = Task.Run(async () =>
        {
            if (_playProcess != null)
            {
                await _playProcess.WaitForExitAsync();
                _playProcess.Dispose();
                _playProcess = null;
            }
            try { File.Delete(tempPath); } catch { }
            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    public Task StopAsync()
    {
        if (_playProcess != null && !_playProcess.HasExited)
        {
            try { _playProcess.Kill(); } catch { }
            _playProcess.Dispose();
            _playProcess = null;
        }
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        // Subprocess playback doesn't support pause — stop instead
        return StopAsync();
    }

    private static void WriteWavHeader(Stream stream, int dataLength, int sampleRate, int channels)
    {
        var bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
    }

    internal static bool IsCommandAvailablePublic(string command)
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo(which, command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(2000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

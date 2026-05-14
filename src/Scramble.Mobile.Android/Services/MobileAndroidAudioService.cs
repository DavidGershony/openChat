using Android.Content;
using Android.Media;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;
using Scramble.Core.Services;

namespace Scramble.MobileAndroid.Services;

/// <summary>
/// Android audio recording using MediaRecorder.
/// Records to M4A (MPEG4/AAC) at 48kHz mono, then decodes to raw PCM
/// using MediaExtractor + MediaCodec for Opus re-encoding via Concentus.
/// Ported from Scramble.Android.Services.AndroidAudioRecordingService.
/// </summary>
public class MobileAndroidAudioRecordingService : IAudioRecordingService
{
    private readonly ILogger<MobileAndroidAudioRecordingService> _logger;
    private readonly Context _context;
    private MediaRecorder? _recorder;
    private string? _tempFilePath;
    private DateTime _recordStartTime;

    public bool IsRecording => _recorder != null;
    public TimeSpan RecordingDuration => IsRecording ? DateTime.UtcNow - _recordStartTime : TimeSpan.Zero;

    public MobileAndroidAudioRecordingService(Context context)
    {
        _context = context;
        _logger = LoggingConfiguration.CreateLogger<MobileAndroidAudioRecordingService>();
    }

    public string? CheckDependencies() => null; // Android has built-in MediaRecorder

    public Task StartRecordingAsync()
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording");

        _tempFilePath = Path.Combine(_context.CacheDir!.AbsolutePath, $"voice_{Guid.NewGuid()}.m4a");
        _recordStartTime = DateTime.UtcNow;

        _recorder = new MediaRecorder(_context);
        _recorder.SetAudioSource(AudioSource.Mic);
        _recorder.SetOutputFormat(OutputFormat.Mpeg4);
        _recorder.SetAudioEncoder(AudioEncoder.Aac);
        _recorder.SetAudioSamplingRate(48000);
        _recorder.SetAudioChannels(1);
        _recorder.SetAudioEncodingBitRate(64000);
        _recorder.SetOutputFile(_tempFilePath);

        _recorder.Prepare();
        _recorder.Start();

        _logger.LogInformation("Recording started: {File}", _tempFilePath);
        return Task.CompletedTask;
    }

    public async Task<AudioRecordingResult> StopRecordingAsync()
    {
        if (_recorder == null || _tempFilePath == null)
            throw new InvalidOperationException("Not recording");

        var duration = DateTime.UtcNow - _recordStartTime;

        try
        {
            _recorder.Stop();
            _recorder.Release();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping MediaRecorder");
        }
        finally
        {
            _recorder = null;
        }

        var pcmData = await DecodeToPcmAsync(_tempFilePath);

        try { File.Delete(_tempFilePath); } catch { }
        _tempFilePath = null;

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
        if (_recorder != null)
        {
            try { _recorder.Stop(); } catch { }
            try { _recorder.Release(); } catch { }
            _recorder = null;
        }

        if (_tempFilePath != null)
        {
            try { File.Delete(_tempFilePath); } catch { }
            _tempFilePath = null;
        }

        _logger.LogInformation("Recording cancelled");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Decodes an M4A/AAC file to raw PCM using Android's MediaExtractor + MediaCodec.
    /// </summary>
    private async Task<byte[]> DecodeToPcmAsync(string filePath)
    {
        var extractor = new MediaExtractor();
        extractor.SetDataSource(filePath);

        int audioTrack = -1;
        for (int i = 0; i < extractor.TrackCount; i++)
        {
            var format = extractor.GetTrackFormat(i);
            var mime = format.GetString(MediaFormat.KeyMime);
            if (mime?.StartsWith("audio/") == true)
            {
                audioTrack = i;
                break;
            }
        }

        if (audioTrack < 0)
            throw new InvalidOperationException("No audio track found in recorded file");

        extractor.SelectTrack(audioTrack);
        var trackFormat = extractor.GetTrackFormat(audioTrack);
        var trackMime = trackFormat.GetString(MediaFormat.KeyMime)!;

        var codec = MediaCodec.CreateDecoderByType(trackMime);
        codec.Configure(trackFormat, null, null, MediaCodecConfigFlags.None);
        codec.Start();

        using var pcmStream = new MemoryStream();
        var info = new MediaCodec.BufferInfo();
        var eos = false;

        while (!eos)
        {
            var inputIndex = codec.DequeueInputBuffer(10000);
            if (inputIndex >= 0)
            {
                var inputBuffer = codec.GetInputBuffer(inputIndex)!;
                var sampleSize = extractor.ReadSampleData(inputBuffer, 0);
                if (sampleSize < 0)
                {
                    codec.QueueInputBuffer(inputIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                }
                else
                {
                    codec.QueueInputBuffer(inputIndex, 0, sampleSize, extractor.SampleTime, 0);
                    extractor.Advance();
                }
            }

            var outputIndex = codec.DequeueOutputBuffer(info, 10000);
            if (outputIndex >= 0)
            {
                var outputBuffer = codec.GetOutputBuffer(outputIndex)!;
                var pcmBytes = new byte[info.Size];
                outputBuffer.Get(pcmBytes, 0, info.Size);
                pcmStream.Write(pcmBytes, 0, info.Size);
                codec.ReleaseOutputBuffer(outputIndex, false);

                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                    eos = true;
            }
        }

        codec.Stop();
        codec.Release();
        extractor.Release();

        return pcmStream.ToArray();
    }
}

/// <summary>
/// Android audio playback using MediaPlayer.
/// Plays raw PCM by writing a temporary WAV file.
/// Ported from Scramble.Android.Services.AndroidAudioPlaybackService.
/// </summary>
public class MobileAndroidAudioPlaybackService : IAudioPlaybackService
{
    private readonly ILogger<MobileAndroidAudioPlaybackService> _logger;
    private readonly Context _context;
    private MediaPlayer? _player;
    private string? _currentTempPath;

    public bool IsPlaying => _player?.IsPlaying == true;
    public TimeSpan Position => _player != null ? TimeSpan.FromMilliseconds(_player.CurrentPosition) : TimeSpan.Zero;
    public TimeSpan Duration => _player != null ? TimeSpan.FromMilliseconds(_player.Duration) : TimeSpan.Zero;

    public event EventHandler? PlaybackCompleted;

    public MobileAndroidAudioPlaybackService(Context context)
    {
        _context = context;
        _logger = LoggingConfiguration.CreateLogger<MobileAndroidAudioPlaybackService>();
    }

    public async Task PlayAsync(byte[] pcmData, int sampleRate = 48000, int channels = 1)
    {
        await StopAsync();

        var tempPath = Path.Combine(_context.CacheDir!.AbsolutePath, $"play_{Guid.NewGuid()}.wav");
        await using (var fs = File.Create(tempPath))
        {
            WriteWavHeader(fs, pcmData.Length, sampleRate, channels);
            await fs.WriteAsync(pcmData);
        }

        _currentTempPath = tempPath;
        _player = new MediaPlayer();
        _player.SetDataSource(tempPath);
        _player.Completion += (s, e) =>
        {
            CleanupTempFile();
            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        };
        _player.Prepare();
        _player.Start();

        _logger.LogInformation("Playback started");
    }

    public Task StopAsync()
    {
        if (_player != null)
        {
            if (_player.IsPlaying) _player.Stop();
            _player.Release();
            _player = null;
        }
        CleanupTempFile();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        if (_player?.IsPlaying == true)
            _player.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (_player != null && !_player.IsPlaying)
            _player.Start();
        return Task.CompletedTask;
    }

    public Task SeekTo(TimeSpan position)
    {
        _player?.SeekTo((int)position.TotalMilliseconds);
        return Task.CompletedTask;
    }

    private void CleanupTempFile()
    {
        if (_currentTempPath != null)
        {
            try { File.Delete(_currentTempPath); } catch { }
            _currentTempPath = null;
        }
    }

    private static void WriteWavHeader(System.IO.Stream stream, int dataLength, int sampleRate, int channels)
    {
        var bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
    }
}

using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenChat.Core.Logging;

namespace OpenChat.Core.Audio;

/// <summary>
/// Pure .NET Opus encoder/decoder using Concentus.
/// Encodes PCM to Opus in OGG container, decodes back.
/// </summary>
public static class OpusCodec
{
    private static readonly ILogger _logger = LoggingConfiguration.CreateLogger<object>();

    private const int DefaultSampleRate = 48000;
    private const int DefaultChannels = 1;
    private const int DefaultBitrate = 24000; // 24 kbps — good for voice, ~180KB/min

    /// <summary>
    /// Encodes raw PCM (16-bit, mono, 48kHz) to Opus in OGG container.
    /// Returns .opus file bytes.
    /// </summary>
    public static byte[] Encode(byte[] pcmData, int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels, int bitrate = DefaultBitrate)
    {
        _logger.LogDebug("Opus encoding: {Len} bytes PCM, {Rate}Hz, {Ch}ch, {Bitrate}bps",
            pcmData.Length, sampleRate, channels, bitrate);

        var encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrate;

        using var outputStream = new MemoryStream();
        var oggOut = new OpusOggWriteStream(encoder, outputStream, new OpusTags());

        // Convert bytes to short samples
        var sampleCount = pcmData.Length / 2; // 16-bit = 2 bytes per sample
        var samples = new short[sampleCount];
        Buffer.BlockCopy(pcmData, 0, samples, 0, pcmData.Length);

        // Write all samples — OpusOggWriteStream handles framing
        oggOut.WriteSamples(samples, 0, sampleCount);
        oggOut.Finish();

        var result = outputStream.ToArray();
        _logger.LogInformation("Opus encoded: {InLen} bytes PCM → {OutLen} bytes Opus",
            pcmData.Length, result.Length);

        return result;
    }

    /// <summary>
    /// Decodes Opus in OGG container to raw PCM (16-bit, mono).
    /// Returns PCM bytes and the sample rate.
    /// </summary>
    public static (byte[] PcmData, int SampleRate, int Channels) Decode(byte[] opusData)
    {
        _logger.LogDebug("Opus decoding: {Len} bytes", opusData.Length);

        using var inputStream = new MemoryStream(opusData);
        var oggIn = new OpusOggReadStream(new OpusDecoder(DefaultSampleRate, DefaultChannels), inputStream);

        var allSamples = new List<short>();
        while (oggIn.HasNextPacket)
        {
            var packet = oggIn.DecodeNextPacket();
            if (packet != null)
                allSamples.AddRange(packet);
        }

        // Convert shorts to bytes
        var pcmBytes = new byte[allSamples.Count * 2];
        Buffer.BlockCopy(allSamples.ToArray(), 0, pcmBytes, 0, pcmBytes.Length);

        var duration = TimeSpan.FromSeconds((double)allSamples.Count / DefaultSampleRate);
        _logger.LogInformation("Opus decoded: {InLen} bytes → {OutLen} bytes PCM ({Duration:F1}s)",
            opusData.Length, pcmBytes.Length, duration.TotalSeconds);

        return (pcmBytes, DefaultSampleRate, DefaultChannels);
    }

    /// <summary>
    /// Returns the duration of an Opus OGG file without fully decoding it.
    /// </summary>
    public static TimeSpan GetDuration(byte[] opusData)
    {
        using var inputStream = new MemoryStream(opusData);
        var oggIn = new OpusOggReadStream(new OpusDecoder(DefaultSampleRate, DefaultChannels), inputStream);

        int totalSamples = 0;
        while (oggIn.HasNextPacket)
        {
            var packet = oggIn.DecodeNextPacket();
            if (packet != null)
                totalSamples += packet.Length;
        }

        return TimeSpan.FromSeconds((double)totalSamples / DefaultSampleRate);
    }
}

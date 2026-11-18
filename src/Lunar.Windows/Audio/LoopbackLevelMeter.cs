using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Audio;

namespace Lunar.Windows.Audio;

/// <summary>The shared-mode sample formats the meter can read.</summary>
internal enum LoopbackSampleFormat
{
    Unsupported,
    Float32,
    Pcm16,
    Pcm24,
    Pcm32
}

/// <summary>The mix format Windows reports for a playback device, reduced to what the meter needs.</summary>
internal readonly record struct LoopbackFormat(LoopbackSampleFormat Sample, int Channels, int SampleRate, int BlockAlign)
{
    private const ushort TagPcm = 1;
    private const ushort TagFloat = 3;
    private const ushort TagExtensible = 0xFFFE;
    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public bool IsReadable => Sample != LoopbackSampleFormat.Unsupported && Channels > 0 && SampleRate > 0 && BlockAlign > 0;

    /// <summary>Reads a WAVEFORMATEX or WAVEFORMATEXTENSIBLE from its bytes (tag, channels, rate, bytes per second, block align, bits, size, ...).</summary>
    public static LoopbackFormat Parse(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 16)
            return default;
        var tag = BitConverter.ToUInt16(wave[..2]);
        var channels = BitConverter.ToUInt16(wave[2..4]);
        var rate = BitConverter.ToInt32(wave[4..8]);
        var blockAlign = BitConverter.ToUInt16(wave[12..14]);
        var bits = BitConverter.ToUInt16(wave[14..16]);
        if (tag == TagExtensible)
        {
            if (wave.Length < 40)
                return default;
            var sub = new Guid(wave.Slice(24, 16));
            tag = sub == SubTypeFloat ? TagFloat : sub == SubTypePcm ? TagPcm : (ushort)0;
        }

        var sample = (tag, bits) switch
        {
            (TagFloat, 32) => LoopbackSampleFormat.Float32,
            (TagPcm, 16) => LoopbackSampleFormat.Pcm16,
            (TagPcm, 24) => LoopbackSampleFormat.Pcm24,
            (TagPcm, 32) => LoopbackSampleFormat.Pcm32,
            _ => LoopbackSampleFormat.Unsupported
        };
        return new LoopbackFormat(sample, channels, rate, blockAlign);
    }
}

/// <summary>
/// Turns loopback packets into <see cref="PlaybackLevels"/> (ADR-0026): converts the device's samples to floats, feeds a
/// <see cref="SpectrumAnalyzer"/> with 6 bands from 40 Hz to 16 kHz, and goes flat when Windows marks a packet silent or sends nothing for a
/// while (loopback delivers no packets when nothing plays). Pure apart from the analyzer's buffers; owned by the capture thread.
/// </summary>
internal sealed class LoopbackLevelMeter
{
    /// <summary>No packet for this long means nothing is playing.</summary>
    internal static readonly TimeSpan QuietAfter = TimeSpan.FromMilliseconds(120);

    private readonly LoopbackFormat _format;
    private readonly SpectrumAnalyzer _analyzer;
    private float[] _samples = new float[4096];
    private TimeSpan _lastPacket;
    private bool _flat = true;

    public LoopbackLevelMeter(LoopbackFormat format)
    {
        if (!format.IsReadable)
            throw new ArgumentException("The format cannot be read.", nameof(format));
        _format = format;
        _analyzer = new SpectrumAnalyzer(format.SampleRate, PlaybackLevelBands.Count, 40, 16000);
    }

    /// <summary>Adds one packet of <paramref name="frames"/> frames. A silent packet counts as zeros.</summary>
    public void AddPacket(ReadOnlySpan<byte> data, int frames, bool silent, TimeSpan now)
    {
        if (frames <= 0)
            return;
        _lastPacket = now;
        var count = frames * _format.Channels;
        if (_samples.Length < count)
            _samples = new float[count];
        var samples = _samples.AsSpan(0, count);
        if (silent || data.Length < frames * _format.BlockAlign)
        {
            samples.Clear();
        }
        else
        {
            Convert(data, samples);
            _flat = false;
        }

        _analyzer.Add(samples, _format.Channels);
    }

    /// <summary>The reading for now: flat after <see cref="QuietAfter"/> without packets.</summary>
    public PlaybackLevels Read(TimeSpan now)
    {
        if (_flat || now - _lastPacket > QuietAfter)
        {
            _flat = true;
            return PlaybackLevels.Flat;
        }

        return new PlaybackLevels(_analyzer.Compute());
    }

    private void Convert(ReadOnlySpan<byte> data, Span<float> samples)
    {
        var bytesPerSample = _format.BlockAlign / _format.Channels;
        for (var i = 0; i < samples.Length; i++)
        {
            var at = i * bytesPerSample;
            samples[i] = _format.Sample switch
            {
                LoopbackSampleFormat.Float32 => BitConverter.ToSingle(data.Slice(at, 4)),
                LoopbackSampleFormat.Pcm16 => BitConverter.ToInt16(data.Slice(at, 2)) / 32768f,
                LoopbackSampleFormat.Pcm24 => ((data[at] << 8) | (data[at + 1] << 16) | (data[at + 2] << 24)) / 2147483648f,
                LoopbackSampleFormat.Pcm32 => BitConverter.ToInt32(data.Slice(at, 4)) / 2147483648f,
                _ => 0
            };
        }
    }
}

/// <summary>Copies a native buffer into a reusable managed array, so the meter never reads native memory after the buffer is released.</summary>
internal static class NativeBuffer
{
    public static ReadOnlySpan<byte> Copy(IntPtr source, int length, ref byte[] scratch)
    {
        if (length <= 0 || source == IntPtr.Zero)
            return [];
        if (scratch.Length < length)
            scratch = new byte[length];
        Marshal.Copy(source, scratch, 0, length);
        return scratch.AsSpan(0, length);
    }
}

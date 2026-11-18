using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Windows.Audio;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Audio;

/// <summary>BE-45 (ADR-0026): reading the level of what Windows plays.</summary>
public sealed class PlaybackLevelsTests
{
    private const string AudibleOptIn = "LUNAR_AUDIBLE_TESTS";

    [Fact]
    public void ReadsAFloatExtensibleMixFormat()
    {
        var format = LoopbackFormat.Parse(Wave(tag: 0xFFFE, channels: 2, rate: 48000, bits: 32, subFormat: "00000003-0000-0010-8000-00AA00389B71"));
        Assert.Equal(new LoopbackFormat(LoopbackSampleFormat.Float32, 2, 48000, 8), format);
        Assert.True(format.IsReadable);
    }

    [Fact]
    public void ReadsPlainPcm16AndRefusesWhatItCannotRead()
    {
        Assert.Equal(LoopbackSampleFormat.Pcm16, LoopbackFormat.Parse(Wave(tag: 1, channels: 2, rate: 44100, bits: 16)).Sample);
        Assert.False(LoopbackFormat.Parse(Wave(tag: 2, channels: 2, rate: 44100, bits: 4)).IsReadable); // ADPCM
        Assert.False(LoopbackFormat.Parse(new byte[10]).IsReadable);
        Assert.False(LoopbackFormat.Parse(Wave(tag: 0xFFFE, channels: 2, rate: 48000, bits: 32, subFormat: "00000092-0000-0010-8000-00AA00389B71")).IsReadable);
    }

    [Fact]
    public void ASineLightsTheBandItFallsIn()
    {
        var meter = new LoopbackLevelMeter(new LoopbackFormat(LoopbackSampleFormat.Float32, 2, 48000, 8));
        var now = TimeSpan.Zero;
        foreach (var packet in SinePackets(1000, 48000, 0.5f, packets: 8, frames: 480))
        {
            now += TimeSpan.FromMilliseconds(10);
            meter.AddPacket(packet, 480, silent: false, now);
        }

        var levels = meter.Read(now);
        Assert.Equal(PlaybackLevelBands.Count, levels.Bands.Count);
        var loudest = levels.Bands.Select((value, band) => (value, band)).MaxBy(x => x.value).band;
        Assert.InRange(levels.Bands[loudest], 0.7f, 1f);

        // 6 log bands from 40 Hz to 16 kHz: edges about 40, 108, 292, 789, 2133, 5768, 16000 Hz, so 1 kHz is the fourth band.
        Assert.Equal(3, loudest);
    }

    [Fact]
    public void SilenceAndNoPacketsAreFlat()
    {
        var meter = new LoopbackLevelMeter(new LoopbackFormat(LoopbackSampleFormat.Float32, 2, 48000, 8));
        Assert.Equal(PlaybackLevels.Flat.Bands, meter.Read(TimeSpan.Zero).Bands);

        meter.AddPacket([], 480, silent: true, TimeSpan.FromMilliseconds(10));
        Assert.All(meter.Read(TimeSpan.FromMilliseconds(10)).Bands, b => Assert.Equal(0f, b));

        var now = TimeSpan.FromMilliseconds(20);
        foreach (var packet in SinePackets(440, 48000, 0.5f, packets: 4, frames: 480))
            meter.AddPacket(packet, 480, silent: false, now);
        Assert.Contains(meter.Read(now).Bands, b => b > 0.5f);

        // Loopback sends nothing when nothing plays: after a short quiet the reading is flat, never the last sound frozen.
        Assert.All(meter.Read(now + LoopbackLevelMeter.QuietAfter + TimeSpan.FromMilliseconds(1)).Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void Pcm16IsConvertedToTheSameLevelsAsFloat()
    {
        var meter = new LoopbackLevelMeter(new LoopbackFormat(LoopbackSampleFormat.Pcm16, 1, 48000, 2));
        var now = TimeSpan.Zero;
        for (var p = 0; p < 4; p++)
        {
            var bytes = new byte[480 * 2];
            for (var i = 0; i < 480; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 1000 * ((p * 480) + i) / 48000) * 16000));
            now += TimeSpan.FromMilliseconds(10);
            meter.AddPacket(bytes, 480, silent: false, now);
        }

        Assert.InRange(meter.Read(now).Bands[3], 0.7f, 1f);
    }

    [Fact]
    public async Task OpensTheRealDefaultPlaybackDeviceAndStopsCleanly()
    {
        using var source = new WasapiPlaybackLevelSource(new NullLog());
        var readings = 0;
        PlaybackLevels? last = null;
        source.LevelsChanged += levels =>
        {
            Interlocked.Increment(ref readings);
            last = levels;
        };

        var started = await source.StartAsync(TestContext.Current.CancellationToken);
        if (started.IsFailure && started.Error!.Code == Lunar.Domain.Shared.ErrorCode.DeviceUnavailable)
            Assert.Skip("This machine has no playback device.");
        Assert.True(started.IsSuccess, started.Error?.SafeMessage);
        Assert.True(source.IsRunning);
        Assert.True((await source.StartAsync(TestContext.Current.CancellationToken)).IsSuccess); // twice is harmless

        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.InRange(Volatile.Read(ref readings), 5, 40); // about 30 a second

        source.Stop();
        Assert.False(source.IsRunning);
        Assert.Equal(PlaybackLevels.Flat.Bands, last!.Bands);
        var after = Volatile.Read(ref readings);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(after, Volatile.Read(ref readings)); // nothing runs after stop
        source.Stop(); // twice is harmless
    }

    /// <summary>The BE-45 proof as a test: plays a quiet 1 kHz tone through the default device and sees it in the right band. Audible, so opt-in.</summary>
    [Fact]
    public async Task ARealToneOnTheSpeakersMovesTheRightBand()
    {
        if (System.Environment.GetEnvironmentVariable(AudibleOptIn) != "1")
            Assert.Skip($"Plays a short quiet tone; set {AudibleOptIn}=1 to run it.");

        using var source = new WasapiPlaybackLevelSource(new NullLog());
        var peaks = new float[PlaybackLevelBands.Count];
        source.LevelsChanged += levels =>
        {
            lock (peaks)
            {
                for (var i = 0; i < peaks.Length; i++)
                    peaks[i] = Math.Max(peaks[i], levels.Bands[i]);
            }
        };
        Assert.True((await source.StartAsync(TestContext.Current.CancellationToken)).IsSuccess);

        var wave = ToneWav(1000, 48000, seconds: 1.2, amplitude: 0.05);
        var handle = GCHandle.Alloc(wave, GCHandleType.Pinned);
        try
        {
            Assert.True(PlaySound(handle.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndSync | SndNoDefault));
        }
        finally
        {
            handle.Free();
        }

        source.Stop();
        lock (peaks)
        {
            var loudest = Array.IndexOf(peaks, peaks.Max());
            Assert.Equal(3, loudest);
            Assert.True(peaks[3] > 0.2f, $"The tone did not reach the meter (peak {peaks[3]:0.00}); is the output muted?");
        }
    }

    private static byte[] Wave(ushort tag, ushort channels, int rate, ushort bits, string? subFormat = null)
    {
        var bytes = new byte[subFormat is null ? 18 : 40];
        var blockAlign = (ushort)(channels * bits / 8);
        BitConverter.TryWriteBytes(bytes.AsSpan(0), tag);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), channels);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), rate);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), rate * blockAlign);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), blockAlign);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), bits);
        if (subFormat is not null)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(16), (ushort)22);
            new Guid(subFormat).TryWriteBytes(bytes.AsSpan(24));
        }

        return bytes;
    }

    private static IEnumerable<byte[]> SinePackets(double hz, int rate, float amplitude, int packets, int frames)
    {
        var index = 0;
        for (var p = 0; p < packets; p++)
        {
            var bytes = new byte[frames * 8];
            for (var i = 0; i < frames; i++, index++)
            {
                var value = (float)(Math.Sin(2 * Math.PI * hz * index / rate) * amplitude);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), value);
                BitConverter.TryWriteBytes(bytes.AsSpan((i * 8) + 4), value);
            }

            yield return bytes;
        }
    }

    private static byte[] ToneWav(double hz, int rate, double seconds, double amplitude)
    {
        var samples = (int)(rate * seconds);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }

    private const uint SndSync = 0x0000;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }
}

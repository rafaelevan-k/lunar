using System.Numerics;
using Lunar.Application.Abstractions.Audio;

namespace Lunar.Windows.Audio;

/// <summary>
/// Turns the latest samples into frequency bands (ADR-0024): a 1024-point FFT with a Hann window, logarithmic bands from 80 Hz to 8 kHz, each
/// the loudest bin inside it mapped from -60 dB (0) to 0 dB (1). Allocates only the array it returns. Not thread-safe; the audio thread owns it.
/// </summary>
internal sealed class SpectrumAnalyzer
{
    internal const int FftSize = 1024;
    private const double LowestHz = 80;
    private const double HighestHz = 8000;
    private const double FloorDb = -60;

    private readonly float[] _ring = new float[FftSize];
    private readonly Complex[] _bins = new Complex[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly int[] _edges = new int[VoiceLevelBands.Count + 1];
    private int _next;

    public SpectrumAnalyzer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));

        var hzPerBin = (double)sampleRate / FftSize;
        var top = Math.Min(HighestHz, sampleRate / 2.0);
        var ratio = Math.Pow(top / LowestHz, 1.0 / VoiceLevelBands.Count);
        _edges[0] = Math.Max(1, (int)Math.Round(LowestHz / hzPerBin));
        for (var band = 1; band <= VoiceLevelBands.Count; band++)
        {
            var bin = (int)Math.Round(LowestHz * Math.Pow(ratio, band) / hzPerBin);
            _edges[band] = Math.Clamp(bin, _edges[band - 1] + 1, (FftSize / 2) - VoiceLevelBands.Count + band);
        }
    }

    /// <summary>The band a frequency falls in, or -1 outside the range. For tests and for reading the result.</summary>
    internal int BandOfBin(int bin)
    {
        for (var band = 0; band < VoiceLevelBands.Count; band++)
        {
            if (bin >= _edges[band] && bin < _edges[band + 1])
                return band;
        }

        return -1;
    }

    /// <summary>Takes interleaved samples, mixed down to one channel.</summary>
    public void Add(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += interleaved[i + channel];
            _ring[_next] = sum / channels;
            _next = (_next + 1) % FftSize;
        }
    }

    public float[] Compute()
    {
        for (var i = 0; i < FftSize; i++)
            _bins[i] = new Complex(_ring[(_next + i) % FftSize] * _window[i], 0);
        Transform(_bins);

        // A full-scale sine reads 1: the window passes half the signal and the energy is split between the two halves of the spectrum.
        var scale = 4.0 / FftSize;
        var bands = new float[VoiceLevelBands.Count];
        for (var band = 0; band < bands.Length; band++)
        {
            var loudest = 0.0;
            for (var bin = _edges[band]; bin < _edges[band + 1]; bin++)
                loudest = Math.Max(loudest, _bins[bin].Magnitude * scale);
            var db = loudest > 0 ? 20 * Math.Log10(loudest) : double.NegativeInfinity;
            bands[band] = (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }

        return bands;
    }

    private static void Transform(Complex[] data)
    {
        for (int i = 1, j = 0; i < data.Length; i++)
        {
            var bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }

        for (var length = 2; length <= data.Length; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < data.Length; start += length)
            {
                var twiddle = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + (length / 2)] * twiddle;
                    data[start + k] = even + odd;
                    data[start + k + (length / 2)] = even - odd;
                    twiddle *= step;
                }
            }
        }
    }
}

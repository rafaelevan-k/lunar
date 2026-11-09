using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Shared;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Devices;
using Windows.Media.Transcoding;
using WinRT;

namespace Lunar.Windows.Audio;

/// <summary>One recording on an audio graph. The graph's own quantum thread does the counting and the metering.</summary>
internal sealed class WinRtVoiceCaptureSession : IVoiceCaptureSession
{
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FinishLimit = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AudioGraph _graph;
    private readonly AudioFileOutputNode _file;
    private readonly AudioFrameOutputNode _meter;
    private readonly string _deviceId;
    private readonly DeviceWatcher _watcher;
    private readonly SpectrumAnalyzer _analyzer;
    private readonly int _channels;
    private readonly int _quantaPerLevel;
    private readonly double _secondsPerQuantum;
    private float[] _samples = [];
    private long _quanta;
    private int _quantaSinceLevel;
    private int _failed;
    private volatile bool _writing;
    private bool _finished;
    private bool _disposed;

    internal WinRtVoiceCaptureSession(AudioGraph graph, AudioFileOutputNode file, AudioFrameOutputNode meter, string deviceId)
    {
        _graph = graph;
        _file = file;
        _meter = meter;
        _deviceId = deviceId;
        var rate = (int)graph.EncodingProperties.SampleRate;
        _channels = (int)graph.EncodingProperties.ChannelCount;
        _analyzer = new SpectrumAnalyzer(rate);
        _secondsPerQuantum = (double)graph.SamplesPerQuantum / rate;
        _quantaPerLevel = Math.Max(1, (int)Math.Round(LevelInterval.TotalSeconds / _secondsPerQuantum));

        _graph.QuantumProcessed += OnQuantum;
        _graph.UnrecoverableErrorOccurred += OnGraphLost;
        _watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioCaptureSelector());
        _watcher.Added += OnDeviceAdded;
        _watcher.Removed += OnDeviceRemoved;
        _watcher.Start();
        _writing = true;
        _graph.Start();
    }

    public TimeSpan Recorded => TimeSpan.FromSeconds(Interlocked.Read(ref _quanta) * _secondsPerQuantum);

    public event Action<VoiceLevels>? LevelsChanged;

    public event Action<OperationError>? Failed;

    public Task<Result> PauseAsync(CancellationToken ct) => Serialized(() =>
    {
        if (_writing)
        {
            _writing = false;
            _file.Stop();
            RaiseLevels(new float[VoiceLevelBands.Count]);
        }

        return Task.FromResult(Result.Success());
    }, ct);

    public Task<Result> ResumeAsync(CancellationToken ct) => Serialized(() =>
    {
        if (!_writing)
        {
            _file.Start();
            _writing = true;
        }

        return Task.FromResult(Result.Success());
    }, ct);

    public Task<Result> FinishAsync(CancellationToken ct) => Serialized(async () =>
    {
        _writing = false;
        _graph.Stop();
        _file.Stop();
        TranscodeFailureReason outcome;
        try
        {
            outcome = await _file.FinalizeAsync().AsTask(ct).WaitAsync(FinishLimit, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Result.Failure(new OperationError(ErrorCode.Timeout, "Windows took too long to complete the recording."));
        }

        if (outcome != TranscodeFailureReason.None)
            return Result.Failure(new OperationError(ErrorCode.IoFailure, "The recording file could not be completed."));
        _finished = true;
        return Result.Success();
    }, ct);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _writing = false;
            _graph.QuantumProcessed -= OnQuantum;
            _graph.UnrecoverableErrorOccurred -= OnGraphLost;
            _watcher.Added -= OnDeviceAdded;
            _watcher.Removed -= OnDeviceRemoved;
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
            await Task.Run(_graph.Dispose).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result> Serialized(Func<Task<Result>> action, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }

        try
        {
            if (_disposed || _finished)
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "This recording has ended."));
            return await Task.Run(action, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Result.Failure(new OperationError(ErrorCode.Unexpected, "Windows refused that recording step.", retryable: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnQuantum(AudioGraph sender, object args)
    {
        try
        {
            using var frame = _meter.GetFrame();
            if (!_writing)
                return;
            Interlocked.Increment(ref _quanta);
            ReadSamples(frame);
            if (++_quantaSinceLevel >= _quantaPerLevel)
            {
                _quantaSinceLevel = 0;
                RaiseLevels(_analyzer.Compute());
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // A frame that cannot be read costs one meter update; the file node is separate and goes on. Losing the device is reported by the graph.
        }
    }

    private void ReadSamples(AudioFrame frame)
    {
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = ((IMemoryBuffer)buffer).CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
        var count = (int)Math.Min(capacity, buffer.Length) / sizeof(float);
        if (_samples.Length < count)
            _samples = new float[count];
        Marshal.Copy(data, _samples, 0, count);
        _analyzer.Add(_samples.AsSpan(0, count), _channels);
    }

    private void RaiseLevels(float[] bands)
    {
        var handlers = LevelsChanged;
        if (handlers is null)
            return;
        var levels = new VoiceLevels(bands);
        foreach (var handler in handlers.GetInvocationList().Cast<Action<VoiceLevels>>())
        {
            try
            {
                handler(levels);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Isolated: a bad listener must not reach the audio thread.
            }
        }
    }

    private void OnGraphLost(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args) =>
        Fail(VoiceCaptureErrors.FromLostGraph(args.Error));

    // Required for the watcher to report removals.
    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
    {
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (args.Id == _deviceId)
            Fail(new OperationError(ErrorCode.DeviceUnavailable, "The microphone was removed during the recording."));
    }

    private void Fail(OperationError error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
            return;
        var handlers = Failed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<OperationError>>())
        {
            try
            {
                handler(error);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Isolated, like the level listeners.
            }
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out nint buffer, out uint capacity);
    }
}

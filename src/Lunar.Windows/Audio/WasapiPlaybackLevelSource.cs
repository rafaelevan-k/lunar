using System.Diagnostics;
using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Audio;

/// <summary>
/// <see cref="IPlaybackLevelSource"/> through WASAPI loopback on the default playback device (ADR-0026). Every COM object is created, used and
/// released on one background MTA thread that lives only between start and stop. It wakes every 10 ms to collect packets and publishes a reading
/// about every 33 ms. A default-device change or an invalidated device makes the thread reopen the new default device. No audio is kept: packets
/// are reduced to band levels and dropped. The microphone is never opened.
/// </summary>
public sealed class WasapiPlaybackLevelSource : IPlaybackLevelSource
{
    private const string Component = "playback-levels";
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(1);

    private readonly IDiagnosticLog _log;
    private readonly object _gate = new();
    private Worker? _worker;
    private bool _disposed;

    public WasapiPlaybackLevelSource(IDiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public event Action<PlaybackLevels>? LevelsChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _worker is { IsAlive: true };
        }
    }

    public async Task<Result> StartAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();

        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsAlive: true })
                return Result.Success();
            worker = new Worker(this);
            _worker = worker;
        }

        Result opened;
        try
        {
            opened = await worker.StartAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop();
            return Result.Cancelled();
        }

        if (opened.IsFailure)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_worker, worker))
                    _worker = null;
            }

            _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "playback-levels.open-failed", Component, "start", opened.Error!.Code));
        }

        return opened;
    }

    public void Stop()
    {
        Worker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }

        if (worker is null)
            return;
        worker.Stop(StopWait);
        Publish(PlaybackLevels.Flat);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
    }

    private void Publish(PlaybackLevels levels)
    {
        try
        {
            LevelsChanged?.Invoke(levels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "playback-levels.handler-failed", Component, "publish", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
        }
    }

    private void LogReopen() =>
        _log.Write(new DiagnosticEvent(DiagnosticLevel.Information, "playback-levels.reopened", Component, "device-change"));

    private static OperationError Failure(ErrorCode code) => code switch
    {
        ErrorCode.DeviceUnavailable => new OperationError(code, "No playback device is available.", retryable: true),
        ErrorCode.Unsupported => new OperationError(code, "The playback device uses a sound format Lunar cannot read."),
        _ => new OperationError(code, "The playing sound could not be read.", retryable: true)
    };

    /// <summary>The capture thread and everything it owns.</summary>
    private sealed class Worker
    {
        private const int ShareModeShared = 0;
        private const uint StreamFlagsLoopback = 0x00020000;
        private const long BufferDuration = 1_000_000; // 100 ms in 100-ns units
        private const uint BufferFlagsSilent = 0x2;
        private const uint ClsCtxAll = 23;
        private const int EDataFlowRender = 0;
        private const int ERoleConsole = 0;
        private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly TimeSpan Wake = TimeSpan.FromMilliseconds(10);

        private readonly WasapiPlaybackLevelSource _owner;
        private readonly TaskCompletionSource<Result> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private volatile bool _deviceChanged;

        public Worker(WasapiPlaybackLevelSource owner)
        {
            _owner = owner;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lunar playback levels" };
            _thread.SetApartmentState(ApartmentState.MTA);
        }

        public bool IsAlive => _thread.IsAlive && !_stop.IsSet;

        public Task<Result> StartAsync()
        {
            _thread.Start();
            return _opened.Task;
        }

        public void Stop(TimeSpan wait)
        {
            _stop.Set();
            if (Thread.CurrentThread != _thread)
                _thread.Join(wait);
        }

        private void Run()
        {
            IMMDeviceEnumerator? enumerator = null;
            DeviceWatcher? watcher = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClass, throwOnError: true)!)!;
                watcher = new DeviceWatcher(this);
                if (enumerator.RegisterEndpointNotificationCallback(watcher) != 0)
                    watcher = null; // still works; only a change of default device is not followed until the device is invalidated

                var first = true;
                while (!_stop.IsSet)
                {
                    _deviceChanged = false;
                    var result = CaptureOnce(enumerator, first);
                    if (first)
                    {
                        _opened.TrySetResult(result);
                        if (result.IsFailure)
                            return;
                        first = false;
                    }

                    if (_stop.IsSet)
                        break;

                    // The device went away or the default changed: say nothing for a moment, then follow the new default.
                    _owner.Publish(PlaybackLevels.Flat);
                    if (_stop.Wait(TimeSpan.FromMilliseconds(250)))
                        break;
                    _owner.LogReopen();
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
            {
                _opened.TrySetResult(Result.Failure(Failure(ErrorCode.Unexpected)));
            }
            finally
            {
                if (enumerator is not null)
                {
                    if (watcher is not null)
                        _ = enumerator.UnregisterEndpointNotificationCallback(watcher);
                    Marshal.ReleaseComObject(enumerator);
                }

                _opened.TrySetResult(Result.Cancelled());
            }
        }

        /// <summary>Reads one device until stop, a device change or an error. Returns how the device was opened.</summary>
        private Result CaptureOnce(IMMDeviceEnumerator enumerator, bool first)
        {
            IMMDevice? device = null;
            object? clientObject = null;
            object? captureObject = null;
            var mix = IntPtr.Zero;
            try
            {
                var hr = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out device);
                if (hr != 0 || device is null)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));

                var clientId = typeof(IAudioClient).GUID;
                if (device.Activate(ref clientId, ClsCtxAll, IntPtr.Zero, out clientObject) != 0)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));
                var client = (IAudioClient)clientObject;
                if (client.GetMixFormat(out mix) != 0 || mix == IntPtr.Zero)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));

                var size = 18 + Marshal.ReadInt16(mix, 16);
                var bytes = new byte[Math.Clamp(size, 18, 256)];
                Marshal.Copy(mix, bytes, 0, bytes.Length);
                var format = LoopbackFormat.Parse(bytes);
                if (!format.IsReadable)
                    return Result.Failure(Failure(ErrorCode.Unsupported));

                if (client.Initialize(ShareModeShared, StreamFlagsLoopback, BufferDuration, 0, mix, IntPtr.Zero) != 0)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));
                var captureId = typeof(IAudioCaptureClient).GUID;
                if (client.GetService(ref captureId, out captureObject) != 0)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));
                var capture = (IAudioCaptureClient)captureObject;
                if (client.Start() != 0)
                    return Result.Failure(Failure(ErrorCode.DeviceUnavailable));

                if (first)
                    _opened.TrySetResult(Result.Success());
                Pump(capture, format);
                _ = client.Stop();
                return Result.Success();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                return Result.Failure(Failure(ErrorCode.DeviceUnavailable));
            }
            finally
            {
                if (mix != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(mix);
                Release(captureObject);
                Release(clientObject);
                Release(device);
            }
        }

        private void Pump(IAudioCaptureClient capture, LoopbackFormat format)
        {
            var meter = new LoopbackLevelMeter(format);
            var clock = Stopwatch.StartNew();
            var scratch = new byte[16384];
            var nextPublish = TimeSpan.Zero;
            while (!_stop.Wait(Wake) && !_deviceChanged)
            {
                while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    var hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    if (hr != 0)
                        return; // invalidated: the caller reopens the default device
                    try
                    {
                        var length = checked((int)frames * format.BlockAlign);
                        var silent = (flags & BufferFlagsSilent) != 0;
                        var copy = silent ? ReadOnlySpan<byte>.Empty : NativeBuffer.Copy(data, length, ref scratch);
                        meter.AddPacket(copy, (int)frames, silent, clock.Elapsed);
                    }
                    finally
                    {
                        _ = capture.ReleaseBuffer(frames);
                    }
                }

                var now = clock.Elapsed;
                if (now >= nextPublish)
                {
                    nextPublish = now + PlaybackLevelBands.Interval;
                    _owner.Publish(meter.Read(now));
                }
            }
        }

        private static void Release(object? comObject)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }

        /// <summary>Only flags a change; never calls COM from the notification thread.</summary>
        private sealed class DeviceWatcher(Worker owner) : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string deviceId, int newState) => 0;

            public int OnDeviceAdded(string deviceId) => 0;

            public int OnDeviceRemoved(string deviceId) => 0;

            public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
            {
                if (flow == EDataFlowRender && role == ERoleConsole)
                    owner._deviceChanged = true;
                return 0;
            }

            public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
        }
    }
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

using Lunar.Application.Abstractions.Audio;
using Lunar.Domain.Shared;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Lunar.Windows.Audio;

/// <summary>Windows' list of sound inputs (ADR-0022). The watchers run only while somebody listens. No name or id reaches a log.</summary>
public sealed class WindowsAudioInputCatalog : IAudioInputCatalog, IDisposable
{
    private readonly object _gate = new();
    private Action? _changed;
    private DeviceWatcher? _watcher;
    private volatile bool _initialListDone;
    private bool _disposed;

    public async Task<Result<IReadOnlyList<AudioInput>>> GetInputsAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<IReadOnlyList<AudioInput>>();
        try
        {
            var found = await Task.Run(async () => await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector()).AsTask(ct), ct).ConfigureAwait(false);
            var defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            IReadOnlyList<AudioInput> inputs = found.Where(d => d.IsEnabled)
                .Select(d => new AudioInput(d.Id, d.Name, d.Id == defaultId))
                .OrderByDescending(i => i.IsWindowsDefault)
                .ToList();
            return Result.Success(inputs);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<IReadOnlyList<AudioInput>>();
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure<IReadOnlyList<AudioInput>>(new OperationError(ErrorCode.AccessDenied, "Windows refused the list of microphones."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Result.Failure<IReadOnlyList<AudioInput>>(new OperationError(ErrorCode.Unexpected, "The list of microphones could not be read.", retryable: true));
        }
    }

    public event Action? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (_changed is not null && _watcher is null && !_disposed)
                    StartWatching();
            }
        }

        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                    StopWatching();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _changed = null;
            StopWatching();
        }
    }

    private void StartWatching()
    {
        _initialListDone = false;
        _watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioCaptureSelector());
        _watcher.Added += OnAdded;
        _watcher.Removed += OnRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        MediaDevice.DefaultAudioCaptureDeviceChanged += OnDefaultChanged;
        _watcher.Start();
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;
        MediaDevice.DefaultAudioCaptureDeviceChanged -= OnDefaultChanged;
        _watcher.Added -= OnAdded;
        _watcher.Removed -= OnRemoved;
        _watcher.EnumerationCompleted -= OnEnumerationCompleted;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            _watcher.Stop();
        _watcher = null;
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args) => _initialListDone = true;

    // The watcher first reports every device that already exists; that is not a change.
    private void OnAdded(DeviceWatcher sender, DeviceInformation args)
    {
        if (_initialListDone)
            Raise();
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => Raise();

    private void OnDefaultChanged(object sender, DefaultAudioCaptureDeviceChangedEventArgs args) => Raise();

    private void Raise()
    {
        Action? handlers;
        lock (_gate)
            handlers = _changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Isolated by contract: one bad listener must not stop the others or reach Windows.
            }
        }
    }
}

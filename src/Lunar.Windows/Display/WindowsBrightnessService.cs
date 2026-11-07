using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Display;

/// <summary>
/// <see cref="IBrightnessService"/> for Windows (ADR-0021). For a display it first looks for a built-in panel that WMI can dim (matched by the
/// monitor's device id), then for an external monitor that answers DDC/CI, and otherwise reports the display as unsupported. Calls are
/// serialized and run on the thread pool (WMI and DDC/CI both block, DDC/CI for tens of milliseconds). Outside changes of a built-in panel are
/// watched from the first <see cref="Changed"/> subscription until disposal. Log events carry codes only.
/// </summary>
public sealed class WindowsBrightnessService : IBrightnessService
{
    private const string Component = "Brightness";
    private static readonly int[] AccessDeniedCodes = [unchecked((int)0x80070005), unchecked((int)0x80041003)];

    private readonly IWmiBrightness _wmi;
    private readonly IDisplayDirectory _displays;
    private readonly IDiagnosticLog? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _eventGate = new();
    private Action<string>? _changed;
    private IDisposable? _watch;
    private bool _disposed;

    public WindowsBrightnessService(IDiagnosticLog? log = null)
        : this(new WmiBrightness(), new Win32DisplayDirectory(), log)
    {
    }

    internal WindowsBrightnessService(IWmiBrightness wmi, IDisplayDirectory displays, IDiagnosticLog? log = null)
    {
        _wmi = wmi;
        _displays = displays;
        _log = log;
    }

    public event Action<string>? Changed
    {
        add
        {
            lock (_eventGate)
            {
                if (_disposed)
                    return;
                _changed += value;
                _watch ??= StartWatching();
            }
        }
        remove
        {
            lock (_eventGate)
                _changed -= value;
        }
    }

    public Task<Result<BrightnessState>> GetAsync(string? displayId, CancellationToken ct) =>
        RunAsync("Get", () =>
        {
            var display = displayId ?? _displays.PrimaryDisplay();
            if (display is null)
                return Result.Success(BrightnessState.Unavailable);
            return Route(display) switch
            {
                BuiltInRoute panel => Result.Success(new BrightnessState(panel.Percent, BrightnessSource.BuiltIn, CapabilityStatus.Supported)),
                ExternalRoute monitor => Result.Success(new BrightnessState(monitor.Reading.Percent, BrightnessSource.External, CapabilityStatus.Supported)),
                MissingRoute => Result.Success(BrightnessState.Unavailable),
                _ => Result.Success(BrightnessState.Unsupported)
            };
        }, ct);

    public async Task<Result> SetAsync(string? displayId, int percent, CancellationToken ct)
    {
        if (percent is < 0 or > 100)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Brightness must be between 0 and 100."));

        var set = await RunAsync("Set", () =>
        {
            var display = displayId ?? _displays.PrimaryDisplay();
            if (display is null)
                return Result.Failure<bool>(new OperationError(ErrorCode.DeviceUnavailable, "The display is not connected."));
            switch (Route(display))
            {
                case BuiltInRoute panel:
                    _wmi.Set(panel.InstanceName, percent);
                    return Result.Success(true);
                case ExternalRoute monitor:
                    return _displays.WriteDdc(display, monitor.Reading.ToRaw(percent))
                        ? Result.Success(true)
                        : Result.Failure<bool>(new OperationError(ErrorCode.IoFailure, "The monitor did not accept the brightness.", retryable: true));
                case MissingRoute:
                    return Result.Failure<bool>(new OperationError(ErrorCode.DeviceUnavailable, "The display is not connected."));
                default:
                    return Result.Failure<bool>(new OperationError(ErrorCode.Unsupported, "Brightness can't be changed here for this display."));
            }
        }, ct).ConfigureAwait(false);
        return set.IsSuccess ? Result.Success() : Result.Failure(set.Error!);
    }

    public ValueTask DisposeAsync()
    {
        IDisposable? watch;
        lock (_eventGate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            watch = _watch;
            _watch = null;
            _changed = null;
        }

        watch?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- routing ------------------------------------------------------------------------------------------------------

    private abstract record DisplayRoute;

    private sealed record BuiltInRoute(string InstanceName, int Percent) : DisplayRoute;

    private sealed record ExternalRoute(DdcReading Reading) : DisplayRoute;

    private sealed record MissingRoute : DisplayRoute;

    private sealed record NoRoute : DisplayRoute;

    private DisplayRoute Route(string display)
    {
        var monitors = _displays.MonitorInstances(display);
        if (monitors.Count == 0)
            return new MissingRoute();

        foreach (var panel in ReadPanels())
        {
            if (monitors.Any(m => panel.InstanceName.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                return new BuiltInRoute(panel.InstanceName, panel.Percent);
        }

        return _displays.ReadDdc(display) is { } reading ? new ExternalRoute(reading) : new NoRoute();
    }

    /// <summary>A machine without WMI brightness (most desktops) is not an error: there is simply no built-in panel.</summary>
    private IReadOnlyList<WmiPanel> ReadPanels()
    {
        try
        {
            return _wmi.Read();
        }
        catch (COMException ex) when (!AccessDeniedCodes.Contains(ex.HResult))
        {
            Log(DiagnosticLevel.Debug, "brightness.wmi.none", "Read", ErrorCode.Unsupported, ex.GetType());
            return [];
        }
    }

    private async Task<Result<T>> RunAsync<T>(string operation, Func<Result<T>> work, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<T>();
        }
        catch (ObjectDisposedException)
        {
            return Result.Failure<T>(new OperationError(ErrorCode.DeviceUnavailable, "Brightness is no longer available."));
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(work, ct).ConfigureAwait(false);
            if (result.IsFailure && !result.IsCancelled)
                Log(DiagnosticLevel.Warning, "brightness." + operation.ToLowerInvariant() + ".failed", operation, result.Error!.Code);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<T>();
        }
        catch (COMException ex)
        {
            var code = AccessDeniedCodes.Contains(ex.HResult) ? ErrorCode.AccessDenied : ErrorCode.IoFailure;
            Log(DiagnosticLevel.Warning, "brightness." + operation.ToLowerInvariant() + ".failed", operation, code, ex.GetType());
            return Result.Failure<T>(new OperationError(code, code == ErrorCode.AccessDenied
                ? "Windows did not allow Lunar to " + (operation == "Set" ? "change" : "read") + " the brightness."
                : "Windows did not answer about the brightness.", retryable: true));
        }
        catch (Exception ex) when (ex is InvalidCastException or ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log(DiagnosticLevel.Warning, "brightness." + operation.ToLowerInvariant() + ".failed", operation, ErrorCode.IoFailure, ex.GetType());
            return Result.Failure<T>(new OperationError(ErrorCode.IoFailure, "Windows did not answer about the brightness.", retryable: true));
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while this call ran; nobody waits on the gate any more.
            }
        }
    }

    // ---- outside changes --------------------------------------------------------------------------------------------------

    private IDisposable? StartWatching()
    {
        try
        {
            return _wmi.Watch(OnPanelChanged);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OutOfMemoryException)
        {
            Log(DiagnosticLevel.Warning, "brightness.watch.failed", "Watch", ErrorCode.IoFailure, ex.GetType());
            return null;
        }
    }

    private void OnPanelChanged(string instanceName)
    {
        string? display = null;
        try
        {
            display = _displays.PrimaryDisplay() is { } primary && Matches(primary, instanceName) ? primary : FindDisplay(instanceName);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log(DiagnosticLevel.Debug, "brightness.event.unmatched", "Watch", ErrorCode.IoFailure, ex.GetType());
        }

        if (display is null)
            return;
        Action<string>? handlers;
        lock (_eventGate)
            handlers = _disposed ? null : _changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(display);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log(DiagnosticLevel.Warning, "brightness.handler.failed", "Changed", ErrorCode.Unexpected, ex.GetType());
            }
        }
    }

    private bool Matches(string display, string instanceName) =>
        _displays.MonitorInstances(display).Any(m => instanceName.StartsWith(m, StringComparison.OrdinalIgnoreCase));

    private string? FindDisplay(string instanceName)
    {
        for (var n = 1; n <= 16; n++)
        {
            var display = @"\\.\DISPLAY" + n.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            if (Matches(display, instanceName))
                return display;
        }

        return null;
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, null, exception));
}

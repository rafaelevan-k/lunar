using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Settings;

/// <summary>
/// The one "Start with Windows" switch that the Settings window and the tray menu both show (FE-26 wiring of BE-29). It reads and changes
/// the registration only through <see cref="IStartupRegistration"/> and always shows what Windows has, not what was clicked: the state is
/// read again after every change and whenever <see cref="Refresh"/> is called (the user may have switched Lunar off in Task Manager).
/// The registration is not stored in the settings file. UI thread only.
/// </summary>
public sealed class StartupSwitch
{
    private const string Component = "StartupSwitch";

    private readonly IStartupRegistration? _registration;
    private readonly IDiagnosticLog? _log;

    /// <param name="registration">Null when this copy of Lunar cannot register itself; the switch is then shown as unavailable.</param>
    public StartupSwitch(IStartupRegistration? registration, IDiagnosticLog? log = null)
    {
        _registration = registration;
        _log = log;
        Refresh();
    }

    /// <summary>Raised after the state was read again and may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>False when the registration cannot be read (no port, an unusable location, an unsupported system).</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True when Windows will start Lunar at sign-in.</summary>
    public bool IsOn { get; private set; }

    /// <summary>Reads the state from Windows again.</summary>
    public void Refresh()
    {
        if (_registration is null)
        {
            Set(available: false, on: false);
            return;
        }

        var read = _registration.IsEnabled();
        if (read.IsFailure)
            Log(DiagnosticLevel.Warning, "startup.read.failed", "Read", read.Error!.Code);
        Set(available: read.IsSuccess, on: read.IsSuccess && read.Value);
    }

    /// <summary>Turns the registration on when it is off and off when it is on; the result says whether Windows accepted it.</summary>
    public Result Toggle()
    {
        if (_registration is null || !IsAvailable)
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "Start with Windows is not available."));

        var result = IsOn ? _registration.Disable() : _registration.Enable();
        if (result.IsFailure)
            Log(DiagnosticLevel.Warning, "startup.change.failed", IsOn ? "Disable" : "Enable", result.Error!.Code);
        Refresh();
        return result;
    }

    private void Set(bool available, bool on)
    {
        IsAvailable = available;
        IsOn = on;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode code) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code));
}

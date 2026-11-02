using System.ComponentModel;
using System.Diagnostics;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Environment;

/// <summary>
/// Opens one of the three whitelisted Windows Settings pages (ADR-0002). Callers pass an enum, never a URI, and a
/// launch failure is a failed <see cref="Result"/> instead of an exception.
/// </summary>
public sealed class WindowsSettingsLauncher : ISystemSettingsLauncher
{
    private readonly Action<string> _start;
    private readonly IDiagnosticLog? _log;

    public WindowsSettingsLauncher(IDiagnosticLog? log = null)
        : this(StartShell, log)
    {
    }

    internal WindowsSettingsLauncher(Action<string> start, IDiagnosticLog? log = null)
    {
        _start = start;
        _log = log;
    }

    internal static string? UriFor(SettingsPage page) => page switch
    {
        SettingsPage.Wifi => "ms-settings:network-wifi",
        SettingsPage.Bluetooth => "ms-settings:bluetooth",
        SettingsPage.DateAndTime => "ms-settings:dateandtime",
        _ => null
    };

    public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());

        var uri = UriFor(page);
        if (uri is null)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "That Settings page is not available.")));

        try
        {
            _start(uri);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "settings.open.failed", "Settings", "Open",
                ErrorCode.IoFailure, ExceptionType: ex.GetType()));
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.IoFailure, "Windows Settings could not be opened.", retryable: true)));
        }
    }

    private static void StartShell(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
}

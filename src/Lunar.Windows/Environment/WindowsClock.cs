using System.Globalization;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Environment;

/// <summary>
/// The system clock with the user's time zone and regional format (BE-14). Windows lets the user change both while the
/// app runs, and .NET caches them, so they are re-read at most every <see cref="DefaultRefreshInterval"/> as part of
/// a normal read (or at once by <see cref="Refresh"/>); there is no listener thread, timer or polling of its own. If a
/// setting cannot be read the last good value is kept (UTC and the invariant culture before the first good read), the
/// failure is logged once per kind with the exception type only, and nothing is thrown to the caller. Thread-safe.
/// </summary>
public sealed class WindowsClock : ILocaleClock
{
    /// <summary>How stale the zone and format may get before a read refreshes them.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(30);

    private const string Component = "Clock";

    private readonly TimeProvider _time;
    private readonly Func<TimeZoneInfo> _readZone;
    private readonly Func<CultureInfo> _readCulture;
    private readonly Action _clearCaches;
    private readonly TimeSpan _refreshInterval;
    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();

    private Snapshot _snapshot;
    private bool _zoneFailureLogged;
    private bool _cultureFailureLogged;

    public WindowsClock(IDiagnosticLog? log = null)
        : this(TimeProvider.System, () => TimeZoneInfo.Local, () => CultureInfo.CurrentCulture, ClearSystemCaches, DefaultRefreshInterval, log)
    {
    }

    internal WindowsClock(
        TimeProvider time,
        Func<TimeZoneInfo> readZone,
        Func<CultureInfo> readCulture,
        Action clearCaches,
        TimeSpan refreshInterval,
        IDiagnosticLog? log = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _readZone = readZone ?? throw new ArgumentNullException(nameof(readZone));
        _readCulture = readCulture ?? throw new ArgumentNullException(nameof(readCulture));
        _clearCaches = clearCaches ?? throw new ArgumentNullException(nameof(clearCaches));
        if (refreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "The refresh interval must be positive.");
        _refreshInterval = refreshInterval;
        _log = log;
        _snapshot = new Snapshot(TimeZoneInfo.Utc, CultureInfo.InvariantCulture, long.MinValue);
        Refresh();
    }

    public DateTimeOffset LocalNow
    {
        get
        {
            var snapshot = Current();
            return TimeZoneInfo.ConvertTime(_time.GetUtcNow(), snapshot.Zone);
        }
    }

    public CultureInfo Culture => Current().Culture;

    public TimeZoneInfo TimeZone => Current().Zone;

    public void Refresh()
    {
        lock (_gate)
        {
            try
            {
                _clearCaches();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
            {
                // Stale data is better than no clock; the reads below still run.
            }

            var zone = _snapshot.Zone;
            var culture = _snapshot.Culture;
            try
            {
                zone = _readZone() ?? zone;
                _zoneFailureLogged = false;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException or InvalidOperationException)
            {
                LogOnce(ref _zoneFailureLogged, "clock.zone.failed", ex);
            }

            try
            {
                culture = _readCulture() ?? culture;
                _cultureFailureLogged = false;
            }
            catch (Exception ex) when (ex is CultureNotFoundException or InvalidOperationException)
            {
                LogOnce(ref _cultureFailureLogged, "clock.culture.failed", ex);
            }

            _snapshot = new Snapshot(zone, culture, _time.GetTimestamp());
        }
    }

    private Snapshot Current()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (_time.GetElapsedTime(snapshot.ReadAt) < _refreshInterval)
            return snapshot;

        Refresh();
        return Volatile.Read(ref _snapshot);
    }

    private void LogOnce(ref bool logged, string eventId, Exception ex)
    {
        if (logged)
            return;
        logged = true;
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Read", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
    }

    // .NET caches the zone and the user's regional format; clearing makes the next read see a change the user made.
    private static void ClearSystemCaches()
    {
        TimeZoneInfo.ClearCachedData();
#pragma warning disable CS0618 // marked obsolete in places, but the only way to pick up a regional-format change
        CultureInfo.CurrentCulture.ClearCachedData();
#pragma warning restore CS0618
    }

    private sealed record Snapshot(TimeZoneInfo Zone, CultureInfo Culture, long ReadAt);
}

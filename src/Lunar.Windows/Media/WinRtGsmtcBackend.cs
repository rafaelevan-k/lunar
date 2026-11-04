using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Lunar.Windows.Media;

/// <summary>
/// The real Global System Media Transport Controls implementation of <see cref="IGsmtcBackend"/> (ADR-0002, ADR-0013). It
/// reads the session list, current session, media properties, playback info, timeline and thumbnail, and (BE-16) sends the
/// player a play, pause, toggle, next or previous request when asked. It never synthesizes a key press.
///
/// Ids: a session is identified by its app's user-model id; when several sessions share one (two windows of one browser) the
/// later ones get "#2", "#3" in the order Windows lists them. An id therefore stays the same while the session lives and
/// the set of sessions of that app does not change order; it is not meant to survive a restart.
///
/// Subscriptions: the manager's two events are subscribed once; each session's three events are subscribed while it is in
/// the list and unsubscribed when it leaves it or the backend is disposed. Callbacks arrive on thread-pool threads and only
/// raise <see cref="Changed"/>; nothing calls back into Windows from them. A session that closes under a read is left out
/// of that read. Artwork is read once per track (cached by title, artist and album) and only when it is at most
/// <see cref="Lunar.Application.Media.Models.MediaSessionSnapshot.MaxArtworkBytes"/>.
/// Every call into Windows runs on one dedicated thread (<see cref="GsmtcWorker"/>), because the session objects are bound to
/// the thread that first used them. Calls are serialized by the owning service.
/// </summary>
internal sealed class WinRtGsmtcBackend : IGsmtcBackend
{
    private const int NotRegisteredHResult = unchecked((int)0x80040154);
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    private readonly object _gate = new();
    private readonly GsmtcWorker _worker = new();
    private readonly Dictionary<string, string?> _appNames = [];
    private readonly Dictionary<string, (string Key, byte[] Bytes)> _artwork = [];
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _mediaChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _playbackChanged;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _timelineChanged;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private List<GlobalSystemMediaTransportControlsSession> _tracked = [];
    private List<string> _ids = [];
    private bool _disposed;

    public WinRtGsmtcBackend()
    {
        _sessionsChanged = (_, _) => RaiseChanged();
        _currentChanged = (_, _) => RaiseChanged();
        _mediaChanged = (_, _) => RaiseChanged();
        _playbackChanged = (_, _) => RaiseChanged();
        _timelineChanged = (_, _) => RaiseChanged();
    }

    public event Action? Changed;

    public Task<IReadOnlyList<MediaSessionReading>> ReadAsync(CancellationToken ct) => _worker.RunAsync(() => ReadCoreAsync(ct));

    public Task<GsmtcCommandResult> SendAsync(string sessionId, GsmtcAction action, TimeSpan target, CancellationToken ct) =>
        _worker.RunAsync(() => SendCoreAsync(sessionId, action, target, ct));

    private async Task<IReadOnlyList<MediaSessionReading>> ReadCoreAsync(CancellationToken ct)
    {
        try
        {
            var manager = await ManagerAsync(ct);
            var sessions = manager.GetSessions().ToList();
            var current = manager.GetCurrentSession();
            var ids = AssignIds(sessions);
            Track(sessions, ids);

            var currentIndex = IndexOfCurrent(sessions, current);
            var readings = new List<MediaSessionReading>(sessions.Count);
            for (var i = 0; i < sessions.Count; i++)
            {
                var reading = await ReadOneAsync(sessions[i], ids[i], i == currentIndex, ct);
                if (reading is not null)
                    readings.Add(reading);
            }

            Forget(ids);
            return readings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw Translate(ex);
        }
    }

    private async Task<GsmtcCommandResult> SendCoreAsync(string sessionId, GsmtcAction action, TimeSpan target, CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var index = _ids.IndexOf(sessionId);
            session = index >= 0 && index < _tracked.Count ? _tracked[index] : null;
        }

        if (session is null)
            return GsmtcCommandResult.SessionGone;

        try
        {
            // Each of these is a request to the player; true means the player accepted it. Nothing here synthesizes a key press.
            var operation = action switch
            {
                GsmtcAction.Play => session.TryPlayAsync(),
                GsmtcAction.Pause => session.TryPauseAsync(),
                GsmtcAction.Toggle => session.TryTogglePlayPauseAsync(),
                GsmtcAction.Next => session.TrySkipNextAsync(),
                GsmtcAction.Seek => session.TryChangePlaybackPositionAsync(target.Ticks),
                _ => session.TrySkipPreviousAsync()
            };
            var accepted = await operation.AsTask(ct);
            return accepted ? GsmtcCommandResult.Accepted : GsmtcCommandResult.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw Translate(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // The Windows objects are released on the thread that owns them, after anything already queued; this does not wait.
        _worker.Complete(ReleaseWindowsObjects);
    }

    private void ReleaseWindowsObjects()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        List<GlobalSystemMediaTransportControlsSession> tracked;
        lock (_gate)
        {
            manager = _manager;
            _manager = null;
            tracked = _tracked;
            _tracked = [];
            _ids = [];
        }

        if (manager is not null)
        {
            manager.SessionsChanged -= _sessionsChanged;
            manager.CurrentSessionChanged -= _currentChanged;
        }

        foreach (var session in tracked)
            Unsubscribe(session);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager is not null)
                return _manager;
        }

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WinRtGsmtcBackend));
            if (_manager is not null)
                return _manager;
            _manager = manager;
            manager.SessionsChanged += _sessionsChanged;
            manager.CurrentSessionChanged += _currentChanged;
            return manager;
        }
    }

    // One id per session: the app's user-model id, with "#2", "#3" for further sessions of the same app.
    private static List<string> AssignIds(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new List<string>(sessions.Count);
        foreach (var session in sessions)
        {
            var app = session.SourceAppUserModelId ?? string.Empty;
            seen[app] = seen.TryGetValue(app, out var count) ? count + 1 : 1;
            ids.Add(seen[app] == 1 ? app : app + "#" + seen[app].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    private static int IndexOfCurrent(List<GlobalSystemMediaTransportControlsSession> sessions, GlobalSystemMediaTransportControlsSession? current)
    {
        if (current is null)
            return -1;
        var byReference = sessions.FindIndex(s => ReferenceEquals(s, current));
        return byReference >= 0 ? byReference : sessions.FindIndex(s => s.SourceAppUserModelId == current.SourceAppUserModelId);
    }

    // Keeps our event subscriptions on exactly the sessions that are in the list.
    private void Track(List<GlobalSystemMediaTransportControlsSession> sessions, List<string> ids)
    {
        List<GlobalSystemMediaTransportControlsSession> previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _tracked;
            _tracked = sessions;
            _ids = ids;
        }

        foreach (var session in previous.Where(p => !sessions.Any(s => ReferenceEquals(s, p))))
            Unsubscribe(session);
        foreach (var session in sessions.Where(s => !previous.Any(p => ReferenceEquals(s, p))))
            Subscribe(session);
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged += _mediaChanged;
        session.PlaybackInfoChanged += _playbackChanged;
        session.TimelinePropertiesChanged += _timelineChanged;
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= _mediaChanged;
            session.PlaybackInfoChanged -= _playbackChanged;
            session.TimelinePropertiesChanged -= _timelineChanged;
        }
        catch (COMException)
        {
            // The session is already gone.
        }
    }

    // Drops cached app names and artwork of sessions that are no longer listed.
    private void Forget(List<string> liveIds)
    {
        foreach (var gone in _artwork.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _artwork.Remove(gone);
    }

    private async Task<MediaSessionReading?> ReadOneAsync(GlobalSystemMediaTransportControlsSession session, string id, bool isCurrent, CancellationToken ct)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(ct);
            var controls = playback.Controls;
            var artwork = properties?.Thumbnail is { } thumbnail
                ? await ArtworkAsync(id, $"{properties.Title}|{properties.Artist}|{properties.AlbumTitle}", thumbnail, ct)
                : [];

            var stamp = timeline.LastUpdatedTime;
            return new MediaSessionReading(
                id,
                AppName(session.SourceAppUserModelId),
                properties?.Title,
                properties?.Artist,
                ToStatus(playback.PlaybackStatus),
                timeline.StartTime,
                timeline.EndTime,
                timeline.Position,
                stamp.Year > 1 ? stamp : null,
                playback.PlaybackRate ?? 1.0,
                controls.IsPlayEnabled,
                controls.IsPauseEnabled,
                controls.IsNextEnabled,
                controls.IsPreviousEnabled,
                controls.IsPlaybackPositionEnabled,
                isCurrent,
                artwork,
                controls.IsPlayPauseToggleEnabled);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return null; // the session closed or changed under the read; the next read has it right
        }
    }

    private async Task<byte[]> ArtworkAsync(string id, string key, IRandomAccessStreamReference thumbnail, CancellationToken ct)
    {
        if (_artwork.TryGetValue(id, out var cached) && cached.Key == key)
            return cached.Bytes;

        var bytes = Array.Empty<byte>();
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(ct);
            var size = stream.Size;
            if (size is > 0 and <= Lunar.Application.Media.Models.MediaSessionSnapshot.MaxArtworkBytes)
            {
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)size).AsTask(ct);
                bytes = new byte[size];
                reader.ReadBytes(bytes);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            bytes = []; // no artwork is a normal state
        }

        _artwork[id] = (key, bytes);
        return bytes;
    }

    private string? AppName(string? appUserModelId)
    {
        if (string.IsNullOrEmpty(appUserModelId))
            return null;
        if (_appNames.TryGetValue(appUserModelId, out var known))
            return known;

        string? name = null;
        try
        {
            name = AppInfo.GetFromAppUserModelId(appUserModelId)?.DisplayInfo?.DisplayName;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            // Not a packaged app: the cleaned id below is the name.
        }

        name = string.IsNullOrWhiteSpace(name) ? MediaSnapshotMapper.FriendlyAppName(appUserModelId) : name;
        _appNames[appUserModelId] = name;
        return name;
    }

    private void RaiseChanged()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        Changed?.Invoke();
    }

    internal static GsmtcStatus ToStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => GsmtcStatus.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => GsmtcStatus.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => GsmtcStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => GsmtcStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => GsmtcStatus.Paused,
        _ => GsmtcStatus.Closed
    };

    private static MediaException Translate(Exception ex) => ex switch
    {
        MediaException known => known,
        UnauthorizedAccessException => new MediaException(MediaFailure.AccessDenied, AccessDeniedHResult, "Windows refused the media call."),
        COMException com when com.HResult == AccessDeniedHResult => new MediaException(MediaFailure.AccessDenied, com.HResult, "Windows refused the media call."),
        COMException com when com.HResult == NotRegisteredHResult => new MediaException(MediaFailure.NotSupported, com.HResult, "The Windows media-session API is not available."),
        TypeLoadException or PlatformNotSupportedException or NotSupportedException or FileNotFoundException or DllNotFoundException =>
            new MediaException(MediaFailure.NotSupported, ex.HResult, "The Windows media-session API is not available."),
        _ => new MediaException(MediaFailure.Other, ex.HResult, "The Windows media call failed (0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").")
    };
}

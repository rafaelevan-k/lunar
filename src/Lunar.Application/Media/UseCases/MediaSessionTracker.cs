using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Reactive;
using Lunar.Domain.Shared;

namespace Lunar.Application.Media.UseCases;

/// <summary>
/// Chooses which media session is active (BE-18, PRD FR-MED-05) and keeps that choice while sessions come and go.
///
/// Policy, the same every time for the same list (<see cref="Choose"/>): the session the user picked, as long as it is open;
/// otherwise the session Windows treats as current; otherwise a playing session, then any session, the lowest id first, so
/// the result never depends on the order Windows listed them in. A choice of the user is kept until that session closes; once it
/// has closed the choice is forgotten (the same id appearing later is a new session) and the policy takes over again.
///
/// The tracker listens to <see cref="IMediaService.Changes"/> from <see cref="InitializeAsync"/> until it is disposed, and
/// publishes an <see cref="ActiveMediaState"/> whenever the list or the active session changes. When the player behind the active
/// session exits, the next published state is already the fallback; when a player starts again it is simply in the next list.
/// Observable semantics are those of <see cref="StateBroadcaster{T}"/> (ADR-0006). Thread-safe.
/// </summary>
public sealed class MediaSessionTracker : IAsyncDisposable, IDisposable
{
    private readonly IMediaService _service;
    private readonly StateBroadcaster<ActiveMediaState> _changes = new();
    private readonly object _gate = new();

    private IDisposable? _subscription;
    private ActiveMediaState? _current;
    private string? _selectedId;
    private bool _disposed;

    public MediaSessionTracker(IMediaService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>The last state published; null before the first list arrived.</summary>
    public ActiveMediaState? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public IObservable<ActiveMediaState> Changes => _changes;

    /// <summary>
    /// Starts listening, then reads the sessions once. Listening starts first so no change is missed; a list that arrives while
    /// the first read is still running is newer and wins. A failed read leaves the tracker listening, so a later change still
    /// produces a state.
    /// </summary>
    public async Task<Result<ActiveMediaState>> InitializeAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<ActiveMediaState>();

        lock (_gate)
        {
            if (_disposed)
                return Result.Failure<ActiveMediaState>(Closed);
            _subscription ??= _service.Changes.Subscribe(new Listener(this));
        }

        var read = await _service.GetSessionsAsync(ct).ConfigureAwait(false);
        if (read.IsCancelled)
            return Result.Cancelled<ActiveMediaState>();
        if (read.IsFailure)
            return Result.Failure<ActiveMediaState>(read.Error!);

        lock (_gate)
        {
            if (_disposed)
                return Result.Failure<ActiveMediaState>(Closed);
            if (_current is null)
                Apply(read.Value);
            return Result.Success(_current!);
        }
    }

    /// <summary>
    /// The user picks a session. Only an open session can be picked (<see cref="ErrorCode.InvalidInput"/> otherwise). The pick is
    /// kept until that session closes; picking the session that is already active because of the policy makes it a pick.
    /// </summary>
    public Result Select(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "A media session id is required."));

        lock (_gate)
        {
            if (_disposed)
                return Result.Failure(Closed);
            if (_current is null || !_current.Sessions.Any(s => s.Id == sessionId))
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That media session is not open."));

            _selectedId = sessionId;
            Apply(_current.Sessions);
            return Result.Success();
        }
    }

    /// <summary>Forgets the user's pick; the policy chooses again.</summary>
    public void ClearSelection()
    {
        lock (_gate)
        {
            if (_disposed || _selectedId is null)
                return;
            _selectedId = null;
            if (_current is not null)
                Apply(_current.Sessions);
        }
    }

    /// <summary>
    /// The deterministic policy: the selected session when it is in the list, else the one Windows marks current, else a playing
    /// one, else any, always the lowest id first among equals. Null when the list is empty.
    /// </summary>
    public static MediaSessionSnapshot? Choose(IReadOnlyList<MediaSessionSnapshot> sessions, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
            return null;

        if (selectedId is not null && sessions.FirstOrDefault(s => s.Id == selectedId) is { } picked)
            return picked;

        var ordered = sessions.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        return ordered.FirstOrDefault(s => s.IsCurrent)
            ?? ordered.FirstOrDefault(s => s.Status == MediaPlaybackStatus.Playing)
            ?? ordered[0];
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        _changes.Complete();
    }

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The media session tracker is closed.");

    private void OnList(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Apply(sessions);
        }
    }

    // Callers hold the gate. Forgets a pick whose session is gone, chooses, and publishes when the result differs from the last.
    private void Apply(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        if (_selectedId is not null && !sessions.Any(s => s.Id == _selectedId))
            _selectedId = null;

        var active = Choose(sessions, _selectedId);
        var next = new ActiveMediaState(sessions, active, IsUserChoice: _selectedId is not null && active is not null);
        if (_current is not null && ReferenceEquals(_current.Sessions, sessions) && _current.Active == next.Active && _current.IsUserChoice == next.IsUserChoice)
            return;

        _current = next;
        _changes.Publish(next);
    }

    private sealed class Listener(MediaSessionTracker owner) : IObserver<IReadOnlyList<MediaSessionSnapshot>>
    {
        public void OnNext(IReadOnlyList<MediaSessionSnapshot> value) => owner.OnList(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => owner.Dispose();
    }
}

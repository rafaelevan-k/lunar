using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>Which of the Media page's four faces is showing.</summary>
public enum MediaPageMode
{
    /// <summary>Nothing has been read yet: a placeholder.</summary>
    Loading,

    /// <summary>No media session is open: the page explains it.</summary>
    NoSession,

    /// <summary>The sessions could not be read: the page says why and offers Retry.</summary>
    Unavailable,

    /// <summary>A session is active: artwork, title, artist, source, timeline and transport buttons.</summary>
    Session
}

/// <summary>
/// What the Media page shows and can do (FE-17): artwork, title, artist, source app, play state, the position and length of the
/// media, and previous, play/pause and next buttons. It reads the Application layer only (<see cref="MediaSessionTracker"/> for
/// which session is active, <see cref="MediaTransport"/> to send a button press), never a Windows API.
///
/// Every state has its place: loading (placeholder), no session (an explanation), unavailable (the reason and Retry), and a
/// session with absent metadata ("Unknown title", "Unknown artist", "Unknown app"), absent or undecodable artwork (a
/// placeholder), a live or timeline-less stream (a note instead of times), and a timeline that cannot be seeked (a note). A button
/// is enabled only when the session advertises that command. A press never changes the shown state: the page shows what the
/// player reports through the tracker, so a refused command leaves it as it was and says so.
///
/// While the island is open the position moves along once a second while playing (<see cref="StartTicker"/>); nothing runs
/// while it is closed. The session selector belongs to FE-19.
///
/// Seeking (FE-18): the position the player reports and the position the user is dragging to are two different things. While
/// the thumb is held (<see cref="IsScrubbing"/>) the page shows the drag position and ignores the ticker and the player's
/// reports, so the thumb never jumps under the hand. Nothing is sent during the drag; on release one seek goes to
/// <see cref="SeekMedia"/> and the page keeps showing the released position (no snap back and forth) until the outcome is
/// known. A seek that succeeds continues from what the player then reports; one that fails says so in words and the page
/// goes back to the real position. A drag whose media changed or stopped being seekable meanwhile is dropped, never sent.
///
/// Port callbacks arrive on other threads and are marshalled through <c>post</c> before any property changes. Use on the UI thread.
/// </summary>
public sealed class MediaPageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "MediaPage";

    private readonly MediaSessionTracker _tracker;
    private readonly MediaTransport _transport;
    private readonly SeekMedia _seek;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private IOneShotTimer? _timer;
    private IDisposable? _subscription;
    private ReadOnlyMemory<byte> _artworkBytes;

    private bool _started;
    private bool _disposed;
    private bool _tickerRunning;
    private bool _hasState;
    private MediaPageMode _mode = MediaPageMode.Loading;
    private MediaSessionSnapshot? _active;
    private DateTimeOffset _activeAt;
    private IReadOnlyList<MediaSessionSnapshot> _sessions = [];
    private bool _isUserChoice;
    private (TimeSpan Position, DateTimeOffset At)? _seekAnchor;
    private bool _isScrubbing;
    private bool _userMoved;
    private bool _ignoreUntilRelease;
    private bool _seeking;
    private int _seekGeneration;
    private double _seekValue;
    private ImageSource? _artwork;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _source = string.Empty;
    private string _statusText = string.Empty;
    private string _sourceLine = string.Empty;
    private string _summary = string.Empty;
    private string? _timelineNote;
    private string _positionText = string.Empty;
    private string _durationText = string.Empty;
    private double _progress;
    private string? _unavailableText;
    private string? _errorMessage;

    public MediaPageViewModel(MediaSessionTracker tracker, MediaTransport transport, SeekMedia seek, Action<Action> post, IDiagnosticLog? log = null)
        : this(tracker, transport, seek, post, log, TimeProvider.System, timer: null)
    {
    }

    internal MediaPageViewModel(
        MediaSessionTracker tracker,
        MediaTransport transport,
        SeekMedia seek,
        Action<Action> post,
        IDiagnosticLog? log,
        TimeProvider time,
        IOneShotTimer? timer)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(seek);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(time);
        _tracker = tracker;
        _transport = transport;
        _seek = seek;
        _post = post;
        _log = log;
        _time = time;
        _timer = timer;

        var lifetime = _lifetime.Token;
        PlayPauseCommand = new AsyncRelayCommand(ct => SendAsync(MediaCommand.PlayPause, ct), () => CanSend(MediaCommand.PlayPause), OnFault, lifetime);
        NextCommand = new AsyncRelayCommand(ct => SendAsync(MediaCommand.Next, ct), () => CanSend(MediaCommand.Next), OnFault, lifetime);
        PreviousCommand = new AsyncRelayCommand(ct => SendAsync(MediaCommand.Previous, ct), () => CanSend(MediaCommand.Previous), OnFault, lifetime);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => _mode == MediaPageMode.Unavailable, OnFault, lifetime);
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        SwitchSessionCommand = new RelayCommand(SwitchSession, () => HasMultipleSessions);
        UseAutomaticCommand = new RelayCommand(UseAutomatic, () => ShowAutomatic);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- which face --------------------------------------------------------------------------------------------------

    public MediaPageMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value))
                return;
            OnChanged(nameof(IsLoading));
            OnChanged(nameof(IsNoSession));
            OnChanged(nameof(IsUnavailable));
            OnChanged(nameof(HasSession));
            ((AsyncRelayCommand)RetryCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsLoading => _mode == MediaPageMode.Loading;

    public bool IsNoSession => _mode == MediaPageMode.NoSession;

    public bool IsUnavailable => _mode == MediaPageMode.Unavailable;

    public bool HasSession => _mode == MediaPageMode.Session;

    public string NoSessionTitle => MediaPageText.NoSessionTitle;

    public string NoSessionHint => MediaPageText.NoSessionHint;

    /// <summary>Why the sessions could not be read, while <see cref="IsUnavailable"/>.</summary>
    public string? UnavailableText
    {
        get => _unavailableText;
        private set => Set(ref _unavailableText, value);
    }

    // ---- the session -------------------------------------------------------------------------------------------------

    /// <summary>The active session, or null when there is none. Commands act on this one.</summary>
    public MediaSessionSnapshot? Active => _active;

    /// <summary>The artwork, or null when there is none or it could not be decoded (the page shows a placeholder).</summary>
    public ImageSource? Artwork
    {
        get => _artwork;
        private set
        {
            if (Set(ref _artwork, value))
                OnChanged(nameof(HasArtwork));
        }
    }

    public bool HasArtwork => _artwork is not null;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        private set => Set(ref _artist, value);
    }

    /// <summary>The app that plays it ("Spotify"), or "Unknown app".</summary>
    public string Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    /// <summary>The source and the play state on one line, for example "Spotify · Playing"; just the source while the player is changing track.</summary>
    public string SourceLine
    {
        get => _sourceLine;
        private set => Set(ref _sourceLine, value);
    }

    /// <summary>"Playing", "Paused" or "Stopped"; empty while the player is changing track.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The whole session in one sentence for assistive technology.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    // ---- the timeline ------------------------------------------------------------------------------------------------

    /// <summary>True when there is a finite length to show times for.</summary>
    public bool HasTimeline => _active?.Duration is { } d && d > TimeSpan.Zero;

    /// <summary>"Live or no timeline" or "This app doesn't allow seeking"; null when the timeline can be seeked.</summary>
    public string? TimelineNote
    {
        get => _timelineNote;
        private set => Set(ref _timelineNote, value);
    }

    /// <summary>Where the media is now, mm:ss or hh:mm:ss.</summary>
    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    /// <summary>How far along the media is, 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>The timeline in one sentence for assistive technology, for example "01:12 of 03:45".</summary>
    public string TimelineLabel => HasTimeline ? $"{_positionText} of {_durationText}" : _timelineNote ?? string.Empty;

    // ---- seeking (FE-18) ---------------------------------------------------------------------------------------------

    /// <summary>True when the timeline is finite and the player offers seeking: the page shows a slider instead of a read-only bar.</summary>
    public bool CanSeek => _active?.CanSeek == true;

    /// <summary>True for a finite timeline that cannot be seeked: the page shows the read-only bar and says why.</summary>
    public bool ShowReadOnlyBar => HasTimeline && !CanSeek;

    /// <summary>
    /// Set by the slider (one way to source) while the user holds it. While true the page shows the drag position and ignores
    /// the ticker and the player's reports; the seek is sent when it turns false.
    /// </summary>
    public bool IsScrubbing
    {
        get => _isScrubbing;
        set
        {
            if (_isScrubbing == value)
                return;
            _isScrubbing = value;
            if (value)
            {
                // The user takes over the thumb: any seek still waiting for its outcome no longer owns the display.
                _seekGeneration++;
                _seeking = false;
                _seekAnchor = null;
                return;
            }

            _ignoreUntilRelease = false;
            if (_userMoved)
                CommitSeek();
            else
                RefreshPosition();
        }
    }

    /// <summary>
    /// The slider value, 0 to 1 of the media. It follows the player while the user is not holding it; while they are, it is
    /// where they dragged to, and the page makes no write of its own during that time.
    /// </summary>
    public double SeekValue
    {
        get => _seekValue;
        set
        {
            if (double.IsNaN(value))
                return;
            value = Math.Clamp(value, 0.0, 1.0);
            // The slider echoes what the page just set: that is not the user moving it.
            if (Math.Abs(value - _seekValue) < 1e-9)
                return;
            if (!CanSeek || _ignoreUntilRelease)
                return;

            _seekValue = value;
            _userMoved = true;
            OnChanged(nameof(SeekValue));
            ShowDragPosition();
            // Not held (a screen reader, or a click whose drag has not started yet): decide once the current input is done.
            if (!_isScrubbing)
                _post(CommitIfReleased);
        }
    }

    /// <summary>A small keyboard step (about 5 seconds) as a fraction of the media.</summary>
    public double SeekSmallChange => StepFraction(TimeSpan.FromSeconds(5));

    /// <summary>A large keyboard step (about 30 seconds) as a fraction of the media.</summary>
    public double SeekLargeChange => StepFraction(TimeSpan.FromSeconds(30));

    // ---- transport ---------------------------------------------------------------------------------------------------

    public bool IsPlaying => _active?.Status == MediaPlaybackStatus.Playing;

    /// <summary>Segoe icon: pause while playing, play otherwise.</summary>
    public string PlayPauseGlyph => IsPlaying ? "" : "";

    /// <summary>What pressing the button would do (also its accessible name).</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public ICommand PlayPauseCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public ICommand RetryCommand { get; }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public ICommand DismissErrorCommand { get; }

    // ---- several players (FE-19) -------------------------------------------------------------------------------------

    /// <summary>How many media sessions are open.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>True when more than one player is open: the page offers a way to switch between them.</summary>
    public bool HasMultipleSessions => _mode == MediaPageMode.Session && _sessions.Count > 1;

    /// <summary>"1 of 2": which player is shown. Empty with a single player.</summary>
    public string SessionPositionText => HasMultipleSessions && SessionIndex() is var i && i >= 0 ? $"{i + 1} of {_sessions.Count}" : string.Empty;

    /// <summary>What the switch button does, in words (also its accessible name): names the player it would show next.</summary>
    public string SwitchLabel => HasMultipleSessions && NextSession() is { } next ? $"Switch to {MediaPageText.Source(next.AppName)}" : "Switch player";

    /// <summary>True when the player on show was picked by the user, so the page can offer to go back to the automatic choice.</summary>
    public bool ShowAutomatic => HasMultipleSessions && _isUserChoice;

    /// <summary>Shows the next open player and keeps that choice until it closes.</summary>
    public ICommand SwitchSessionCommand { get; }

    /// <summary>Gives the choice back to the page: the player Windows treats as current is shown again.</summary>
    public ICommand UseAutomaticCommand { get; }

    // ---- lifetime ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts listening to the tracker, then reads once. Never throws: a failed read is the Unavailable face with Retry.
    /// Call once, after the page is created.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_started || _disposed)
            return;
        _started = true;

        // Listen first so a change during the first read is not lost.
        _subscription = _tracker.Changes.Subscribe(new Listener(state => _post(() => Apply(state))));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await ReadAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the position along once a second while a session plays: refreshes at once, then every second, each tick reading the
    /// real time so a late tick corrects itself. Call <see cref="StopTicker"/> when the island closes. Idempotent.
    /// </summary>
    public void StartTicker()
    {
        if (_disposed || _tickerRunning)
            return;
        _tickerRunning = true;
        RefreshPosition();
        ScheduleTick();
    }

    public void StopTicker()
    {
        _tickerRunning = false;
        _timer?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tickerRunning = false;
        _timer?.Dispose();
        _timer = null;
        _lifetime.Cancel();
        _subscription?.Dispose();
        _subscription = null;
    }

    // Retry shows the placeholder again while it reads, unless a state arrived meanwhile.
    private Task RetryAsync(CancellationToken ct)
    {
        _post(() =>
        {
            if (!_hasState && !_disposed)
                Mode = MediaPageMode.Loading;
        });
        return ReadAsync(ct);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var result = await _tracker.InitializeAsync(ct).ConfigureAwait(false);
        if (result.IsCancelled)
            return;
        if (result.IsSuccess)
        {
            var state = result.Value;
            _post(() => Apply(state));
        }
        else
        {
            var code = result.Error!.Code;
            _post(() => ShowUnavailable(code));
        }
    }

    // ---- applying tracker state --------------------------------------------------------------------------------------

    private void Apply(ActiveMediaState state)
    {
        if (_disposed)
            return;

        _hasState = true;
        UnavailableText = null;
        var previous = _active;
        var previousId = previous?.Id;
        var session = state.Active;
        _active = session;
        _sessions = state.Sessions;
        _isUserChoice = state.IsUserChoice;
        _activeAt = _time.GetUtcNow();
        _seekAnchor = null; // the player's own report is the truth again

        // A position picked on other media, or on media that cannot be seeked, means nothing: drop the drag and the wait.
        if (session is null || previous is null || !SameMedia(previous, session) || !session.CanSeek)
            AbandonSeek();

        if (session is null)
        {
            Mode = MediaPageMode.NoSession;
            ClearSession();
        }
        else
        {
            Mode = MediaPageMode.Session;
            if (previousId != session.Id)
                ErrorMessage = null; // an error about another player is not about this one
            ShowSession(session);
        }

        NotifyCommandsChanged();
        NotifySessionChoice();
    }

    private void ShowSession(MediaSessionSnapshot session)
    {
        Title = MediaPageText.Title(session.Title);
        Artist = MediaPageText.Artist(session.Artist);
        Source = MediaPageText.Source(session.AppName);
        StatusText = MediaPageText.Status(session.Status);
        SourceLine = MediaPageText.SourceLine(Source, StatusText);
        Summary = MediaPageText.Summary(session);
        TimelineNote = MediaPageText.TimelineNote(session);
        ShowArtwork(session.Artwork);
        OnChanged(nameof(Active));
        OnChanged(nameof(IsPlaying));
        OnChanged(nameof(PlayPauseGlyph));
        OnChanged(nameof(PlayPauseLabel));
        OnChanged(nameof(HasTimeline));
        OnChanged(nameof(CanSeek));
        OnChanged(nameof(ShowReadOnlyBar));
        OnChanged(nameof(SeekSmallChange));
        OnChanged(nameof(SeekLargeChange));
        RefreshPosition();
        if (_tickerRunning)
            ScheduleTick();
    }

    private void ClearSession()
    {
        Title = Artist = Source = StatusText = SourceLine = Summary = string.Empty;
        TimelineNote = null;
        _artworkBytes = ReadOnlyMemory<byte>.Empty;
        Artwork = null;
        PositionText = DurationText = string.Empty;
        Progress = 0;
        OnChanged(nameof(Active));
        OnChanged(nameof(IsPlaying));
        OnChanged(nameof(PlayPauseGlyph));
        OnChanged(nameof(PlayPauseLabel));
        OnChanged(nameof(HasTimeline));
        OnChanged(nameof(CanSeek));
        OnChanged(nameof(ShowReadOnlyBar));
        OnChanged(nameof(TimelineLabel));
        SetSeekValueFromPlayer(0);
        _timer?.Cancel();
    }

    private void ShowUnavailable(ErrorCode code)
    {
        if (_disposed || _hasState)
            return; // a state that arrived meanwhile is newer than this failure

        UnavailableText = MediaPageText.Unavailable(code);
        Mode = MediaPageMode.Unavailable;
        NotifyCommandsChanged();
    }

    // The same bytes again (the usual case: a position or status change) keep the decoded image; new bytes are decoded once.
    private void ShowArtwork(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Span.SequenceEqual(_artworkBytes.Span) && (_artwork is not null || bytes.IsEmpty))
            return;

        _artworkBytes = bytes;
        Artwork = ArtworkDecoder.Decode(bytes);
    }

    // ---- position ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Where the media is now: the position the player last reported, moved forward by the time since that report while it
    /// plays, never past the end.
    /// </summary>
    internal TimeSpan? CurrentPosition()
    {
        if (_active is not { Duration: { } duration } || duration <= TimeSpan.Zero)
            return null;

        // After an accepted seek the position the player then reported (or the target) is the starting point.
        TimeSpan position;
        DateTimeOffset since;
        if (_seekAnchor is { } anchor)
        {
            position = anchor.Position;
            since = anchor.At;
        }
        else if (_active.Position is { } reported)
        {
            position = reported;
            since = _activeAt;
        }
        else
        {
            return null;
        }

        if (_active.Status == MediaPlaybackStatus.Playing)
        {
            var elapsed = _time.GetUtcNow() - since;
            if (elapsed > TimeSpan.Zero)
                position += elapsed;
        }

        return position > duration ? duration : position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void RefreshPosition()
    {
        // The user's hand (or a seek waiting for its answer) owns the display: nothing the player says moves it meanwhile.
        if ((_isScrubbing && !_ignoreUntilRelease) || _seeking || _userMoved)
            return;

        if (CurrentPosition() is { } position && _active?.Duration is { } duration)
        {
            PositionText = MediaPageText.FormatTime(position);
            DurationText = MediaPageText.FormatTime(duration);
            Progress = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0.0, 1.0) : 0.0;
        }
        else
        {
            PositionText = DurationText = string.Empty;
            Progress = 0;
        }

        SetSeekValueFromPlayer(Progress);
        OnChanged(nameof(TimelineLabel));
    }

    // The page's own write to the slider value: never mistaken for the user (the setter ignores an echo of the same value).
    private void SetSeekValueFromPlayer(double value)
    {
        if (Math.Abs(value - _seekValue) < 1e-9)
            return;
        _seekValue = value;
        OnChanged(nameof(SeekValue));
    }

    private double StepFraction(TimeSpan step) =>
        _active?.Duration is { } d && d > TimeSpan.Zero ? Math.Clamp(step / d, 0.0, 1.0) : 0.0;

    private static bool SameMedia(MediaSessionSnapshot a, MediaSessionSnapshot b) =>
        a.Id == b.Id && a.Title == b.Title && a.Artist == b.Artist && a.Duration == b.Duration;

    // ---- seeking -----------------------------------------------------------------------------------------------------

    // While the thumb moves, the time shows where the user is dragging to. Nothing is sent.
    private void ShowDragPosition()
    {
        if (_active?.Duration is not { } duration || duration <= TimeSpan.Zero)
            return;
        PositionText = MediaPageText.FormatTime(SeekMedia.FromFraction(_seekValue, duration));
        OnChanged(nameof(TimelineLabel));
    }

    private void CommitIfReleased()
    {
        if (!_disposed && !_isScrubbing && _userMoved)
            CommitSeek();
    }

    // Drops a drag or a wait that no longer applies. A held thumb stays ignored until it is released.
    private void AbandonSeek()
    {
        _seekGeneration++;
        _seeking = false;
        _userMoved = false;
        if (_isScrubbing)
            _ignoreUntilRelease = true;
    }

    // The thumb was released: one seek goes to the session that is active now, and the page keeps the released position.
    private void CommitSeek()
    {
        _userMoved = false;
        if (_disposed)
            return;

        var session = _active;
        if (session is null || !session.CanSeek || session.Duration is not { } duration)
        {
            RefreshPosition();
            return;
        }

        var target = SeekMedia.FromFraction(_seekValue, duration);
        var generation = ++_seekGeneration;
        _seeking = true;
        PositionText = MediaPageText.FormatTime(target);
        OnChanged(nameof(TimelineLabel));
        ErrorMessage = null;

        // One short-lived command per seek: it keeps the async entry point guarded and lets a later seek queue behind this one
        // inside SeekMedia instead of being dropped here.
        new AsyncRelayCommand(ct => SeekAsync(session, target, generation, ct), null, OnFault, _lifetime.Token).Execute(null);
    }

    private async Task SeekAsync(MediaSessionSnapshot session, TimeSpan target, int generation, CancellationToken ct)
    {
        Result<SeekOutcome> result;
        try
        {
            result = await _seek.ExecuteAsync(session, target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            OnFault(ex);
            result = Result.Failure<SeekOutcome>(new OperationError(ErrorCode.Unexpected, "The seek failed."));
        }

        if (result.IsCancelled)
            return;
        _post(() => FinishSeek(generation, result));
    }

    private void FinishSeek(int generation, Result<SeekOutcome> result)
    {
        // A newer drag, a seek, or other media owns the display now: this answer is about the past.
        if (_disposed || generation != _seekGeneration)
            return;

        _seeking = false;
        if (result.IsSuccess)
        {
            // What the player reports after the seek is the truth; when it could not be read the accepted target stands.
            var outcome = result.Value;
            _seekAnchor = (outcome.Position ?? outcome.Target, _time.GetUtcNow());
        }
        else
        {
            // Back to the real position, and say why in words.
            _seekAnchor = null;
            ErrorMessage = MediaPageText.SeekFailed(result.Error!.Code);
        }

        RefreshPosition();
        if (_tickerRunning)
            ScheduleTick();
    }

    private void ScheduleTick()
    {
        if (!_tickerRunning || _disposed)
            return;

        if (!IsPlaying || !HasTimeline)
        {
            _timer?.Cancel(); // nothing moves: no timer runs
            return;
        }

        // Created on first use, on the UI thread that owns the page.
        _timer ??= new DispatcherOneShotTimer();
        _timer.Schedule(TimeSpan.FromSeconds(1), OnTick);
    }

    private void OnTick()
    {
        if (!_tickerRunning || _disposed)
            return;

        RefreshPosition();
        ScheduleTick();
    }

    // ---- transport ---------------------------------------------------------------------------------------------------

    // ---- several players ---------------------------------------------------------------------------------------------

    // The same order the tracker's policy uses, so "next" is predictable and never depends on how Windows listed them.
    private List<MediaSessionSnapshot> OrderedSessions() => [.. _sessions.OrderBy(s => s.Id, StringComparer.Ordinal)];

    private int SessionIndex() => _active is null ? -1 : OrderedSessions().FindIndex(s => s.Id == _active.Id);

    private MediaSessionSnapshot? NextSession()
    {
        var ordered = OrderedSessions();
        if (ordered.Count < 2)
            return null;
        var index = SessionIndex();
        return ordered[(index + 1) % ordered.Count];
    }

    // The pick goes through the tracker; the page changes only when the tracker publishes the new active session, so artwork,
    // text, buttons and the timeline all switch together and nothing of the old player is left behind.
    private void SwitchSession()
    {
        if (NextSession() is not { } next)
            return;

        ErrorMessage = null;
        var result = _tracker.Select(next.Id);
        if (result.IsFailure)
            ErrorMessage = MediaPageText.CommandFailed(result.Error!.Code);
    }

    private void UseAutomatic() => _tracker.ClearSelection();

    private void NotifySessionChoice()
    {
        OnChanged(nameof(SessionCount));
        OnChanged(nameof(HasMultipleSessions));
        OnChanged(nameof(SessionPositionText));
        OnChanged(nameof(SwitchLabel));
        OnChanged(nameof(ShowAutomatic));
        ((RelayCommand)SwitchSessionCommand).NotifyCanExecuteChanged();
        ((RelayCommand)UseAutomaticCommand).NotifyCanExecuteChanged();
    }

    private bool CanSend(MediaCommand command) => _active is { } session && MediaTransport.IsSupported(session, command);

    private void NotifyCommandsChanged()
    {
        ((AsyncRelayCommand)PlayPauseCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)NextCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)PreviousCommand).NotifyCanExecuteChanged();
    }

    // The command goes to the session that was active when the button was pressed. The page changes only when the player
    // reports a new state through the tracker; a refusal is said in words and nothing else moves.
    private async Task SendAsync(MediaCommand command, CancellationToken ct)
    {
        var session = _active;
        if (session is null)
            return;

        _post(() => ErrorMessage = null);
        var result = await _transport.ExecuteAsync(session, command, ct).ConfigureAwait(false);
        if (result.IsFailure && !result.IsCancelled)
        {
            var code = result.Error!.Code;
            // An answer about a player that is no longer on show is not news about the one that is.
            _post(() =>
            {
                if (_active?.Id == session.Id)
                    ErrorMessage = MediaPageText.CommandFailed(code);
            });
        }
    }

    private void OnFault(Exception ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "mediapage.fault", Component, "Command", ErrorCode.Unexpected, ExceptionType: ex.GetType()));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class Listener(Action<ActiveMediaState> onNext) : IObserver<ActiveMediaState>
    {
        public void OnNext(ActiveMediaState value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}

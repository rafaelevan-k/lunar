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
/// while it is closed. The seek slider belongs to FE-18, the session selector to FE-19.
///
/// Port callbacks arrive on other threads and are marshalled through <c>post</c> before any property changes. Use on the UI thread.
/// </summary>
public sealed class MediaPageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "MediaPage";

    private readonly MediaSessionTracker _tracker;
    private readonly MediaTransport _transport;
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

    public MediaPageViewModel(MediaSessionTracker tracker, MediaTransport transport, Action<Action> post, IDiagnosticLog? log = null)
        : this(tracker, transport, post, log, TimeProvider.System, timer: null)
    {
    }

    internal MediaPageViewModel(
        MediaSessionTracker tracker,
        MediaTransport transport,
        Action<Action> post,
        IDiagnosticLog? log,
        TimeProvider time,
        IOneShotTimer? timer)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(time);
        _tracker = tracker;
        _transport = transport;
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
        var previousId = _active?.Id;
        var session = state.Active;
        _active = session;
        _activeAt = _time.GetUtcNow();

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
        OnChanged(nameof(TimelineLabel));
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
        if (_active is not { Duration: { } duration, Position: { } position } || duration <= TimeSpan.Zero)
            return null;

        if (_active.Status == MediaPlaybackStatus.Playing)
        {
            var elapsed = _time.GetUtcNow() - _activeAt;
            if (elapsed > TimeSpan.Zero)
                position += elapsed;
        }

        return position > duration ? duration : position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void RefreshPosition()
    {
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

        OnChanged(nameof(TimelineLabel));
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
            _post(() => ErrorMessage = MediaPageText.CommandFailed(code));
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

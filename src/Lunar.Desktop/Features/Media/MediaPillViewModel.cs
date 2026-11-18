using System.ComponentModel;
using System.Windows.Media;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Settings;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The collapsed island while music or video plays (ADR-0026, FE-39). Follows the session the Media page follows: it shows while that session plays, and
/// 3 seconds after a pause, a stop or the session going away it hands the island back to the eyes, so a track change does not flicker. The Settings
/// switch turns it off at once. The sound bars come from <see cref="IPlaybackLevelSource"/>, which runs only while the pill is actually on screen
/// (<see cref="SetVisible"/>). Use on the UI thread.
/// </summary>
public sealed class MediaPillViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long the media pill stays after playing stops.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private const float Decay = 0.72f;

    private readonly MediaPageViewModel _media;
    private readonly IPlaybackLevelSource _levels;
    private readonly SettingsService _settings;
    private readonly IOneShotTimer _grace;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly float[] _shown = new float[PlaybackLevelBands.Count];
    private IReadOnlyList<float> _bands = PlaybackLevels.Flat.Bands;
    private bool _showsInIsland;
    private bool _visible;
    private bool _enabled;
    private string? _trackKey;
    private int _frameQueued;
    private bool _disposed;

    internal MediaPillViewModel(
        MediaPageViewModel media,
        IPlaybackLevelSource levels,
        SettingsService settings,
        IOneShotTimer grace,
        Action<Action> post,
        IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grace);
        ArgumentNullException.ThrowIfNull(post);
        _media = media;
        _levels = levels;
        _settings = settings;
        _grace = grace;
        _post = post;
        _log = log;
        _enabled = settings.Current.Media.ShowInPill;
        _media.PropertyChanged += OnMediaChanged;
        _settings.Changed += OnSettingsChanged;
        _levels.LevelsChanged += OnLevels;
        Follow();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A new track started while the pill shows: the screen reader hears it once.</summary>
    public event EventHandler? TrackStarted;

    /// <summary>True while the collapsed island should show the media pill (before priority: a recording or focus session still wins).</summary>
    public bool ShowsInIsland => _showsInIsland;

    /// <summary>True while the session is playing; false during the grace time after a pause.</summary>
    public bool IsPlaying => _media.IsPlaying;

    public ImageSource? Artwork => _media.Artwork;

    public bool HasArtwork => _media.HasArtwork;

    /// <summary>The six bars, smoothed so they fall gently and rise at once. Flat while not playing.</summary>
    public IReadOnlyList<float> Bands => _bands;

    /// <summary>What a screen reader calls the pill: "Playing: title by artist", or the app when nothing better is known.</summary>
    public string AccessibleName
    {
        get
        {
            var title = _media.Title;
            var artist = _media.Artist;
            if (string.IsNullOrWhiteSpace(title))
                return Strings.Format("MediaPill.PlayingFrom", _media.Source);
            return string.IsNullOrWhiteSpace(artist)
                ? Strings.Format("MediaPill.PlayingTitle", title)
                : Strings.Format("MediaPill.PlayingTitleArtist", title, artist);
        }
    }

    /// <summary>
    /// The island says whether the pill is on screen now (collapsed and chosen over the eyes). The meter runs only while it is, and stops on expand,
    /// on a pause and when another activity takes over.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible)
            return;
        _visible = visible;
        ApplyMeter();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _media.PropertyChanged -= OnMediaChanged;
        _settings.Changed -= OnSettingsChanged;
        _levels.LevelsChanged -= OnLevels;
        _grace.Cancel();
        if (_levels.IsRunning)
            _levels.Stop();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        switch (e.PropertyName)
        {
            case nameof(MediaPageViewModel.IsPlaying) or nameof(MediaPageViewModel.Mode) or nameof(MediaPageViewModel.Active):
                Follow();
                break;
            case nameof(MediaPageViewModel.Artwork) or nameof(MediaPageViewModel.HasArtwork):
                Raise(nameof(Artwork));
                Raise(nameof(HasArtwork));
                break;
            case nameof(MediaPageViewModel.Title) or nameof(MediaPageViewModel.Artist):
                Raise(nameof(AccessibleName));
                Follow();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        _post(() =>
        {
            if (_disposed || e.Current.Media.ShowInPill == _enabled)
                return;
            _enabled = e.Current.Media.ShowInPill;
            Follow();
        });

    private void Follow()
    {
        var playing = _enabled && _media.HasSession && _media.IsPlaying;
        if (playing)
        {
            _grace.Cancel();
            // The session's own values, not the page's texts: those change one by one while a new track is shown.
            var active = _media.Active;
            var key = active is null ? string.Empty : active.Id + "\u0001" + active.Title + "\u0001" + active.Artist;
            var wasShown = _showsInIsland;
            SetShows(true);
            if (!wasShown || !string.Equals(key, _trackKey, StringComparison.Ordinal))
            {
                _trackKey = key;
                TrackStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (!_enabled)
        {
            _grace.Cancel();
            SetShows(false);
        }
        else if (_showsInIsland)
        {
            _grace.Schedule(Grace, () =>
            {
                if (!_disposed && !(_media.HasSession && _media.IsPlaying))
                    SetShows(false);
            });
        }

        Raise(nameof(IsPlaying));
        ApplyMeter();
    }

    private void SetShows(bool shows)
    {
        if (_showsInIsland == shows)
            return;
        _showsInIsland = shows;
        if (!shows)
            _trackKey = null;
        Raise(nameof(ShowsInIsland));
        Raise(nameof(AccessibleName));
    }

    private void ApplyMeter()
    {
        var run = _visible && _showsInIsland && _media.IsPlaying;
        if (run && !_levels.IsRunning)
        {
            _ = StartMeterAsync();
        }
        else if (!run && _levels.IsRunning)
        {
            _levels.Stop();
            Flatten();
        }
        else if (!run)
        {
            Flatten();
        }
    }

    private async Task StartMeterAsync()
    {
        Result started;
        try
        {
            started = await _levels.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // A failed start keeps the bars still and low; nothing is invented (ADR-0026 §3). The adapter logs the reason.
        if (started.IsFailure)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "media-pill.meter-unavailable", "media-pill", "start", started.Error!.Code));
            return;
        }

        // The pill may have gone while the device was opening.
        if (_disposed || !(_visible && _showsInIsland && _media.IsPlaying))
            _levels.Stop();
    }

    private void OnLevels(PlaybackLevels levels)
    {
        // Raised about 30 times a second on the audio thread: keep only the newest and let the UI take it when it can.
        var bands = levels.Bands;
        lock (_shown)
        {
            for (var i = 0; i < _shown.Length; i++)
            {
                var value = i < bands.Count ? Math.Clamp(bands[i], 0f, 1f) : 0f;
                _shown[i] = Math.Max(value, _shown[i] * Decay);
                if (_shown[i] < 0.02f)
                    _shown[i] = 0f;
            }
        }

        if (Interlocked.Exchange(ref _frameQueued, 1) == 0)
        {
            _post(() =>
            {
                Interlocked.Exchange(ref _frameQueued, 0);
                if (_disposed)
                    return;
                lock (_shown)
                    _bands = [.. _shown];
                Raise(nameof(Bands));
            });
        }
    }

    private void Flatten()
    {
        lock (_shown)
            Array.Clear(_shown);
        if (_bands.Any(b => b > 0))
        {
            _bands = PlaybackLevels.Flat.Bands;
            Raise(nameof(Bands));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

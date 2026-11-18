using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Focus;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.Features.Pomodoro;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-39 (ADR-0026): the collapsed island while music or video plays. A scripted media service, a fake meter and a hand-fired grace timer; the real
// loopback meter is proven in BE-45, and the look on a real display with real music is the owner's manual check.
public sealed class MediaPillTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RecordingThenCountdownThenMediaThenEyes()
    {
        Assert.Equal(IslandActivity.Recording, IslandActivitySelector.Choose(true, true, true));
        Assert.Equal(IslandActivity.FocusCountdown, IslandActivitySelector.Choose(false, true, true));
        Assert.Equal(IslandActivity.MediaPlaying, IslandActivitySelector.Choose(false, false, true));
        Assert.Equal(IslandActivity.Eyes, IslandActivitySelector.Choose(false, false, false));
        Assert.Equal(IslandActivity.Eyes, IslandActivitySelector.Choose(false, false)); // the old two-way rule is unchanged
    }

    [Fact]
    public void ShowsWhilePlayingAndLeavesThreeSecondsAfterAPause()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        Assert.True(rig.Pill.ShowsInIsland);

        rig.Media.Set(S(status: MediaPlaybackStatus.Paused));
        Assert.True(rig.Pill.ShowsInIsland); // not at once: a track change or a short pause must not flicker
        Assert.Equal(MediaPillViewModel.Grace, rig.Timer.Pending);

        rig.Media.Set(S(status: MediaPlaybackStatus.Playing)); // back before the grace ran out
        Assert.Null(rig.Timer.Pending);
        Assert.True(rig.Pill.ShowsInIsland);

        rig.Media.Set(S(status: MediaPlaybackStatus.Stopped));
        rig.Timer.Fire();
        Assert.False(rig.Pill.ShowsInIsland);
    }

    [Fact]
    public void ASessionThatGoesAwayAlsoGivesTheIslandBack()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        rig.Media.Set();
        rig.Timer.Fire();
        Assert.False(rig.Pill.ShowsInIsland);
    }

    [Fact]
    public void NothingShowsWhilePausedFromTheStart()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Paused));
        Assert.False(rig.Pill.ShowsInIsland);
        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public async Task TheSwitchTurnsItOffAtOnceAndOnAgain()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        await rig.Settings.UpdateAsync(s => s with { Media = s.Media with { ShowInPill = false } }, Ct);
        Assert.False(rig.Pill.ShowsInIsland);
        Assert.Null(rig.Timer.Pending);

        await rig.Settings.UpdateAsync(s => s with { Media = s.Media with { ShowInPill = true } }, Ct);
        Assert.True(rig.Pill.ShowsInIsland);
    }

    [Fact]
    public void TheMeterRunsOnlyWhileThePillIsOnScreenAndPlaying()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        Assert.Equal(0, rig.Meter.Starts); // shown is not yet on screen

        rig.Pill.SetVisible(true);
        Assert.Equal(1, rig.Meter.Starts);
        Assert.True(rig.Meter.IsRunning);

        rig.Pill.SetVisible(false); // the island expanded or another activity took over
        Assert.False(rig.Meter.IsRunning);

        rig.Pill.SetVisible(true);
        Assert.True(rig.Meter.IsRunning);
        rig.Media.Set(S(status: MediaPlaybackStatus.Paused)); // paused: the bars lie flat, the meter stops, the pill stays for the grace
        Assert.False(rig.Meter.IsRunning);
        Assert.True(rig.Pill.ShowsInIsland);
        Assert.All(rig.Pill.Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void AFailedMeterKeepsTheBarsStillAndNothingIsInvented()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        rig.Meter.Fail = true;
        rig.Pill.SetVisible(true);
        Assert.False(rig.Meter.IsRunning);
        Assert.All(rig.Pill.Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void ReadingsRiseAtOnceAndFallGently()
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        rig.Pill.SetVisible(true);
        rig.Meter.Emit([1f, 0.5f, 0, 0, 0, 0]);
        Assert.Equal(1f, rig.Pill.Bands[0]);
        Assert.Equal(0.5f, rig.Pill.Bands[1]);

        rig.Meter.Emit([0, 0, 0, 0, 0, 0]);
        Assert.InRange(rig.Pill.Bands[0], 0.5f, 0.9f);
        for (var i = 0; i < 30; i++)
            rig.Meter.Emit([0, 0, 0, 0, 0, 0]);
        Assert.All(rig.Pill.Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void ANewTrackIsAnnouncedOnceAndTheNameSaysWhatPlays()
    {
        using var rig = new Rig();
        var tracks = 0;
        rig.Pill.TrackStarted += (_, _) => tracks++;
        rig.Started(S(status: MediaPlaybackStatus.Playing, title: "Song", artist: "Band"));
        Assert.Equal(1, tracks);
        rig.Media.Set(S(status: MediaPlaybackStatus.Playing, title: "Song", artist: "Band", positionSeconds: 40));
        Assert.Equal(1, tracks); // the same track moving along is not news

        rig.Media.Set(S(status: MediaPlaybackStatus.Playing, title: "Other", artist: "Band"));
        Assert.Equal(2, tracks);
        Assert.Contains("Other", rig.Pill.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Band", rig.Pill.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposingStopsTheMeterAndLetsGo()
    {
        var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        rig.Pill.SetVisible(true);
        rig.Dispose();
        Assert.False(rig.Meter.IsRunning);
        Assert.Equal(0, rig.Meter.Subscribers);
    }

    // ---- colour ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBarsTakeTheArtworksLivelyColour()
    {
        var pixels = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            // Half grey, half a strong red: the grey does not count.
            var red = i % 2 == 0;
            pixels[(i * 4) + 0] = (byte)(red ? 30 : 128);
            pixels[(i * 4) + 1] = (byte)(red ? 30 : 128);
            pixels[(i * 4) + 2] = (byte)(red ? 220 : 128);
            pixels[(i * 4) + 3] = 255;
        }

        var tint = ArtworkTint.Dominant(pixels);
        Assert.NotNull(tint);
        Assert.Equal(220, tint.Value.R);
        Assert.Null(ArtworkTint.Dominant(Enumerable.Repeat((byte)128, 64).ToArray())); // grey artwork: white bars
    }

    [Theory]
    [InlineData("#0B0C10")] // dark palette
    [InlineData("#F6F7FB")] // light palette
    public void TheTintIsLiftedToThreeToOneAgainstThePill(string background)
    {
        var pill = (Color)ColorConverter.ConvertFromString(background);
        foreach (var tint in new[] { Color.FromRgb(20, 20, 120), Color.FromRgb(240, 240, 90), Color.FromRgb(200, 30, 30) })
            Assert.True(ArtworkTint.Contrast(ArtworkTint.Lift(tint, pill), pill) >= ArtworkTint.MinimumContrast);
    }

    [Fact]
    public void TheViewShowsTheArtworkColourAndOpensTheMediaPage() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, artwork: SolidPng(Color.FromRgb(30, 160, 60))));
        var expands = 0;
        var view = new MediaPillView { DataContext = rig.Pill, ExpandCommand = new RelayCommand(() => expands++) };
        view.Resources["Lunar.Color.Background"] = (Color)ColorConverter.ConvertFromString("#0B0C10");
        view.Resources["Lunar.Brush.TextPrimary"] = Brushes.White;
        var window = new Window { Content = view, Width = 300, Height = 100, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
        window.Show();
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            var brush = Assert.IsType<SolidColorBrush>(view.BarBrush);
            Assert.True(brush.Color.G > brush.Color.R && brush.Color.G > brush.Color.B, "green artwork gives green bars");
            view.RequestExpand();
            Assert.Equal(1, expands);
            Assert.Equal(rig.Pill.AccessibleName, System.Windows.Automation.AutomationProperties.GetName(view));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheIslandShowsTheMediaPillOnlyWhenNothingMoreImportantRuns() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var dispatcher = Dispatcher.CurrentDispatcher;
        Action<Action> post = work => work();
        var settings = new SettingsService(new RecordsPageTests.Repository());
        var time = new RecordsPageTests.ManualTime();
        var recording = new RecordingService(new RecordsPageTests.FakeEngine(), new RecordsPageTests.FakeCatalog(), new RecordsPageTests.FakeFiles(), settings, new RecordsPageTests.Clock(), null, time);
        using var records = new RecordsViewModel(recording, new RecordsPageTests.FakeFolders(), new RecordsPageTests.FakeRevealer(), new RecordsPageTests.FakeLauncher(), post, time);
        var focusService = new PomodoroService(new PomodoroPageTests.FakeFocus(), new PomodoroPageTests.Marker(), settings, new PomodoroPageTests.MovableClock(), null, time);
        using var focus = new PomodoroPageViewModel(focusService, new RecordsPageTests.FakeLauncher(), post, time);
        var island = new IslandViewModel();
        var navigation = new PageNavigationViewModel(["1", "2", "3", "4", "5", "6"]);
        var swiper = new SwiperView();
        swiper.Initialize(navigation, IslandShellView.CreatePlaceholderPages(navigation.Titles), true);
        using var shell = new IslandShellView(new EyesView(), swiper, island, true);
        var mediaView = new MediaPillView { DataContext = rig.Pill };
        using var activity = new IslandActivityController(island, shell, records, new RecordingPillView { DataContext = records }, focus,
            new FocusPillView { DataContext = focus }, rig.Pill, mediaView);

        Assert.Equal(IslandActivity.MediaPlaying, activity.Current);
        Assert.True(rig.Meter.IsRunning); // collapsed and chosen: on screen
        Assert.Equal(IslandLayout.IdleWidth, island.IdleWidth); // the normal pill

        var started = focusService.StartAsync(25, Ct);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!(started.IsCompleted && focus.IsActive) && DateTime.UtcNow < end)
        {
            dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.Equal(IslandActivity.FocusCountdown, activity.Current);
        Assert.False(rig.Meter.IsRunning); // not on screen: no meter

        var stopped = focusService.StopAsync();
        end = DateTime.UtcNow.AddSeconds(5);
        while (!(stopped.IsCompleted && !focus.IsActive) && DateTime.UtcNow < end)
        {
            dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.Equal(IslandActivity.MediaPlaying, activity.Current);
        Assert.True(rig.Meter.IsRunning);
    });

    [Fact]
    public void TheMediaPillFitsTheShorterNotch() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, artwork: SolidPng(Color.FromRgb(200, 60, 120))));
        var view = new MediaPillView { DataContext = rig.Pill, Width = Lunar.Desktop.Shell.IslandLayout.NotchMediaIdleWidth - 24, Height = Lunar.Desktop.Shell.IslandLayout.NotchIdleHeight };
        view.Resources["Lunar.Color.Background"] = (Color)ColorConverter.ConvertFromString("#0B0C10");
        view.Resources["Lunar.Brush.TextPrimary"] = Brushes.White;
        var host = new System.Windows.Controls.Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0C10")), Child = view };
        var window = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
        window.Show();
        try
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            window.UpdateLayout();
            var thumbnail = (FrameworkElement)view.FindName("Thumbnail");
            Assert.Equal(22, thumbnail.ActualHeight, 3); // 32 DIP high, with room above and below
            var at = thumbnail.TranslatePoint(new Point(0, 0), view);
            Assert.InRange(at.Y, 4, 6);
            var bars = (FrameworkElement)view.FindName("Bars");
            Assert.InRange(bars.ActualHeight, 12, 14.01);
            var dir = System.Environment.GetEnvironmentVariable("LUNAR_RENDER_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                rig.Pill.SetVisible(true);
                rig.Meter.Emit([0.9f, 0.6f, 0.8f, 0.4f, 0.7f, 0.3f]);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth * 3, (int)host.ActualHeight * 3, 288, 288, PixelFormats.Pbgra32);
                bmp.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var file = System.IO.File.Create(System.IO.Path.Combine(dir, "7-notch-media.png"));
                encoder.Save(file);
            }
        }
        finally
        {
            window.Close();
        }
    });

    // 2026-11-18, the owner: in the Pill the picture was too big and too close to the Pill's round left end.
    [Fact]
    public void InThePillThePictureIsSmallerAndClearOfTheRoundEnd() => RunOnSta(() =>
    {
        using var rig = new Rig().Started(S(status: MediaPlaybackStatus.Playing, artwork: SolidPng(Color.FromRgb(60, 140, 220))));
        var view = new MediaPillView { DataContext = rig.Pill, Width = Lunar.Desktop.Shell.IslandLayout.IdleWidth - 2, Height = Lunar.Desktop.Shell.IslandLayout.IdleHeight - 2 };
        view.Resources["Lunar.Color.Background"] = (Color)ColorConverter.ConvertFromString("#0B0C10");
        view.Resources["Lunar.Brush.TextPrimary"] = Brushes.White;
        var host = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B0C10")),
            CornerRadius = new CornerRadius(Lunar.Desktop.Shell.IslandLayout.IdleHeight / 2),
            Child = view
        };
        var window = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10000 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
            window.UpdateLayout();
            var thumbnail = (FrameworkElement)view.FindName("Thumbnail");
            Assert.Equal(24, thumbnail.ActualHeight, 3);
            var at = thumbnail.TranslatePoint(new Point(0, 0), view);
            Assert.True(at.X >= 14, $"the picture starts {at.X} DIP from the end");
            Assert.InRange(at.Y + 12, (view.ActualHeight / 2) - 1, (view.ActualHeight / 2) + 1); // centred
            var bars = (FrameworkElement)view.FindName("Bars");
            Assert.True(view.ActualWidth - bars.TranslatePoint(new Point(bars.ActualWidth, 0), view).X >= 14, "the bars keep the same distance from the right end");
            var dir = System.Environment.GetEnvironmentVariable("LUNAR_RENDER_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                rig.Pill.SetVisible(true);
                rig.Meter.Emit([0.9f, 0.6f, 0.8f, 0.4f, 0.7f, 0.3f]);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth * 3, (int)host.ActualHeight * 3, 288, 288, PixelFormats.Pbgra32);
                bmp.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var file = System.IO.File.Create(System.IO.Path.Combine(dir, "11-pill-media.png"));
                encoder.Save(file);
            }
        }
        finally
        {
            window.Close();
        }
    });

    // ---- rig ------------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public ScriptedMedia Media { get; } = new();

        public FakeTimer Timer { get; } = new();

        public FakeMeter Meter { get; } = new();

        public SettingsService Settings { get; } = new(new RecordsPageTests.Repository());

        public MediaSessionTracker Tracker { get; }

        public MediaPageViewModel Model { get; }

        public MediaPillViewModel Pill { get; }

        public Rig()
        {
            Tracker = new MediaSessionTracker(Media);
            Model = new MediaPageViewModel(Tracker, new MediaTransport(Media), new SeekMedia(Media), work => work(), null, TimeProvider.System, new FakeTimer());
            Pill = new MediaPillViewModel(Model, Meter, Settings, Timer, work => work());
        }

        public Rig Started(params MediaSessionSnapshot[] sessions)
        {
            Media.Set(sessions);
            Model.InitializeAsync(Ct).GetAwaiter().GetResult();
            return this;
        }

        public void Dispose()
        {
            Pill.Dispose();
            Model.Dispose();
            Tracker.Dispose();
        }
    }

    internal sealed class FakeMeter : IPlaybackLevelSource
    {
        private Action<PlaybackLevels>? _handlers;

        public bool IsRunning { get; private set; }

        public int Starts { get; private set; }

        public bool Fail { get; set; }

        public int Subscribers => _handlers?.GetInvocationList().Length ?? 0;

        public event Action<PlaybackLevels>? LevelsChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public Task<Result> StartAsync(CancellationToken ct)
        {
            Starts++;
            if (Fail)
                return Task.FromResult(Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No playback device is available.")));
            IsRunning = true;
            return Task.FromResult(Result.Success());
        }

        public void Stop()
        {
            IsRunning = false;
            _handlers?.Invoke(PlaybackLevels.Flat);
        }

        public void Emit(float[] bands) => _handlers?.Invoke(new PlaybackLevels(bands));

        public void Dispose() => Stop();
    }

    private sealed class ScriptedMedia : IMediaService
    {
        private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();
        private List<MediaSessionSnapshot> _sessions = [];

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

        public void Set(params MediaSessionSnapshot[] sessions)
        {
            _sessions = [.. sessions];
            _changes.Publish(_sessions.ToArray());
        }

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Success<IReadOnlyList<MediaSessionSnapshot>>(_sessions.ToArray()));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTimer : IOneShotTimer
    {
        private Action? _callback;

        public TimeSpan? Pending { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            Pending = delay;
            _callback = callback;
        }

        public void Cancel()
        {
            Pending = null;
            _callback = null;
        }

        public void Fire()
        {
            var callback = _callback;
            Pending = null;
            _callback = null;
            callback?.Invoke();
        }

        public void Dispose() => Cancel();
    }

    private static MediaSessionSnapshot S(
        string? title = "Song",
        string? artist = "Artist",
        MediaPlaybackStatus status = MediaPlaybackStatus.Paused,
        int positionSeconds = 30,
        byte[]? artwork = null) =>
        new("a", "Player", title, artist, status, TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(200),
            new MediaCapabilities(true, true, true, true), true, artwork ?? []);

    private static byte[] SolidPng(Color color)
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF media pill check failed.", failure);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// The real Media page with the real theme and control styles on an STA thread, bound to a real view model over fake media.
// Windows that are shown are closed again and do not take focus.
public sealed class MediaPageTests
{
    // The area a page gets inside the expanded island: minimum island size minus the eyes strip and the page dots.
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private sealed class Rig(Window window, MediaPage page, MediaPageViewModel model, FakeMediaService media, MediaSessionTracker tracker) : IDisposable
    {
        public Window Window { get; } = window;

        public MediaPage Page { get; } = page;

        public MediaPageViewModel Model { get; } = model;

        public FakeMediaService Media { get; } = media;

        public void Dispose()
        {
            Model.Dispose();
            tracker.Dispose();
            Window.Close();
        }
    }

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static Rig Create(string colors = "Colors.Dark.xaml", bool start = true, params MediaSessionSnapshot[] sessions)
    {
        var media = new FakeMediaService();
        media.SetSessions(sessions);
        var tracker = new MediaSessionTracker(media);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var model = new MediaPageViewModel(tracker, new MediaTransport(media), new SeekMedia(media),
            work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); });
        var page = new MediaPage { DataContext = model };
        var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(Load("Themes", colors));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        window.UpdateLayout();

        var rig = new Rig(window, page, model, media, tracker);
        if (start)
        {
            Pump(model.InitializeAsync());
            Settle(window);
        }

        return rig;
    }

    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "The task did not finish in time.");
        task.GetAwaiter().GetResult();
    }

    private static void Settle(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        window.UpdateLayout();
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
            throw new InvalidOperationException("WPF media page check failed.", failure);
    }

    private static T Named<T>(Rig rig, string name)
        where T : class => (T)rig.Page.FindName(name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static MediaSessionSnapshot S(
        string id = "a",
        string? title = "Song",
        string? artist = "Artist",
        string? app = "Player",
        MediaPlaybackStatus status = MediaPlaybackStatus.Paused,
        int? positionSeconds = 30,
        int? durationSeconds = 200,
        bool seek = true,
        bool next = true,
        byte[]? artwork = null) =>
        new(id, app, title, artist, status,
            positionSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
            durationSeconds is { } d ? TimeSpan.FromSeconds(d) : null,
            new MediaCapabilities(CanPlayPause: true, CanNext: next, CanPrevious: true, CanSeek: seek), isCurrent: true, artwork ?? []);

    private static byte[] Png()
    {
        var pixels = new byte[4 * 4 * 4];
        Array.Fill(pixels, (byte)200);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- faces -------------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeAnythingIsReadOnlyThePlaceholderShows() => RunOnSta(() =>
    {
        using var rig = Create(start: false);

        Assert.Equal(Visibility.Visible, Named<StackPanel>(rig, "LoadingFace").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig, "NoSessionFace").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Grid>(rig, "SessionFace").Visibility);
        Assert.All(Descendants<SkeletonBlock>(rig.Page), block => Assert.True(block.IsVisible));
        Assert.DoesNotContain(Descendants<IconButton>(rig.Page), b => b.IsVisible);
    });

    [Fact]
    public void WithNoSessionThePageExplainsIt() => RunOnSta(() =>
    {
        using var rig = Create();

        Assert.Equal(Visibility.Visible, Named<StackPanel>(rig, "NoSessionFace").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Grid>(rig, "SessionFace").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig, "LoadingFace").Visibility);
        Assert.Equal("Nothing is playing", Named<TextBlock>(rig, "NoSessionTitleText").Text);
        Assert.Contains("Play music or a video", Named<TextBlock>(rig, "NoSessionHintText").Text, StringComparison.Ordinal);
        Assert.Equal("Nothing is playing", AutomationProperties.GetName(Named<StackPanel>(rig, "NoSessionFace")));
        Assert.DoesNotContain(Descendants<IconButton>(rig.Page), b => b.IsVisible);
    });

    [Fact]
    public void ASessionShowsArtworkTitleArtistSourceTimelineAndTransport() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(status: MediaPlaybackStatus.Playing, artwork: Png()));

        Assert.Equal(Visibility.Visible, Named<Grid>(rig, "SessionFace").Visibility);
        Assert.Equal("Song", Named<TextBlock>(rig, "TitleText").Text);
        Assert.Equal("Artist", Named<TextBlock>(rig, "ArtistText").Text);
        Assert.Equal("Player · Playing", Named<TextBlock>(rig, "SourceText").Text);
        Assert.Equal("00:30", Named<TextBlock>(rig, "PositionTextBlock").Text);
        Assert.Equal("03:20", Named<TextBlock>(rig, "DurationTextBlock").Text);
        Assert.Equal(Visibility.Visible, Named<Image>(rig, "ArtworkImage").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<TextBlock>(rig, "ArtworkPlaceholder").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<TextBlock>(rig, "TimelineNoteText").Visibility);
        Assert.Equal(["Previous", "Pause", "Next"], new[] { "PreviousButton", "PlayPauseButton", "NextButton" }.Select(n => AutomationProperties.GetName(Named<IconButton>(rig, n))));
        Assert.All(new[] { "PreviousButton", "PlayPauseButton", "NextButton" }, n => Assert.True(Named<IconButton>(rig, n).IsEnabled));
    });

    [Fact]
    public void MissingArtworkShowsAPlaceholderInTheSameSquare() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(artwork: []));
        var frame = Named<Border>(rig, "ArtworkFrame");

        Assert.Equal(Visibility.Visible, Named<TextBlock>(rig, "ArtworkPlaceholder").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Image>(rig, "ArtworkImage").Visibility);
        Assert.Equal(72, frame.ActualWidth);
        Assert.Equal(72, frame.ActualHeight);
    });

    [Fact]
    public void ArtworkThatCannotBeDecodedLooksLikeMissingArtwork() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(artwork: [9, 9, 9, 9]));

        Assert.Equal(Visibility.Visible, Named<TextBlock>(rig, "ArtworkPlaceholder").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Image>(rig, "ArtworkImage").Visibility);
    });

    [Fact]
    public void LongTextIsCutWithAnEllipsisOnOneLineAndShownInFullInItsTooltip() => RunOnSta(() =>
    {
        var title = new string('T', 400);
        var artist = string.Join(' ', Enumerable.Repeat("Very Long Artist Name", 30));
        var app = new string('A', 300);
        using var rig = Create(sessions: S(title: title, artist: artist, app: app, durationSeconds: 7300, positionSeconds: 3725));

        var texts = new[] { "TitleText", "ArtistText", "SourceText" }.Select(n => Named<TextBlock>(rig, n)).ToList();
        Assert.All(texts, t =>
        {
            Assert.Equal(TextTrimming.CharacterEllipsis, t.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, t.TextWrapping);
            Assert.True(t.ActualWidth <= PageWidth - 80 - 72 - 10 + 0.5, $"{t.Name} is {t.ActualWidth} wide");
            Assert.True(t.ActualHeight < 30, $"{t.Name} is {t.ActualHeight} high: more than one line");
        });
        Assert.Equal(title, Named<TextBlock>(rig, "TitleText").ToolTip);
        Assert.Equal(artist, Named<TextBlock>(rig, "ArtistText").ToolTip);
        Assert.Equal("01:02:05", Named<TextBlock>(rig, "PositionTextBlock").Text);
        Assert.True(Named<Grid>(rig, "SessionFace").ActualWidth <= PageWidth - 80 + 0.5);
    });

    [Fact]
    public void ASessionFitsTheAreaAPageGets() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(durationSeconds: 7300, positionSeconds: 3725));

        var face = Named<Grid>(rig, "SessionFace");
        Assert.True(face.ActualHeight <= PageHeight - 4, $"The session face is {face.ActualHeight} high; the area is {PageHeight - 4}.");
        Assert.True(Named<IconButton>(rig, "NextButton").TranslatePoint(new Point(0, 0), rig.Page).Y + Named<IconButton>(rig, "NextButton").ActualHeight <= PageHeight);
    });

    [Fact]
    public void ALiveStreamShowsANoteInsteadOfTimes() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(durationSeconds: null, positionSeconds: null));

        Assert.Equal(Visibility.Collapsed, Named<Grid>(rig, "Timeline").Visibility);
        var note = Named<TextBlock>(rig, "TimelineNoteText");
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Equal("Live or no timeline", note.Text);
    });

    // FE-18: a seekable timeline shows the slider (named, with the time in words); one that cannot be seeked shows the read-only bar.
    [Fact]
    public void ASeekableTimelineShowsASliderAndNotTheReadOnlyBar() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());

        var slider = Named<Slider>(rig, "SeekSlider");
        Assert.Equal(Visibility.Visible, slider.Visibility);
        Assert.Equal(Visibility.Collapsed, Named<ProgressBar>(rig, "TimelineBar").Visibility);
        Assert.Equal("Seek", AutomationProperties.GetName(slider));
        Assert.Equal("00:30 of 03:20", AutomationProperties.GetHelpText(slider));
        Assert.Equal(0.15, slider.Value, 3);
        Assert.True(slider.IsEnabled);
    });

    // FE-19: the player switcher shows only with several players, and fits beside the transport buttons.
    [Fact]
    public void WithOnePlayerThereIsNoSwitcher() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());

        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(rig, "SessionSwitcher").Visibility);
    });

    [Fact]
    public void WithSeveralPlayersTheSwitcherShowsAndFitsTheArea() => RunOnSta(() =>
    {
        using var rig = Create(sessions: [S(id: "a", app: "Spotify"), S(id: "b", app: "Browser")]);

        var switcher = Named<StackPanel>(rig, "SessionSwitcher");
        Assert.Equal(Visibility.Visible, switcher.Visibility);
        Assert.Equal("1 of 2", Named<TextBlock>(rig, "SessionPositionTextBlock").Text);
        Assert.Equal("Switch to Browser", AutomationProperties.GetName(Named<IconButton>(rig, "SwitchButton")));
        Assert.Equal(Visibility.Collapsed, Named<IconButton>(rig, "AutomaticButton").Visibility);

        // After a pick the automatic button appears; with it the transport row still fits the width the page gets.
        rig.Model.SwitchSessionCommand.Execute(null);
        rig.Window.UpdateLayout();
        var last = Named<IconButton>(rig, "AutomaticButton");
        Assert.Equal(Visibility.Visible, last.Visibility);
        var right = last.TranslatePoint(new Point(last.ActualWidth, 0), rig.Page).X;
        Assert.True(right <= PageWidth - 40 + 0.5, $"The switcher ends at {right}; the page allows {PageWidth - 40}.");
        Assert.True(Named<Grid>(rig, "SessionFace").ActualHeight <= PageHeight - 4);
    });

    [Fact]
    public void ATimelineThatCannotBeSeekedHasNoSlider() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(seek: false));

        Assert.Equal(Visibility.Collapsed, Named<Slider>(rig, "SeekSlider").Visibility);
        Assert.Equal(Visibility.Visible, Named<ProgressBar>(rig, "TimelineBar").Visibility);
    });

    [Fact]
    public void MovingTheSliderShowsTheDragTimeAndSendsNothingUntilTheDragEnds() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());
        var slider = Named<Slider>(rig, "SeekSlider");

        // The slider's drag signal reaches the view model through the real binding.
        Behaviors.SliderInteraction.SetIsAdjusting(slider, true);
        slider.Value = 0.5;

        Assert.True(rig.Model.IsScrubbing);
        Assert.Equal("01:40", Named<TextBlock>(rig, "PositionTextBlock").Text);
        Assert.Equal(0.5, rig.Model.SeekValue, 6);
    });

    [Fact]
    public void ATimelineThatCannotBeSeekedShowsTimesAndANote() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(seek: false));

        Assert.Equal(Visibility.Visible, Named<Grid>(rig, "Timeline").Visibility);
        Assert.Equal("This app doesn't allow seeking", Named<TextBlock>(rig, "TimelineNoteText").Text);
        Assert.Equal("00:30 of 03:20", AutomationProperties.GetHelpText(Named<Grid>(rig, "Timeline")));
    });

    [Fact]
    public void AButtonTheCurrentPlayerDoesNotOfferIsDisabled() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(next: false));

        Assert.False(Named<IconButton>(rig, "NextButton").IsEnabled);
        Assert.True(Named<IconButton>(rig, "PreviousButton").IsEnabled);
        Assert.True(Named<IconButton>(rig, "PlayPauseButton").IsEnabled);
    });

    [Fact]
    public void WhenThePlayerClosesThePageGoesBackToTheExplanation() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());

        rig.Media.SetSessions();
        Settle(rig.Window);

        Assert.Equal(Visibility.Visible, Named<StackPanel>(rig, "NoSessionFace").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<Grid>(rig, "SessionFace").Visibility);
    });

    [Fact]
    public void ThePlayPauseButtonChangesItsNameWithTheState() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(status: MediaPlaybackStatus.Paused));
        Assert.Equal("Play", AutomationProperties.GetName(Named<IconButton>(rig, "PlayPauseButton")));

        rig.Media.SetSessions(S(status: MediaPlaybackStatus.Playing));
        Settle(rig.Window);

        Assert.Equal("Pause", AutomationProperties.GetName(Named<IconButton>(rig, "PlayPauseButton")));
    });

    [Fact]
    public void PressingAButtonSendsTheCommandToThePlayer() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());

        rig.Window.Dispatcher.Invoke(() => Named<IconButton>(rig, "NextButton").Command.Execute(null));
        var end = DateTime.UtcNow.AddSeconds(3);
        while (rig.Media.Commands.Count == 0 && DateTime.UtcNow < end)
        {
            rig.Window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(5);
        }

        Assert.Equal(["next"], rig.Media.Commands);
    });

    [Fact]
    public void AFailedReadShowsItsReasonAndARetryButton() => RunOnSta(() =>
    {
        // A tracker over a service that cannot be read.
        var media = new UnreadableMedia();
        var tracker = new MediaSessionTracker(media);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var model = new MediaPageViewModel(tracker, new MediaTransport(media), new SeekMedia(media), work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); });
        var page = new MediaPage { DataContext = model };
        var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(Load("Themes", "Colors.Dark.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        try
        {
            Pump(model.InitializeAsync());
            Settle(window);

            Assert.Equal(Visibility.Visible, ((StackPanel)page.FindName("UnavailableFace")).Visibility);
            Assert.Equal("Media controls aren't available on this PC.", ((TextBlock)page.FindName("UnavailableText")).Text);
            var retry = (Button)page.FindName("RetryButton");
            Assert.True(retry.IsEnabled);
            Assert.Equal("Retry", retry.Content);
        }
        finally
        {
            model.Dispose();
            tracker.Dispose();
            window.Close();
        }
    });

    private sealed class UnreadableMedia : Lunar.Application.Abstractions.Media.IMediaService
    {
        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes { get; } = new TestObservable<IReadOnlyList<MediaSessionSnapshot>>();

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.Unsupported, "x")));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- themes and accessibility ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void EveryThemeRendersTheSessionInsideTheArea(string colors) => RunOnSta(() =>
    {
        using var rig = Create(colors, sessions: S(status: MediaPlaybackStatus.Playing, artwork: Png()));

        Assert.True(Named<Grid>(rig, "SessionFace").ActualHeight <= PageHeight - 4);
        Assert.All(new[] { "PreviousButton", "PlayPauseButton", "NextButton" }, n => Assert.True(Named<IconButton>(rig, n).ActualWidth > 0, $"{n}: {Named<IconButton>(rig, n).ActualWidth}x{Named<IconButton>(rig, n).ActualHeight} visible={Named<IconButton>(rig, n).IsVisible} face={Named<Grid>(rig, "SessionFace").ActualHeight}"));
        Assert.True(((SolidColorBrush)rig.Window.FindResource("Lunar.Brush.TextPrimary")).Color.A > 0);
    });

    [Fact]
    public void TheSessionIsDescribedAsOneSentenceAndEveryButtonIsATabStop() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S(status: MediaPlaybackStatus.Playing));

        var summary = Descendants<Grid>(Named<Grid>(rig, "SessionFace")).First(g => AutomationProperties.GetName(g).StartsWith("Song by", StringComparison.Ordinal));
        Assert.Equal("Song by Artist, from Player, Playing", AutomationProperties.GetName(summary));
        Assert.All(new[] { "PreviousButton", "PlayPauseButton", "NextButton" }, n =>
        {
            var button = Named<IconButton>(rig, n);
            Assert.True(button.IsTabStop);
            Assert.True(button.Focusable);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
        });
    });

    [Fact]
    public void ACommandErrorShowsInPlaceAndTakesNoRoomOtherwise() => RunOnSta(() =>
    {
        using var rig = Create(sessions: S());
        Assert.Equal(Visibility.Collapsed, Named<InlineError>(rig, "PageError").Visibility);
        Assert.Equal(0, Named<InlineError>(rig, "PageError").ActualHeight);
    });

    // FE-24: names, tab stops and tab order of the real page, in English and in Indonesian (the longer words must fit and be named too).
    [Theory]
    [InlineData("en")]
    [InlineData("id")]
    public void TheMediaPageIsNamedAndReachableByKeyboard(string language) => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use(language);
        try
        {
            using var rig = Create(sessions: [S(), S(id: "b", app: "Other")]);
            Lunar.Desktop.UITests.Accessibility.AutomationAudit.AssertAccessible(rig.Window);
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });

    // FE-24: Indonesian words are longer; the page must still fit the island (and nothing may be cut off).
    [Fact]
    public void TheMediaPageStillFitsTheIslandInIndonesian() => RunOnSta(() =>
    {
        Lunar.Desktop.Resources.Strings.Use("id");
        try
        {
            using var rig = Create(sessions: [S(durationSeconds: 7300, positionSeconds: 3725), S(id: "b", app: "Other")]);
            var face = Named<Grid>(rig, "SessionFace");
            Assert.True(face.ActualHeight <= PageHeight - 4, $"The session face is {face.ActualHeight} high; the area is {PageHeight - 4}.");
            Assert.True(rig.Page.ActualWidth <= PageWidth + 0.5);
        }
        finally
        {
            Lunar.Desktop.Resources.Strings.Use("en");
        }
    });
}

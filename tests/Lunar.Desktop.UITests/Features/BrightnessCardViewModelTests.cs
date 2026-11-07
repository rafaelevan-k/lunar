using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.System.UseCases;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-32, ADR-0021: the brightness card's logic over a brightness port in memory. No WPF here; timers are fired by hand.
public sealed class BrightnessCardViewModelTests
{
    private const string Display = @"\\.\DISPLAY1";

    private sealed class Launcher : ISystemSettingsLauncher
    {
        public List<SettingsPage> Opened { get; } = [];

        public Result Answer { get; set; } = Result.Success();

        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct)
        {
            Opened.Add(page);
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Action<FakeBrightnessService>? arrange = null)
        {
            arrange?.Invoke(Service);
            Writer = new BrightnessWriter(Service, interval: TimeSpan.FromMilliseconds(1));
            Card = new BrightnessCardViewModel(Service, Writer, () => DisplayId, Launcher, work => work(), Poll, Time, Log);
        }

        public FakeBrightnessService Service { get; } = new();

        public BrightnessWriter Writer { get; }

        public Launcher Launcher { get; } = new();

        public ManualTimer Poll { get; } = new();

        public ManualTime Time { get; } = new();

        public RecordingLog Log { get; } = new();

        public string? DisplayId { get; set; } = Display;

        public BrightnessCardViewModel Card { get; }

        public async Task StartAsync()
        {
            await Card.InitializeAsync(TestContext.Current.CancellationToken);
            await Settle();
        }

        /// <summary>Lets the writer and the reads it started finish.</summary>
        public static async Task Settle() => await Task.Delay(60, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Card.Dispose();
            Writer.Dispose();
        }
    }

    [Fact]
    public async Task ASupportedDisplayShowsTheSliderAtItsValueAndNoNote()
    {
        using var rig = new Rig();

        Assert.False(rig.Card.IsLoaded);
        await rig.StartAsync();

        Assert.True(rig.Card.IsLoaded);
        Assert.True(rig.Card.IsAvailable);
        Assert.Equal(40, rig.Card.Percent);
        Assert.Equal("40%", rig.Card.PercentText);
        Assert.Null(rig.Card.Note);
        Assert.False(rig.Card.ShowSettingsLink);
        Assert.Equal([Display], rig.Service.Reads);
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported, "Brightness can't be changed here for this display.")]
    [InlineData(CapabilityStatus.Unavailable, "No display was found for brightness.")]
    [InlineData(CapabilityStatus.PermissionDenied, "Windows didn't allow Lunar to read the brightness.")]
    public async Task ADisplayThatCannotBeChangedSaysWhyAndOffersDisplaySettings(CapabilityStatus capability, string note)
    {
        using var rig = new Rig(s => s.State = new BrightnessState(0, BrightnessSource.None, capability));
        await rig.StartAsync();

        Assert.False(rig.Card.IsAvailable);
        Assert.Equal(note, rig.Card.Note);
        Assert.True(rig.Card.ShowSettingsLink);
        rig.Card.OpenSettingsCommand.Execute(null);
        await Rig.Settle();
        Assert.Equal([SettingsPage.Display], rig.Launcher.Opened);
    }

    [Theory]
    [InlineData(ErrorCode.AccessDenied, "Windows didn't allow Lunar to read the brightness.")]
    [InlineData(ErrorCode.IoFailure, "The brightness couldn't be read.")]
    public async Task AFailedReadIsSaidInWordsWithDisplaySettings(ErrorCode code, string note)
    {
        using var rig = new Rig(s => s.FailReadWith = code);
        await rig.StartAsync();

        Assert.True(rig.Card.IsLoaded);
        Assert.False(rig.Card.IsAvailable);
        Assert.Equal(note, rig.Card.Note);
        Assert.True(rig.Card.ShowSettingsLink);
    }

    [Fact]
    public async Task MovingTheSliderShowsTheValueAtOnceAndWritesItForTheIslandsDisplay()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.Card.Percent = 72.4;

        Assert.Equal(72, rig.Card.Percent); // optimistic, rounded
        Assert.Equal("72%", rig.Card.PercentText);
        await Rig.Settle();
        Assert.Equal([(Display, 72)], rig.Service.Writes);
        Assert.Null(rig.Card.Error);
    }

    [Fact]
    public async Task ARefusedWriteSaysWhyAndRollsBackToTheRealValue()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.FailWriteWith = ErrorCode.IoFailure;

        rig.Card.Percent = 90;
        await Rig.Settle();

        Assert.Equal("The brightness couldn't be changed.", rig.Card.Error);
        Assert.Equal(40, rig.Card.Percent); // what the display really has
        rig.Card.DismissErrorCommand.Execute(null);
        Assert.Null(rig.Card.Error);
    }

    [Fact]
    public async Task ADeniedWriteSaysWindowsRefused()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.FailWriteWith = ErrorCode.AccessDenied;

        rig.Card.Percent = 90;
        await Rig.Settle();

        Assert.Equal("Windows didn't allow Lunar to change the brightness.", rig.Card.Error);
    }

    [Fact]
    public async Task AnOutsideChangeOfThisDisplayMovesTheSliderButNotAnotherDisplays()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.Service.State = new BrightnessState(65, BrightnessSource.BuiltIn, CapabilityStatus.Supported);
        rig.Service.RaiseChanged(@"\\.\DISPLAY2");
        await Rig.Settle();
        Assert.Equal(40, rig.Card.Percent);

        rig.Service.RaiseChanged(Display);
        await Rig.Settle();
        Assert.Equal(65, rig.Card.Percent);
    }

    [Fact]
    public async Task WhileTheHandIsOnTheSliderOrJustAfterItsOwnWriteNothingFromOutsideMovesIt()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.Card.IsAdjusting = true;
        rig.Service.State = new BrightnessState(10, BrightnessSource.BuiltIn, CapabilityStatus.Supported);
        rig.Service.RaiseChanged(Display);
        await Rig.Settle();
        Assert.Equal(40, rig.Card.Percent);

        rig.Card.IsAdjusting = false;
        rig.Card.Percent = 55; // its own write: Windows may still report the old value for a moment
        await Rig.Settle();
        rig.Service.State = new BrightnessState(40, BrightnessSource.BuiltIn, CapabilityStatus.Supported); // a stale report
        rig.Service.RaiseChanged(Display);
        await Rig.Settle();
        Assert.Equal(55, rig.Card.Percent);

        rig.Time.Advance(BrightnessCardViewModel.QuietAfterWrite);
        rig.Service.State = new BrightnessState(30, BrightnessSource.BuiltIn, CapabilityStatus.Supported);
        rig.Service.RaiseChanged(Display);
        await Rig.Settle();
        Assert.Equal(30, rig.Card.Percent);
    }

    [Fact]
    public async Task AnExternalMonitorIsReadAgainOnlyWhileTheCardIsVisible()
    {
        using var rig = new Rig(s => s.State = new BrightnessState(50, BrightnessSource.External, CapabilityStatus.Supported));
        await rig.StartAsync();
        Assert.Null(rig.Poll.Pending); // hidden: no polling

        rig.Card.SetVisible(true);
        await Rig.Settle();
        Assert.Equal(BrightnessCardViewModel.PollInterval, rig.Poll.Delay);

        rig.Service.State = new BrightnessState(80, BrightnessSource.External, CapabilityStatus.Supported);
        rig.Poll.Fire();
        await Rig.Settle();
        Assert.Equal(80, rig.Card.Percent);
        Assert.NotNull(rig.Poll.Pending); // keeps polling while visible

        rig.Card.SetVisible(false);
        Assert.Null(rig.Poll.Pending);
    }

    [Fact]
    public async Task ABuiltInPanelIsNeverPolledBecauseItAnnouncesItsChanges()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.Card.SetVisible(true);
        await Rig.Settle();

        Assert.Null(rig.Poll.Pending);
    }

    [Fact]
    public async Task BecomingVisibleReadsTheDisplayTheIslandIsOnNow()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.DisplayId = @"\\.\DISPLAY2"; // the island moved
        rig.Card.SetVisible(true);
        await Rig.Settle();

        Assert.Equal([Display, @"\\.\DISPLAY2"], rig.Service.Reads);
    }

    [Fact]
    public async Task DisposingStopsListeningAndPolling()
    {
        var rig = new Rig(s => s.State = new BrightnessState(50, BrightnessSource.External, CapabilityStatus.Supported));
        await rig.StartAsync();
        rig.Card.SetVisible(true);
        await Rig.Settle();
        Assert.Equal(1, rig.Service.Subscribers);

        rig.Dispose();

        Assert.Equal(0, rig.Service.Subscribers);
        Assert.Null(rig.Poll.Pending);
    }

    [Fact]
    public async Task FailuresAreLoggedWithTheirCodeOnly()
    {
        using var rig = new Rig(s => s.FailReadWith = ErrorCode.IoFailure);
        await rig.StartAsync();

        var logged = Assert.Single(rig.Log.Events);
        Assert.Equal(("brightness.card.read.failed", ErrorCode.IoFailure), (logged.EventId, logged.Error));
    }
}

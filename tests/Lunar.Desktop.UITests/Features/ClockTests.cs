using System.Globalization;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-16: the page's clock. Time, zone, regional format and timers are all fakes driven by hand, so midnight, a changed time
// zone, a changed 12/24 hour setting and an early, late or missing tick are each reproduced exactly.
public sealed class ClockTests
{
    /// <summary>A clock whose real moment (UTC), time zone and regional format the test can change at any time.</summary>
    private sealed class FakeLocaleClock : ILocaleClock
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

        public CultureInfo Format { get; set; } = CultureInfo.GetCultureInfo("en-US");

        public int Refreshes { get; private set; }

        public DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(Utc, Zone);

        public CultureInfo Culture => Format;

        public TimeZoneInfo TimeZone => Zone;

        public void Refresh() => Refreshes++;
    }

    private sealed class FakeTimer : IOneShotTimer
    {
        private Action? _callback;

        public TimeSpan? Pending { get; private set; }

        public int Schedules { get; private set; }

        public bool Disposed { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            Pending = delay;
            _callback = callback;
            Schedules++;
        }

        public void Cancel()
        {
            Pending = null;
            _callback = null;
        }

        /// <summary>The timer's time came: runs the callback it was given (if any).</summary>
        public void Fire()
        {
            var callback = _callback;
            Pending = null;
            _callback = null;
            callback?.Invoke();
        }

        public void Dispose()
        {
            Disposed = true;
            Cancel();
        }
    }

    private sealed class Launcher : ISystemSettingsLauncher
    {
        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class Rig : IDisposable
    {
        public FakeLocaleClock Clock { get; } = new();

        public FakeTimer Timer { get; } = new();

        public SystemPageViewModel Model { get; }

        public Rig()
        {
            Model = new SystemPageViewModel(new FakeVolumeService(), new FakeWifiService(), new FakeBluetoothService(), Clock, new Launcher(), work => work(), null, Timer);
        }

        public void Dispose() => Model.Dispose();
    }

    // ---- the minute tick ---------------------------------------------------------------------------------------------

    [Fact]
    public void NothingRunsUntilTheClockIsStarted()
    {
        using var rig = new Rig();

        Assert.Null(rig.Timer.Pending);
        Assert.Equal(0, rig.Timer.Schedules);
        Assert.Equal(string.Empty, rig.Model.TimeText);
    }

    [Fact]
    public void StartingShowsTheTimeAtOnceAndWaitsForTheNextMinute()
    {
        using var rig = new Rig();
        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 14, 5, 20, TimeSpan.Zero);

        rig.Model.StartClock();

        Assert.Equal("2:05 PM", rig.Model.TimeText);
        Assert.Equal(TimeSpan.FromSeconds(40) + TimeSpan.FromMilliseconds(10), rig.Timer.Pending);
    }

    [Fact]
    public void EveryTickShowsTheNewMinuteAndSchedulesTheNext()
    {
        using var rig = new Rig();
        rig.Model.StartClock();

        for (var minute = 6; minute <= 10; minute++)
        {
            rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 14, minute, 0, 5, TimeSpan.Zero);
            rig.Timer.Fire();

            Assert.Equal($"2:{minute:00} PM", rig.Model.TimeText);
            Assert.NotNull(rig.Timer.Pending);
        }

        Assert.Equal(6, rig.Timer.Schedules); // one per tick plus the first
    }

    [Fact]
    public void EachTickReReadsTheSystemSettings()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        var before = rig.Clock.Refreshes;

        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        rig.Timer.Fire();

        Assert.Equal(before + 1, rig.Clock.Refreshes);
    }

    [Fact]
    public void AnEarlyTickCorrectsItselfInsteadOfWaitingAWholeMinute()
    {
        using var rig = new Rig();
        rig.Model.StartClock();

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 14, 5, 59, 995, TimeSpan.Zero); // fired 5 ms early
        rig.Timer.Fire();

        Assert.Equal("2:05 PM", rig.Model.TimeText);
        Assert.True(rig.Timer.Pending < TimeSpan.FromSeconds(1)); // tries again at once, not in a minute

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 14, 6, 0, 5, TimeSpan.Zero);
        rig.Timer.Fire();

        Assert.Equal("2:06 PM", rig.Model.TimeText);
    }

    [Fact]
    public void ALateTickAfterSleepShowsTheRealTime()
    {
        using var rig = new Rig();
        rig.Model.StartClock();

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 17, 42, 13, TimeSpan.Zero); // the PC slept for hours
        rig.Timer.Fire();

        Assert.Equal("5:42 PM", rig.Model.TimeText);
        Assert.Equal(TimeSpan.FromSeconds(47) + TimeSpan.FromMilliseconds(10), rig.Timer.Pending);
    }

    // ---- date rollover -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheDateRollsOverAtMidnight()
    {
        using var rig = new Rig();
        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 23, 59, 30, TimeSpan.Zero);
        rig.Model.StartClock();
        Assert.Equal("11:59 PM", rig.Model.TimeText);
        Assert.Contains("Wednesday, November 4, 2026", rig.Model.DateText, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(10), rig.Timer.Pending);

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 5, 0, 0, 0, 8, TimeSpan.Zero);
        rig.Timer.Fire();

        Assert.Equal("12:00 AM", rig.Model.TimeText);
        Assert.Contains("Thursday, November 5, 2026", rig.Model.DateText, StringComparison.Ordinal);
        Assert.True(ClockFormat.IsNewDay(new DateTimeOffset(2026, 11, 4, 23, 59, 0, TimeSpan.Zero), rig.Clock.LocalNow));
    }

    [Fact]
    public void TheDateRollsOverAtTheUsersMidnightNotUtcMidnight()
    {
        using var rig = new Rig();
        rig.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Test+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");
        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 16, 59, 30, TimeSpan.Zero); // 23:59:30 at UTC+7
        rig.Model.StartClock();
        Assert.Contains("November 4", rig.Model.DateText, StringComparison.Ordinal);

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 17, 0, 0, 5, TimeSpan.Zero);
        rig.Timer.Fire();

        Assert.Contains("November 5", rig.Model.DateText, StringComparison.Ordinal);
        Assert.Equal("12:00 AM", rig.Model.TimeText);
    }

    [Fact]
    public void TheLastMinuteOfTheYearRollsIntoTheNewYear()
    {
        using var rig = new Rig();
        rig.Clock.Utc = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        rig.Model.StartClock();

        rig.Clock.Utc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, 5, TimeSpan.Zero);
        rig.Timer.Fire();

        Assert.Contains("January 1, 2027", rig.Model.DateText, StringComparison.Ordinal);
    }

    // ---- time zone ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheTimeZoneShowsTheNewZoneAtTheNextTick()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        Assert.Equal("2:05 PM", rig.Model.TimeText);
        Assert.Equal(TimeZoneInfo.Utc.StandardName, rig.Model.TimeZoneText);

        rig.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Test+9", TimeSpan.FromHours(9), "Tokyo Standard Time", "Tokyo Standard Time");
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        rig.Timer.Fire();

        Assert.Equal("11:06 PM", rig.Model.TimeText);
        Assert.Equal("Tokyo Standard Time", rig.Model.TimeZoneText);
    }

    [Fact]
    public void TheZoneNameFollowsDaylightSavingTime()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            Assert.Skip("This machine does not have the Pacific time zone.");
            return;
        }

        using var rig = new Rig();
        rig.Clock.Zone = zone;
        rig.Clock.Utc = new DateTimeOffset(2026, 7, 4, 20, 0, 0, TimeSpan.Zero); // summer
        rig.Model.RefreshClock();
        Assert.Equal(zone.DaylightName, rig.Model.TimeZoneText);

        rig.Clock.Utc = new DateTimeOffset(2026, 12, 4, 20, 0, 0, TimeSpan.Zero); // winter
        rig.Model.RefreshClock();
        Assert.Equal(zone.StandardName, rig.Model.TimeZoneText);
    }

    [Fact]
    public void TheClockJumpsCorrectlyAcrossADaylightSavingChange()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            Assert.Skip("This machine does not have the Pacific time zone.");
            return;
        }

        using var rig = new Rig();
        rig.Clock.Zone = zone;
        rig.Clock.Utc = new DateTimeOffset(2026, 3, 8, 9, 59, 30, TimeSpan.Zero); // 1:59:30 AM PST, one minute before clocks go forward
        rig.Model.StartClock();
        Assert.Equal("1:59 AM", rig.Model.TimeText);

        rig.Clock.Utc = new DateTimeOffset(2026, 3, 8, 10, 0, 0, 5, TimeSpan.Zero);
        rig.Timer.Fire();

        Assert.Equal("3:00 AM", rig.Model.TimeText); // 2:00 does not exist that night
    }

    // ---- 12 / 24 hour and regional format ---------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "2:05 PM")]
    [InlineData("de-DE", "14:05")]
    [InlineData("ja-JP", "14:05")]
    [InlineData("id-ID", "14.05")]
    public void TheRegionalFormatDecidesHowTheTimeLooks(string culture, string expected)
    {
        using var rig = new Rig();
        rig.Clock.Format = CultureInfo.GetCultureInfo(culture);

        rig.Model.StartClock();

        Assert.Equal(expected, rig.Model.TimeText);
    }

    [Fact]
    public void TheUsersExplicit24HourChoiceShowsWithinAMinute()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        Assert.Equal("2:05 PM", rig.Model.TimeText);

        // The user switches Windows to a 24 hour clock: the same region, a different short time pattern.
        var twentyFour = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        twentyFour.DateTimeFormat.ShortTimePattern = "HH:mm";
        rig.Clock.Format = twentyFour;
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        rig.Timer.Fire();

        Assert.Equal("14:06", rig.Model.TimeText);
        Assert.True(ClockFormat.Uses24HourClock(rig.Clock.Culture));
    }

    [Fact]
    public void MidnightAndNoonReadCorrectlyIn12And24HourFormats()
    {
        using var rig = new Rig();
        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        rig.Model.RefreshClock();
        Assert.Equal("12:00 AM", rig.Model.TimeText);

        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        rig.Model.RefreshClock();
        Assert.Equal("12:00 PM", rig.Model.TimeText);

        rig.Clock.Format = CultureInfo.GetCultureInfo("de-DE");
        rig.Clock.Utc = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        rig.Model.RefreshClock();
        Assert.Equal("00:00", rig.Model.TimeText);
    }

    [Fact]
    public void TheDateFollowsTheRegionalLanguageAndOrder()
    {
        using var rig = new Rig();
        rig.Clock.Format = CultureInfo.GetCultureInfo("de-DE");

        rig.Model.RefreshClock();

        Assert.Contains("Mittwoch", rig.Model.DateText, StringComparison.Ordinal);
        Assert.Contains("November", rig.Model.DateText, StringComparison.Ordinal);
    }

    // ---- accessible words --------------------------------------------------------------------------------------------

    [Fact]
    public void TheAccessibleLabelSaysTimeAndDateInOneSentence()
    {
        using var rig = new Rig();
        rig.Model.RefreshClock();

        Assert.Equal("2:05 PM, Wednesday, November 4, 2026", rig.Model.ClockLabel);
    }

    [Fact]
    public void TheLabelChangesWithEveryMinuteAndRaisesItsNotification()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(1);
        rig.Timer.Fire();

        Assert.Equal("2:06 PM, Wednesday, November 4, 2026", rig.Model.ClockLabel);
        Assert.Contains(nameof(SystemPageViewModel.ClockLabel), raised);
        Assert.Contains(nameof(SystemPageViewModel.TimeText), raised);
    }

    [Fact]
    public void ANotificationIsOnlyRaisedForWhatChanged()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        var raised = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Clock.Utc = rig.Clock.Utc.AddSeconds(20); // same minute
        rig.Timer.Fire();

        Assert.DoesNotContain(nameof(SystemPageViewModel.TimeText), raised);
        Assert.DoesNotContain(nameof(SystemPageViewModel.DateText), raised);
        Assert.DoesNotContain(nameof(SystemPageViewModel.TimeZoneText), raised);
    }

    // ---- starting and stopping ---------------------------------------------------------------------------------------

    [Fact]
    public void StoppingCancelsTheTimerAndLaterTicksDoNothing()
    {
        using var rig = new Rig();
        rig.Model.StartClock();

        rig.Model.StopClock();

        Assert.Null(rig.Timer.Pending);
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(5);
        rig.Timer.Fire();
        Assert.Equal("2:05 PM", rig.Model.TimeText); // nothing ran while stopped
    }

    [Fact]
    public void StartingAgainShowsTheTimeThatIsNowAndResumes()
    {
        using var rig = new Rig();
        rig.Model.StartClock();
        rig.Model.StopClock();
        rig.Clock.Utc = rig.Clock.Utc.AddMinutes(30);

        rig.Model.StartClock();

        Assert.Equal("2:35 PM", rig.Model.TimeText);
        Assert.NotNull(rig.Timer.Pending);
    }

    [Fact]
    public void StartingTwiceDoesNotScheduleTwice()
    {
        using var rig = new Rig();

        rig.Model.StartClock();
        rig.Model.StartClock();
        rig.Model.StartClock();

        Assert.Equal(1, rig.Timer.Schedules);
    }

    [Fact]
    public void StoppingWhenNotStartedIsHarmless()
    {
        using var rig = new Rig();

        rig.Model.StopClock();
        rig.Model.StopClock();

        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void DisposingStopsAndReleasesTheTimerAndStartingAfterwardsDoesNothing()
    {
        var rig = new Rig();
        rig.Model.StartClock();

        rig.Dispose();
        rig.Model.StartClock();

        Assert.True(rig.Timer.Disposed);
        Assert.Null(rig.Timer.Pending);
    }

    // ---- tied to the island ------------------------------------------------------------------------------------------

    [Fact]
    public void TheClockRunsOnlyWhileTheIslandIsOpen()
    {
        using var rig = new Rig();
        var island = new IslandViewModel();
        using var lifetime = new ClockLifetime(island, rig.Model);
        Assert.Null(rig.Timer.Pending); // the pill: nothing runs

        island.Toggle();
        island.CompleteExpansion();
        Assert.True(island.IsExpanded);
        Assert.NotNull(rig.Timer.Pending);
        Assert.Equal("2:05 PM", rig.Model.TimeText);

        island.Collapse();
        Assert.Null(rig.Timer.Pending); // closing stops it at once

        island.CompleteCollapse();
        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void AnIslandThatIsAlreadyOpenStartsTheClockRightAway()
    {
        using var rig = new Rig();
        var island = new IslandViewModel();
        island.Toggle();
        island.CompleteExpansion();

        using var lifetime = new ClockLifetime(island, rig.Model);

        Assert.NotNull(rig.Timer.Pending);
    }

    [Fact]
    public void LettingGoOfTheIslandStopsTheClockAndIgnoresLaterChanges()
    {
        using var rig = new Rig();
        var island = new IslandViewModel();
        var lifetime = new ClockLifetime(island, rig.Model);
        island.Toggle();
        island.CompleteExpansion();
        Assert.NotNull(rig.Timer.Pending);

        lifetime.Dispose();
        lifetime.Dispose();
        Assert.Null(rig.Timer.Pending);

        island.Collapse();
        island.CompleteCollapse();
        island.Toggle();
        island.CompleteExpansion();
        Assert.Null(rig.Timer.Pending);
    }

    [Fact]
    public void ClockLifetimeRejectsMissingParts()
    {
        using var rig = new Rig();

        Assert.Throws<ArgumentNullException>(() => new ClockLifetime(null!, rig.Model));
        Assert.Throws<ArgumentNullException>(() => new ClockLifetime(new IslandViewModel(), null!));
    }

    // ---- the real timer ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheRealTimerFiresOnceAndStopsWhenCancelledOrDisposed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                using var timer = new DispatcherOneShotTimer(dispatcher);
                var fired = 0;
                timer.Schedule(TimeSpan.FromMilliseconds(20), () => fired++);
                Pump(dispatcher, () => fired > 0, 3000);
                Assert.Equal(1, fired);

                Pump(dispatcher, () => false, 150); // it does not tick again by itself
                Assert.Equal(1, fired);

                timer.Schedule(TimeSpan.FromMilliseconds(20), () => fired++);
                timer.Cancel();
                Pump(dispatcher, () => false, 150);
                Assert.Equal(1, fired);
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
            throw new InvalidOperationException("The real timer check failed.", failure);
    }

    private static void Pump(System.Windows.Threading.Dispatcher dispatcher, Func<bool> until, int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!until() && DateTime.UtcNow < end)
        {
            dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(5);
        }
    }
}

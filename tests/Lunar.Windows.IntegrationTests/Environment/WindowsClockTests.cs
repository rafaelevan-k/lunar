using System.Globalization;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System;
using Lunar.Windows.Environment;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Environment;

public sealed class WindowsClockTests
{
    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        private long _stamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _stamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by)
        {
            _utcNow += by;
            _stamp += (long)by.TotalMilliseconds;
        }
    }

    private sealed class CollectingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private static TimeZoneInfo Zone(string id, int hours) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), id, id);

    // ---- injected sources: refresh rules ------------------------------------------------------------------------------

    [Fact]
    public void LocalTimeIsTheCurrentMomentInTheUsersZone()
    {
        var time = new FakeTime(); // 17:00 UTC
        var clock = new WindowsClock(time, () => Zone("A", 7), () => CultureInfo.GetCultureInfo("en-US"), () => { }, TimeSpan.FromSeconds(30));

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(7)), clock.LocalNow);
        Assert.Equal(TimeSpan.FromHours(7), clock.LocalNow.Offset);
    }

    [Fact]
    public void ZoneAndFormatAreReReadOnlyAfterTheRefreshInterval()
    {
        var time = new FakeTime();
        var zone = Zone("A", 7);
        var culture = CultureInfo.GetCultureInfo("en-US");
        var reads = 0;
        var clock = new WindowsClock(time, () => { reads++; return zone; }, () => culture, () => { }, TimeSpan.FromSeconds(30));
        Assert.Equal(1, reads); // read once at construction

        _ = clock.LocalNow;
        _ = clock.Culture;
        _ = clock.TimeZone;
        time.Advance(TimeSpan.FromSeconds(29));
        _ = clock.LocalNow;
        Assert.Equal(1, reads); // not yet

        zone = Zone("B", 9); // the user changed the zone
        culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal(TimeSpan.FromHours(7), clock.TimeZone.BaseUtcOffset); // still the old one inside the interval

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromHours(9), clock.LocalNow.Offset);
        Assert.Equal("de-DE", clock.Culture.Name);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void RefreshPicksUpAChangeAtOnceAndClearsTheCaches()
    {
        var time = new FakeTime();
        var zone = Zone("A", 7);
        var cleared = 0;
        var clock = new WindowsClock(time, () => zone, () => CultureInfo.InvariantCulture, () => cleared++, TimeSpan.FromMinutes(10));
        Assert.Equal(1, cleared);

        zone = Zone("B", -5);
        clock.Refresh();

        Assert.Equal(2, cleared);
        Assert.Equal(TimeSpan.FromHours(-5), clock.LocalNow.Offset);
    }

    [Fact]
    public void ADayCanRollOverBecauseTheZoneChanged()
    {
        var time = new FakeTime(); // 17:00 UTC
        var zone = Zone("A", 6); // 23:00 on the 4th
        var clock = new WindowsClock(time, () => zone, () => CultureInfo.InvariantCulture, () => { }, TimeSpan.FromSeconds(30));
        var before = clock.LocalNow;

        zone = Zone("B", 8); // 01:00 on the 5th, without any time passing
        clock.Refresh();

        Assert.True(ClockFormat.IsNewDay(before, clock.LocalNow));
    }

    // ---- failure handling ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnreadableZoneKeepsTheLastGoodOneAndIsLoggedOnlyOnce()
    {
        var time = new FakeTime();
        var log = new CollectingLog();
        var good = true;
        var clock = new WindowsClock(time,
            () => good ? Zone("A", 7) : throw new TimeZoneNotFoundException("gone"),
            () => CultureInfo.InvariantCulture, () => { }, TimeSpan.FromSeconds(30), log);

        good = false;
        for (var i = 0; i < 5; i++)
        {
            clock.Refresh();
            Assert.Equal(TimeSpan.FromHours(7), clock.LocalNow.Offset); // the last good zone
        }

        Assert.Single(log.Events);
        Assert.Equal("clock.zone.failed", log.Events[0].EventId);
        Assert.Equal(typeof(TimeZoneNotFoundException), log.Events[0].ExceptionType);

        good = true; // recovers, and a new failure would be logged again
        clock.Refresh();
        good = false;
        clock.Refresh();
        Assert.Equal(2, log.Events.Count);
    }

    [Fact]
    public void ABrokenZoneAtStartFallsBackToUtcInsteadOfThrowing()
    {
        var clock = new WindowsClock(new FakeTime(), () => throw new InvalidTimeZoneException("bad"), () => throw new CultureNotFoundException("bad"),
            () => throw new InvalidOperationException("no cache"), TimeSpan.FromSeconds(30));

        Assert.Equal(TimeZoneInfo.Utc, clock.TimeZone);
        Assert.Equal(CultureInfo.InvariantCulture, clock.Culture);
        Assert.Equal(TimeSpan.Zero, clock.LocalNow.Offset);
    }

    [Fact]
    public void ANullFromASourceKeepsTheLastValue()
    {
        var clock = new WindowsClock(new FakeTime(), () => null!, () => null!, () => { }, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeZoneInfo.Utc, clock.TimeZone);
        Assert.Equal(CultureInfo.InvariantCulture, clock.Culture);
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        var time = new FakeTime();
        Func<TimeZoneInfo> zone = () => TimeZoneInfo.Utc;
        Func<CultureInfo> culture = () => CultureInfo.InvariantCulture;
        Assert.Throws<ArgumentNullException>(() => new WindowsClock(null!, zone, culture, () => { }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new WindowsClock(time, null!, culture, () => { }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new WindowsClock(time, zone, null!, () => { }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentNullException>(() => new WindowsClock(time, zone, culture, null!, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsClock(time, zone, culture, () => { }, TimeSpan.Zero));
    }

    [Fact]
    public async Task ConcurrentReadsAndRefreshesNeverThrowOrTear()
    {
        var time = new FakeTime();
        var flip = 0;
        var clock = new WindowsClock(time, () => Zone("Z", (Interlocked.Increment(ref flip) % 2 == 0) ? 7 : 9), () => CultureInfo.InvariantCulture, () => { }, TimeSpan.FromMilliseconds(1));

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var now = clock.LocalNow;
                Assert.Contains(now.Offset.TotalHours, new[] { 0d, 7d, 9d });
                if (i % 50 == 0)
                    clock.Refresh();
                time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }));

        await Task.WhenAll(tasks);
    }

    // ---- the real Windows settings ----------------------------------------------------------------------------------

    [Fact]
    public void TheRealClockMatchesTheSystemTimeAndSettings()
    {
        var clock = new WindowsClock();

        var difference = (clock.LocalNow - DateTimeOffset.Now).Duration();
        Assert.True(difference < TimeSpan.FromSeconds(2), $"The clock differs from the system by {difference}.");
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow), clock.LocalNow.Offset);
        Assert.Equal(CultureInfo.CurrentCulture.Name, clock.Culture.Name);
        Assert.Equal(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, clock.Culture.DateTimeFormat.ShortTimePattern);
        Assert.Equal(TimeZoneInfo.Local.Id, clock.TimeZone.Id);
    }

    [Fact]
    public void TheRealClockWorksThroughBothPortsAndRefreshesWithoutError()
    {
        var clock = new WindowsClock();
        IClock plain = clock;
        ILocaleClock locale = clock;

        clock.Refresh();

        Assert.Equal(plain.LocalNow.Offset, locale.LocalNow.Offset);
        Assert.False(string.IsNullOrWhiteSpace(ClockFormat.FormatTime(locale.LocalNow, locale.Culture)));
        Assert.True(ClockFormat.UntilNextMinute(locale.LocalNow) > TimeSpan.Zero);
    }
}

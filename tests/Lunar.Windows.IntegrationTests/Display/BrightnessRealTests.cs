using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Display;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Display;

/// <summary>
/// The real brightness service on this PC's displays (ADR-0021, BE-37), as a standard user. Reading is always safe. Changing the brightness
/// is opt-in (LUNAR_REAL_BRIGHTNESS=1) and always puts the original value back.
/// </summary>
public sealed class BrightnessRealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePrimaryDisplayIsReadWithoutElevationAndMatchesWhatWmiSays()
    {
        await using var service = new WindowsBrightnessService();

        var read = await service.GetAsync(null, Ct);

        Assert.True(read.IsSuccess, read.Error?.SafeMessage);
        var panels = new WmiBrightness().Read();
        if (panels.Count == 0)
        {
            Assert.NotEqual(BrightnessSource.BuiltIn, read.Value.Source);
            Assert.Skip("This PC has no built-in panel that WMI can dim; the read itself worked.");
        }

        Assert.Equal(CapabilityStatus.Supported, read.Value.Capability);
        Assert.Equal(BrightnessSource.BuiltIn, read.Value.Source);
        Assert.Contains(panels, p => p.Percent == read.Value.Percent);
    }

    [Fact]
    public async Task EveryConnectedDisplayGivesATruthfulAnswerAndAnUnknownOneIsUnavailable()
    {
        await using var service = new WindowsBrightnessService();
        var directory = new Win32DisplayDirectory();

        for (var n = 1; n <= 4; n++)
        {
            var id = @"\\.\DISPLAY" + n;
            if (directory.MonitorInstances(id).Count == 0)
                continue;
            var read = await service.GetAsync(id, Ct);
            Assert.True(read.IsSuccess, read.Error?.SafeMessage);
            Assert.True(read.Value.Capability is CapabilityStatus.Supported or CapabilityStatus.Unsupported,
                $"{id}: {read.Value.Capability}");
        }

        var unknown = await service.GetAsync(@"\\.\DISPLAY99", Ct);
        Assert.Equal(BrightnessState.Unavailable, unknown.Value);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetAsync(@"\\.\DISPLAY99", 50, Ct)).Error!.Code);
    }

    [Fact]
    public async Task RealSmokeChangeTheBrightnessSeeTheEventAndPutItBack()
    {
        if (System.Environment.GetEnvironmentVariable("LUNAR_REAL_BRIGHTNESS") != "1")
            Assert.Skip("Set LUNAR_REAL_BRIGHTNESS=1 to change this display's brightness once and put it back.");

        await using var service = new WindowsBrightnessService();
        var original = await service.GetAsync(null, Ct);
        if (original.IsFailure || original.Value.Capability != CapabilityStatus.Supported)
            Assert.Skip("The primary display's brightness cannot be changed here.");

        var events = new List<string>();
        service.Changed += id =>
        {
            lock (events)
                events.Add(id);
        };
        await Task.Delay(500, Ct); // let the watcher start
        var target = original.Value.Percent >= 50 ? original.Value.Percent - 10 : original.Value.Percent + 10;
        try
        {
            Assert.True((await service.SetAsync(null, target, Ct)).IsSuccess);
            var changed = await ReadUntil(service, v => Math.Abs(v - target) <= 5);
            Assert.InRange(changed, target - 5, target + 5); // a panel with fewer steps settles on the nearest one
            if (original.Value.Source == BrightnessSource.BuiltIn)
            {
                for (var i = 0; i < 40 && events.Count == 0; i++)
                    await Task.Delay(50, Ct);
                Assert.NotEmpty(events);
            }
        }
        finally
        {
            Assert.True((await service.SetAsync(null, original.Value.Percent, CancellationToken.None)).IsSuccess);
        }

        var restored = await ReadUntil(service, v => Math.Abs(v - original.Value.Percent) <= 5);
        Assert.InRange(restored, original.Value.Percent - 5, original.Value.Percent + 5);

        // Windows reports a new value with a delay (seen: the old value for over 300 ms after a change), so the restore must still hold
        // once everything has settled.
        await Task.Delay(1500, CancellationToken.None);
        var settled = await service.GetAsync(null, CancellationToken.None);
        Assert.InRange(settled.Value.Percent, original.Value.Percent - 5, original.Value.Percent + 5);
    }

    private static async Task<int> ReadUntil(WindowsBrightnessService service, Func<int, bool> done)
    {
        var value = -1;
        for (var i = 0; i < 20; i++)
        {
            var read = await service.GetAsync(null, CancellationToken.None);
            if (read.IsSuccess)
            {
                value = read.Value.Percent;
                if (done(value))
                    break;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        return value;
    }
}

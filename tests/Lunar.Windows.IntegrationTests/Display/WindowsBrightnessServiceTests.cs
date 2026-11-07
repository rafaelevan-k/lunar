using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Display;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Display;

/// <summary>How the brightness service chooses a route and reports results, with WMI and the displays replaced.</summary>
public sealed class WindowsBrightnessServiceTests
{
    private const string Laptop = @"\\.\DISPLAY1";
    private const string External = @"\\.\DISPLAY2";
    private const string PanelInstance = @"DISPLAY\AUO2992\4&2138af8c&0&UID265988_0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeWmi : IWmiBrightness
    {
        public List<WmiPanel> Panels { get; } = [new(PanelInstance, 30)];

        public int? ReadError { get; set; }

        public int? SetError { get; set; }

        public List<(string Instance, int Percent)> Sets { get; } = [];

        public Action<string>? Watcher { get; private set; }

        public bool WatchStopped { get; private set; }

        public IReadOnlyList<WmiPanel> Read() => ReadError is { } hr ? throw new COMException("x", hr) : Panels;

        public void Set(string instanceName, int percent)
        {
            if (SetError is { } hr)
                throw new COMException("x", hr);
            Sets.Add((instanceName, percent));
        }

        public IDisposable Watch(Action<string> changed)
        {
            Watcher = changed;
            return new Stop(this);
        }

        private sealed class Stop(FakeWmi owner) : IDisposable
        {
            public void Dispose() => owner.WatchStopped = true;
        }
    }

    private sealed class FakeDisplays : IDisplayDirectory
    {
        public string? Primary { get; set; } = Laptop;

        public Dictionary<string, string[]> Monitors { get; } = new()
        {
            [Laptop] = [@"DISPLAY\AUO2992\4&2138af8c&0&UID265988"],
            [External] = [@"DISPLAY\DEL4321\5&1&0&UID1"]
        };

        public DdcReading? Ddc { get; set; } = new DdcReading(0, 60, 100);

        public bool WriteOk { get; set; } = true;

        public List<(string Display, int Raw)> Writes { get; } = [];

        public string? PrimaryDisplay() => Primary;

        public IReadOnlyList<string> MonitorInstances(string displayId) => Monitors.TryGetValue(displayId, out var m) ? m : [];

        public DdcReading? ReadDdc(string displayId) => Ddc;

        public bool WriteDdc(string displayId, int raw)
        {
            Writes.Add((displayId, raw));
            return WriteOk;
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

    [Fact]
    public async Task ABuiltInPanelIsReadAndSetThroughWindowsAndThePrimaryIsTheDefault()
    {
        var wmi = new FakeWmi();
        var displays = new FakeDisplays();
        await using var service = new WindowsBrightnessService(wmi, displays);

        var read = await service.GetAsync(null, Ct);
        var set = await service.SetAsync(Laptop, 75, Ct);

        Assert.Equal(new BrightnessState(30, BrightnessSource.BuiltIn, CapabilityStatus.Supported), read.Value);
        Assert.True(set.IsSuccess);
        Assert.Equal([(PanelInstance, 75)], wmi.Sets);
        Assert.Empty(displays.Writes);
    }

    [Fact]
    public async Task AnExternalMonitorIsReadAndSetThroughDdcInItsOwnUnits()
    {
        var displays = new FakeDisplays { Ddc = new DdcReading(10, 55, 60) };
        await using var service = new WindowsBrightnessService(new FakeWmi(), displays);

        var read = await service.GetAsync(External, Ct);
        var set = await service.SetAsync(External, 50, Ct);

        Assert.Equal(new BrightnessState(90, BrightnessSource.External, CapabilityStatus.Supported), read.Value); // (55-10)/(60-10)
        Assert.True(set.IsSuccess);
        Assert.Equal([(External, 35)], displays.Writes); // 10 + 50% of 50
    }

    [Fact]
    public async Task AMonitorThatDoesNotAnswerIsUnsupportedAndSayingSoIsNotAnError()
    {
        var displays = new FakeDisplays { Ddc = null };
        await using var service = new WindowsBrightnessService(new FakeWmi(), displays);

        var read = await service.GetAsync(External, Ct);
        var set = await service.SetAsync(External, 50, Ct);

        Assert.Equal(BrightnessState.Unsupported, read.Value);
        Assert.Equal(ErrorCode.Unsupported, set.Error!.Code);
        Assert.Equal("Brightness can't be changed here for this display.", set.Error.SafeMessage);
    }

    [Fact]
    public async Task AMonitorThatRefusesAWriteIsAnIoFailure()
    {
        var displays = new FakeDisplays { WriteOk = false };
        await using var service = new WindowsBrightnessService(new FakeWmi(), displays);

        Assert.Equal(ErrorCode.IoFailure, (await service.SetAsync(External, 50, Ct)).Error!.Code);
    }

    [Fact]
    public async Task ADisplayThatIsNotConnectedIsUnavailable()
    {
        var displays = new FakeDisplays { Primary = null };
        await using var service = new WindowsBrightnessService(new FakeWmi(), displays);

        Assert.Equal(BrightnessState.Unavailable, (await service.GetAsync(null, Ct)).Value);
        Assert.Equal(BrightnessState.Unavailable, (await service.GetAsync(@"\\.\DISPLAY9", Ct)).Value);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await service.SetAsync(@"\\.\DISPLAY9", 50, Ct)).Error!.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task AValueOutside0To100IsInvalidInputAndNothingIsTouched(int percent)
    {
        var wmi = new FakeWmi();
        await using var service = new WindowsBrightnessService(wmi, new FakeDisplays());

        Assert.Equal(ErrorCode.InvalidInput, (await service.SetAsync(Laptop, percent, Ct)).Error!.Code);
        Assert.Empty(wmi.Sets);
    }

    [Fact]
    public async Task AMachineWithoutWmiBrightnessFallsBackToDdcQuietly()
    {
        var wmi = new FakeWmi { ReadError = unchecked((int)0x8004100C) }; // WBEM_E_NOT_SUPPORTED, as on most desktops
        await using var service = new WindowsBrightnessService(wmi, new FakeDisplays());

        var read = await service.GetAsync(Laptop, Ct);

        Assert.Equal(BrightnessSource.External, read.Value.Source);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x80041003))]
    public async Task WindowsRefusingIsAccessDeniedAndLoggedWithItsCodeOnly(int hr)
    {
        var log = new RecordingLog();
        var wmi = new FakeWmi { SetError = hr };
        await using var service = new WindowsBrightnessService(wmi, new FakeDisplays(), log);

        var set = await service.SetAsync(Laptop, 40, Ct);

        Assert.Equal(ErrorCode.AccessDenied, set.Error!.Code);
        Assert.Equal("Windows did not allow Lunar to change the brightness.", set.Error.SafeMessage);
        var logged = Assert.Single(log.Events);
        Assert.Equal(("brightness.set.failed", ErrorCode.AccessDenied), (logged.EventId, logged.Error));
    }

    [Fact]
    public async Task OtherWmiFailuresAreAnIoFailureThatCanBeRetried()
    {
        var wmi = new FakeWmi { SetError = unchecked((int)0x80041001) };
        await using var service = new WindowsBrightnessService(wmi, new FakeDisplays());

        var set = await service.SetAsync(Laptop, 40, Ct);

        Assert.Equal(ErrorCode.IoFailure, set.Error!.Code);
        Assert.True(set.Error.Retryable);
    }

    [Fact]
    public async Task ACancelledCallIsCancelled()
    {
        await using var service = new WindowsBrightnessService(new FakeWmi(), new FakeDisplays());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.True((await service.GetAsync(null, cancelled.Token)).IsCancelled);
        Assert.True((await service.SetAsync(null, 50, cancelled.Token)).IsCancelled);
    }

    [Fact]
    public async Task AnOutsideChangeOfThePanelNamesItsDisplayAndAThrowingHandlerIsIsolated()
    {
        var wmi = new FakeWmi();
        var log = new RecordingLog();
        var service = new WindowsBrightnessService(wmi, new FakeDisplays(), log);
        var seen = new List<string>();
        service.Changed += _ => throw new InvalidOperationException("handler bug");
        service.Changed += seen.Add;

        wmi.Watcher!(PanelInstance);
        wmi.Watcher!(@"DISPLAY\UNKNOWN\1_0"); // a panel no display shows: nothing to tell

        Assert.Equal([Laptop], seen);
        Assert.Contains(log.Events, e => e.EventId == "brightness.handler.failed");

        await service.DisposeAsync();
        Assert.True(wmi.WatchStopped);
        wmi.Watcher!(PanelInstance);
        Assert.Single(seen);
    }

    [Fact]
    public void NothingIsWatchedUntilSomeoneListens()
    {
        var wmi = new FakeWmi();
        _ = new WindowsBrightnessService(wmi, new FakeDisplays());

        Assert.Null(wmi.Watcher);
    }

    [Theory]
    [InlineData(0, 0, 100, 0)]
    [InlineData(0, 100, 100, 100)]
    [InlineData(0, 37, 100, 37)]
    [InlineData(20, 60, 120, 40)]
    [InlineData(5, 5, 5, 0)] // a broken range is 0, never a division by zero
    public void DdcReadingsAreScaledTo0To100(int min, int current, int max, int expected) =>
        Assert.Equal(expected, new DdcReading(min, current, max).Percent);

    [Theory]
    [InlineData(0, 100, 0, 0)]
    [InlineData(0, 100, 100, 100)]
    [InlineData(20, 120, 40, 60)]
    public void PercentagesBecomeTheMonitorsOwnUnits(int min, int max, int percent, int expected) =>
        Assert.Equal(expected, new DdcReading(min, min, max).ToRaw(percent));

    [Theory]
    [InlineData(@"\\?\DISPLAY#AUO2992#4&2138af8c&0&UID265988#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"DISPLAY\AUO2992\4&2138af8c&0&UID265988")]
    [InlineData(@"\\?\display#DEL4321#5&1&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"DISPLAY\DEL4321\5&1&0&UID1")]
    [InlineData(@"MONITOR\AUO2992\{4d36e96e}\0001", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AMonitorInterfaceNameBecomesTheStartOfItsWmiInstanceName(string? interfaceName, string? expected) =>
        Assert.Equal(expected, Win32DisplayDirectory.ToInstance(interfaceName));
}

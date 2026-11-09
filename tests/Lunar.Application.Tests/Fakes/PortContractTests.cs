using Lunar.Application.Abstractions.System;
using Lunar.Application.Media.Models;
using Lunar.Application.System.Models;
using Lunar.Application.Tests.Models;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Fakes;

/// <summary>
/// Shows the ADR-0006 port semantics through the fakes that FE and use-case tests will reuse. These are
/// unit tests of the contract and fakes only; they say nothing about Windows adapter behavior.
/// </summary>
public sealed class PortContractTests
{
    private sealed class Recorder<T> : IObserver<T>
    {
        public List<T> Values { get; } = [];

        public bool Completed { get; private set; }

        public Exception? Error { get; private set; }

        public void OnNext(T value) => Values.Add(value);

        public void OnError(Exception error) => Error = error;

        public void OnCompleted() => Completed = true;
    }

    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    [Fact]
    public async Task ReadsAndCommandsReportCancellationAsFailureResult()
    {
        await using var volume = new FakeVolumeService();
        var token = Cancelled();

        Assert.True((await volume.GetAsync(token)).IsCancelled);
        Assert.True((await volume.SetVolumeAsync(0.2f, token)).IsCancelled);
        Assert.Equal(0.5f, volume.State.Scalar);
    }

    [Fact]
    public async Task CommandFailureIsTypedNotThrown()
    {
        await using var volume = new FakeVolumeService();

        var result = await volume.SetVolumeAsync(1.5f, CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Equal(0.5f, volume.State.Scalar);
    }

    [Fact]
    public async Task ChangesHaveNoReplayAndDeliverCommandsAndExternalEvents()
    {
        await using var volume = new FakeVolumeService();
        await volume.SetVolumeAsync(0.1f, CancellationToken.None);
        var recorder = new Recorder<VolumeState>();

        using var subscription = volume.Changes.Subscribe(recorder);
        Assert.Empty(recorder.Values);

        await volume.SetMuteAsync(true, CancellationToken.None);
        volume.RaiseExternalChange(new VolumeState(0.9f, true, CapabilityStatus.Supported));

        Assert.Equal(new[] { 0.1f, 0.9f }, recorder.Values.Select(v => v.Scalar));
        Assert.Null(recorder.Error);
    }

    [Fact]
    public async Task UnsubscribeIsIdempotentAndStopsDelivery()
    {
        await using var volume = new FakeVolumeService();
        var recorder = new Recorder<VolumeState>();
        var subscription = volume.Changes.Subscribe(recorder);

        subscription.Dispose();
        subscription.Dispose();
        await volume.SetMuteAsync(true, CancellationToken.None);

        Assert.Empty(recorder.Values);
        Assert.Equal(0, volume.ObserverCount);
    }

    [Fact]
    public async Task DisposeCompletesObserversAndLaterCallsFailWithoutThrowing()
    {
        var volume = new FakeVolumeService();
        var recorder = new Recorder<VolumeState>();
        using var subscription = volume.Changes.Subscribe(recorder);

        await volume.DisposeAsync();

        Assert.True(recorder.Completed);
        Assert.True(volume.IsDisposed);
        Assert.Equal(ErrorCode.DeviceUnavailable, (await volume.GetAsync(CancellationToken.None)).Error!.Code);
        var late = new Recorder<VolumeState>();
        volume.Changes.Subscribe(late).Dispose();
        Assert.True(late.Completed);
    }

    [Fact]
    public async Task RadioStatesStayDistinctAndToggleRequiresCapability()
    {
        await using var wifi = new FakeWifiService
        {
            State = new WifiState(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Disconnected, true)
        };
        await using var bluetooth = new FakeBluetoothService
        {
            State = new BluetoothState(CapabilityStatus.PermissionDenied, RadioState.Unknown, false)
        };

        Assert.Equal(WifiConnectivity.Disconnected, (await wifi.GetAsync(CancellationToken.None)).Value.Connectivity);
        Assert.True((await wifi.SetEnabledAsync(false, CancellationToken.None)).IsSuccess);
        Assert.Equal(RadioState.Disabled, wifi.State.Radio);
        Assert.Equal(ErrorCode.AccessDenied, (await bluetooth.SetEnabledAsync(true, CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task SettingsLauncherFailureIsAResultAndOnlyWhitelistedPagesExist()
    {
        var launcher = new FakeSettingsLauncher();

        Assert.True((await launcher.OpenAsync(SettingsPage.Bluetooth, CancellationToken.None)).IsSuccess);
        launcher.Fail = true;
        Assert.True((await launcher.OpenAsync(SettingsPage.Wifi, CancellationToken.None)).IsFailure);
        Assert.Equal(new[] { SettingsPage.Bluetooth }, launcher.Opened);
        // The whitelist grows only by decision: Display was added for brightness (ADR-0021, FE-32), Microphone for the recorder (ADR-0022, BE-40).
        Assert.Equal([SettingsPage.Wifi, SettingsPage.Bluetooth, SettingsPage.DateAndTime, SettingsPage.Display, SettingsPage.Microphone], Enum.GetValues<SettingsPage>());
    }

    [Fact]
    public void ClockIsInjectable()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(7)));

        Assert.Equal(9, clock.LocalNow.Hour);
        clock.LocalNow = clock.LocalNow.AddHours(1);
        Assert.Equal(10, clock.LocalNow.Hour);
    }

    [Fact]
    public async Task MediaSeekRequiresCapabilityAndKnownSession()
    {
        await using var media = new FakeMediaService();
        var seekable = ModelValidationTests.Snapshot("a", duration: TimeSpan.FromMinutes(3),
            capabilities: new MediaCapabilities(true, true, true, true));
        var live = ModelValidationTests.Snapshot("b", capabilities: new MediaCapabilities(true, false, false, true));
        media.SetSessions(seekable, live);

        Assert.True((await media.SeekAsync("a", TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess);
        Assert.Equal(ErrorCode.Unsupported, (await media.SeekAsync("b", TimeSpan.FromSeconds(30), CancellationToken.None)).Error!.Code);
        Assert.Equal(ErrorCode.InvalidInput, (await media.NextAsync("missing", CancellationToken.None)).Error!.Code);
        Assert.Equal(new[] { "seek" }, media.Commands);
    }

    [Fact]
    public async Task MediaChangesEmitFullListSoMissingIdMeansClosed()
    {
        await using var media = new FakeMediaService();
        var recorder = new Recorder<IReadOnlyList<MediaSessionSnapshot>>();
        using var subscription = media.Changes.Subscribe(recorder);

        media.SetSessions(ModelValidationTests.Snapshot("a"), ModelValidationTests.Snapshot("b"));
        media.SetSessions(ModelValidationTests.Snapshot("b"));

        Assert.Equal(2, recorder.Values.Count);
        Assert.DoesNotContain(recorder.Values[1], s => s.Id == "a");
        Assert.Contains(recorder.Values[0], s => s.Id == "a");
    }
}

using System.Globalization;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// The view model only: ports are fakes, posting is inline. The page itself is covered in SystemPageTests.
public sealed class SystemPageViewModelTests
{
    private sealed class FakeLocaleClock : ILocaleClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

        public int Refreshes { get; private set; }

        public DateTimeOffset LocalNow => Now;

        public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en-US");

        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

        public void Refresh() => Refreshes++;
    }

    private sealed class FakeLauncher : ISystemSettingsLauncher
    {
        public List<SettingsPage> Opened { get; } = [];

        public Result Next { get; set; } = Result.Success();

        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct)
        {
            Opened.Add(page);
            return Task.FromResult(Next);
        }
    }

    /// <summary>A volume service whose writes can be refused and whose state can be anything.</summary>
    private sealed class ScriptedVolume : IVolumeService
    {
        private readonly TestObservable<VolumeState> _changes = new();

        public VolumeState State { get; set; } = new(0.5f, false, CapabilityStatus.Supported);

        public OperationError? WriteFailure { get; set; }

        public OperationError? ReadFailure { get; set; }

        public List<float> Levels { get; } = [];

        public IObservable<VolumeState> Changes => _changes;

        public Task<Result<VolumeState>> GetAsync(CancellationToken ct) =>
            Task.FromResult(ReadFailure is not null ? Result.Failure<VolumeState>(ReadFailure) : Result.Success(State));

        public Task<Result> SetVolumeAsync(float scalar, CancellationToken ct)
        {
            if (WriteFailure is not null)
                return Task.FromResult(Result.Failure(WriteFailure));
            Levels.Add(scalar);
            State = new VolumeState(scalar, State.IsMuted, State.Capability);
            _changes.Publish(State);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetMuteAsync(bool muted, CancellationToken ct)
        {
            if (WriteFailure is not null)
                return Task.FromResult(Result.Failure(WriteFailure));
            State = new VolumeState(State.Scalar, muted, State.Capability);
            _changes.Publish(State);
            return Task.FromResult(Result.Success());
        }

        public void Raise(VolumeState state)
        {
            State = state;
            _changes.Publish(state);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        public ScriptedVolume Volume { get; } = new();

        public FakeWifiService Wifi { get; } = new();

        public FakeBluetoothService Bluetooth { get; } = new();

        public FakeLocaleClock Clock { get; } = new();

        public FakeLauncher Launcher { get; } = new();

        public SystemPageViewModel Model { get; }

        // Stands in for the dispatcher: port callbacks arrive on other threads, and like the real page the model only
        // changes when the "UI thread" (the test) runs what was posted.
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

        public Rig()
        {
            Model = new SystemPageViewModel(Volume, Wifi, Bluetooth, Clock, Launcher, _posted.Enqueue);
        }

        public void Flush()
        {
            while (_posted.TryDequeue(out var work))
                work();
        }

        public async Task<Rig> StartAsync()
        {
            await Model.InitializeAsync(TestContext.Current.CancellationToken);
            Flush();
            return this;
        }

        public void Dispose() => Model.Dispose();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000, Func<string>? describe = null, Rig? rig = null)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            rig?.Flush();
            if (condition() || DateTime.UtcNow >= end)
                break;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not reached in time. " + describe?.Invoke());
    }

    private static async Task Run(Rig rig, System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await WaitFor(() => command is not AsyncRelayCommand { IsRunning: true }, rig: rig);
        rig.Flush();
    }

    // ---- start -------------------------------------------------------------------------------------------------------

    [Fact]
    public void BeforeTheFirstReadingNothingIsLoadedAndNothingIsClaimed()
    {
        using var rig = new Rig();

        Assert.False(rig.Model.IsVolumeLoaded);
        Assert.False(rig.Model.IsVolumeAvailable);
        Assert.False(rig.Model.Wifi.IsLoaded);
        Assert.False(rig.Model.Bluetooth.IsLoaded);
        Assert.False(rig.Model.Wifi.ToggleCommand.CanExecute(null));
        Assert.False(rig.Model.ToggleMuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeFillsEveryCard()
    {
        using var rig = await new Rig().StartAsync();

        Assert.True(rig.Model.IsVolumeLoaded);
        Assert.True(rig.Model.IsVolumeAvailable);
        Assert.Equal(50, rig.Model.VolumePercent);
        Assert.Equal("50%", rig.Model.VolumeText);
        Assert.True(rig.Model.Wifi.IsLoaded);
        Assert.Equal("On, not connected", rig.Model.Wifi.StatusText);
        Assert.Equal("On", rig.Model.Bluetooth.StatusText);
        Assert.Equal("2:05 PM", rig.Model.TimeText);
        Assert.Equal(1, rig.Clock.Refreshes);
    }

    [Fact]
    public async Task InitializeRunsOnlyOnce()
    {
        using var rig = await new Rig().StartAsync();
        rig.Wifi.State = rig.Wifi.State with { Radio = RadioState.Disabled };

        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.Equal("On, not connected", rig.Model.Wifi.StatusText); // not read again
    }

    // ---- volume ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MovingTheSliderWritesTheLevelAndKeepsWhereTheHandPutIt()
    {
        using var rig = await new Rig().StartAsync();

        rig.Model.VolumePercent = 30;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.Equal([0.3f], rig.Volume.Levels);
        Assert.Equal(30, rig.Model.VolumePercent);
        Assert.Equal("30%", rig.Model.VolumeText);
    }

    [Fact]
    public async Task ManyQuickMovesEndOnTheNewestLevel()
    {
        using var rig = await new Rig().StartAsync();

        for (var i = 1; i <= 100; i++)
            rig.Model.VolumePercent = i;
        await rig.Model.PendingWrite;
        rig.Flush();
        await WaitFor(() => rig.Volume.State.Scalar > 0.99f, 3000, () => $"service {rig.Volume.State.Scalar}, page {rig.Model.VolumePercent}", rig);

        Assert.Equal(1f, rig.Volume.State.Scalar);
        Assert.Equal(100, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task OutOfRangeAndNotANumberAreClampedOrIgnored()
    {
        using var rig = await new Rig().StartAsync();

        rig.Model.VolumePercent = 250;
        await rig.Model.PendingWrite;
        rig.Flush();
        Assert.Equal(100, rig.Model.VolumePercent);

        rig.Model.VolumePercent = double.NaN;
        Assert.Equal(100, rig.Model.VolumePercent);

        rig.Model.VolumePercent = -5;
        await rig.Model.PendingWrite;
        rig.Flush();
        Assert.Equal(0, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task AnOutsideChangeMovesTheSliderAndTheMuteState()
    {
        using var rig = await new Rig().StartAsync();

        rig.Volume.Raise(new VolumeState(0.8f, true, CapabilityStatus.Supported));
        rig.Flush(); // keyboard keys, another app
        await WaitFor(() => rig.Model.IsMuted, rig: rig);

        Assert.Equal(80, rig.Model.VolumePercent);
        Assert.Equal("Muted", rig.Model.VolumeText);
        Assert.Equal("Unmute", rig.Model.MuteLabel);
    }

    [Fact]
    public async Task ReceivingAnOutsideChangeDoesNotWriteAnything()
    {
        using var rig = await new Rig().StartAsync();

        rig.Volume.Raise(new VolumeState(0.8f, false, CapabilityStatus.Supported));
        rig.Flush();

        Assert.Empty(rig.Volume.Levels);
    }

    [Fact]
    public async Task MuteIsShownAtOnceAndConfirmed()
    {
        using var rig = await new Rig().StartAsync();

        await Run(rig, rig.Model.ToggleMuteCommand);

        Assert.True(rig.Model.IsMuted);
        Assert.True(rig.Volume.State.IsMuted);
        Assert.Equal("Unmute", rig.Model.MuteLabel);
        Assert.Equal("Muted", rig.Model.VolumeText);
    }

    [Fact]
    public async Task ARefusedMuteSnapsBackAndSaysSo()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.WriteFailure = new OperationError(ErrorCode.AccessDenied, "no");

        await Run(rig, rig.Model.ToggleMuteCommand);

        Assert.False(rig.Model.IsMuted);
        Assert.NotNull(rig.Model.VolumeError);
        Assert.DoesNotContain("no", rig.Model.VolumeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedLevelSnapsTheSliderBack()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.WriteFailure = new OperationError(ErrorCode.DeviceUnavailable, "gone");

        rig.Model.VolumePercent = 90;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.Equal(50, rig.Model.VolumePercent);
        Assert.NotNull(rig.Model.VolumeError);
        rig.Model.DismissVolumeErrorCommand.Execute(null);
        Assert.Null(rig.Model.VolumeError);
    }

    [Theory]
    [InlineData(CapabilityStatus.Unavailable, "No audio output device")]
    [InlineData(CapabilityStatus.Unsupported, "Volume can't be controlled on this PC")]
    [InlineData(CapabilityStatus.PermissionDenied, "Windows didn't allow access to the volume")]
    [InlineData(CapabilityStatus.Unknown, "Volume is unavailable")]
    public async Task EveryUnusableVolumeStateSaysWhyAndDisablesTheControls(CapabilityStatus capability, string note)
    {
        using var rig = new Rig();
        rig.Volume.State = new VolumeState(0f, false, capability);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.True(rig.Model.IsVolumeLoaded);
        Assert.False(rig.Model.IsVolumeAvailable);
        Assert.Equal(note, rig.Model.VolumeNote);
        Assert.False(rig.Model.ToggleMuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDeviceComingBackEnablesTheControls()
    {
        using var rig = new Rig();
        rig.Volume.State = VolumeState.Unavailable;
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();
        Assert.False(rig.Model.IsVolumeAvailable);

        rig.Volume.Raise(new VolumeState(0.4f, false, CapabilityStatus.Supported));
        rig.Flush();

        Assert.True(rig.Model.IsVolumeAvailable);
        Assert.Null(rig.Model.VolumeNote);
        Assert.Equal(40, rig.Model.VolumePercent);
        Assert.True(rig.Model.ToggleMuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task AVolumeThatCannotBeReadShowsUnknownWithAnError()
    {
        using var rig = new Rig();
        rig.Volume.ReadFailure = new OperationError(ErrorCode.Unexpected, "boom", retryable: true);

        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.True(rig.Model.IsVolumeLoaded);
        Assert.False(rig.Model.IsVolumeAvailable);
        Assert.Equal("Volume is unavailable", rig.Model.VolumeNote);
        Assert.Equal("Couldn't read the volume.", rig.Model.VolumeError);
    }

    // ---- wifi and bluetooth ------------------------------------------------------------------------------------------

    [Fact]
    public async Task WifiConnectedShowsConnectedAndOffersTurnOff()
    {
        using var rig = new Rig();
        rig.Wifi.State = new WifiState(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Connected, true);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.Equal("Connected", rig.Model.Wifi.StatusText);
        Assert.Equal("Turn off", rig.Model.Wifi.ActionText);
        Assert.True(rig.Model.Wifi.CanToggle);
        Assert.False(rig.Model.Wifi.ShowSettingsLink);
    }

    [Fact]
    public async Task TheSwitchWritesTheOppositeAndTheCardFollows()
    {
        using var rig = await new Rig().StartAsync();

        await Run(rig, rig.Model.Wifi.ToggleCommand);

        Assert.Equal(RadioState.Disabled, rig.Wifi.State.Radio);
        Assert.Equal("Off", rig.Model.Wifi.StatusText);
        Assert.Equal("Turn on", rig.Model.Wifi.ActionText);
        Assert.Null(rig.Model.Wifi.Error);
    }

    [Fact]
    public async Task BluetoothSwitchWorksTheSameWay()
    {
        using var rig = await new Rig().StartAsync();

        await Run(rig, rig.Model.Bluetooth.ToggleCommand);

        Assert.Equal(RadioState.Disabled, rig.Bluetooth.State.Radio);
        Assert.Equal("Off", rig.Model.Bluetooth.StatusText);
    }

    [Fact]
    public async Task ARefusedSwitchExplainsAndPointsToSettings()
    {
        using var rig = new Rig();
        rig.Bluetooth.State = new BluetoothState(CapabilityStatus.Supported, RadioState.Enabled, CanToggle: false);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.False(rig.Model.Bluetooth.CanToggle);
        Assert.True(rig.Model.Bluetooth.ShowSettingsLink);
        Assert.False(rig.Model.Bluetooth.ToggleCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(CapabilityStatus.Unsupported, RadioState.Unavailable, "Not supported", true)]
    [InlineData(CapabilityStatus.PermissionDenied, RadioState.Unknown, "Access denied", true)]
    [InlineData(CapabilityStatus.Unknown, RadioState.Unknown, "Unknown", true)]
    [InlineData(CapabilityStatus.Unavailable, RadioState.Unavailable, "No Wi-Fi adapter", false)]
    public async Task EveryUnusableWifiStateHasItsOwnWordsAndNoSwitch(CapabilityStatus capability, RadioState radio, string text, bool settings)
    {
        using var rig = new Rig();
        rig.Wifi.State = new WifiState(capability, radio, WifiConnectivity.Unknown, false);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.True(rig.Model.Wifi.IsLoaded);
        Assert.Equal(text, rig.Model.Wifi.StatusText);
        Assert.False(rig.Model.Wifi.CanToggle);
        Assert.Equal(settings, rig.Model.Wifi.ShowSettingsLink);
    }

    [Fact]
    public async Task ARadioSwitchRefusedByWindowsShowsAnErrorThatCanBeDismissed()
    {
        using var rig = new Rig();
        rig.Wifi.State = rig.Wifi.State with { CanToggle = true };
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();
        rig.Wifi.State = rig.Wifi.State with { CanToggle = false }; // the service now refuses, the page has not heard yet

        await Run(rig, rig.Model.Wifi.ToggleCommand);

        Assert.NotNull(rig.Model.Wifi.Error);
        rig.Model.Wifi.DismissErrorCommand.Execute(null);
        Assert.Null(rig.Model.Wifi.Error);
    }

    [Fact]
    public async Task ASettingsLinkOpensTheRightPage()
    {
        using var rig = await new Rig().StartAsync();

        await Run(rig, rig.Model.Wifi.OpenSettingsCommand);
        await Run(rig, rig.Model.Bluetooth.OpenSettingsCommand);

        Assert.Equal([SettingsPage.Wifi, SettingsPage.Bluetooth], rig.Launcher.Opened);
    }

    [Fact]
    public async Task ASettingsPageThatWillNotOpenIsReported()
    {
        using var rig = await new Rig().StartAsync();
        rig.Launcher.Next = Result.Failure(new OperationError(ErrorCode.IoFailure, "x", retryable: true));

        await Run(rig, rig.Model.Wifi.OpenSettingsCommand);

        Assert.Equal("Couldn't open Settings.", rig.Model.Wifi.Error);
    }

    [Fact]
    public async Task ThePageShowsOneErrorAtATimeAndDismissingClearsThemAll()
    {
        using var rig = new Rig();
        rig.Volume.ReadFailure = new OperationError(ErrorCode.Unexpected, "x", retryable: true);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();
        rig.Model.Wifi.ShowFault("Couldn't read the Wi-Fi state.");
        var changes = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal("Couldn't read the volume.", rig.Model.ErrorMessage); // volume first
        rig.Model.DismissErrorCommand.Execute(null);

        Assert.Null(rig.Model.VolumeError);
        Assert.Null(rig.Model.Wifi.Error);
        Assert.Null(rig.Model.ErrorMessage);
        Assert.Contains(nameof(SystemPageViewModel.ErrorMessage), changes);
    }

    [Fact]
    public async Task ACardErrorReachesThePageErrorLine()
    {
        using var rig = await new Rig().StartAsync();
        var changes = new List<string?>();
        rig.Model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        rig.Model.Bluetooth.ShowFault("Couldn't read the Bluetooth state.");

        Assert.Equal("Couldn't read the Bluetooth state.", rig.Model.ErrorMessage);
        Assert.Contains(nameof(SystemPageViewModel.ErrorMessage), changes);
    }

    // ---- adjusting (FE-14) -------------------------------------------------------------------------------------------

    private static VolumeState Level(float scalar, bool muted = false) => new(scalar, muted, CapabilityStatus.Supported);

    [Fact]
    public async Task AnOutsideLevelWaitsWhileTheHandIsOnTheSliderAndAppliesWhenItLetsGo()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;

        rig.Volume.Raise(Level(0.9f));
        rig.Flush();

        Assert.Equal(50, rig.Model.VolumePercent); // not moved under the thumb
        rig.Model.IsAdjusting = false;
        Assert.Equal(90, rig.Model.VolumePercent); // the user did not move it, so the outside level wins now
    }

    [Fact]
    public async Task IfTheUserMovedTheSliderTheirLevelWinsAndTheOutsideOneIsDropped()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;
        rig.Model.VolumePercent = 20;
        rig.Volume.Raise(Level(0.9f)); // an outside change during the drag
        rig.Flush();
        await rig.Model.PendingWrite;
        rig.Flush();

        rig.Model.IsAdjusting = false;

        Assert.Equal(20, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task OnlyTheLatestHeldBackLevelIsApplied()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;

        rig.Volume.Raise(Level(0.7f));
        rig.Volume.Raise(Level(0.3f));
        rig.Flush();
        rig.Model.IsAdjusting = false;

        Assert.Equal(30, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task AnOutsideMuteShowsAtOnceEvenDuringADrag()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;

        rig.Volume.Raise(Level(0.9f, muted: true));
        rig.Flush();

        Assert.True(rig.Model.IsMuted);
        Assert.Equal(50, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task ARefusedWriteSnapsBackEvenDuringADrag()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.WriteFailure = new OperationError(ErrorCode.DeviceUnavailable, "gone");
        rig.Model.IsAdjusting = true;

        rig.Model.VolumePercent = 90;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.Equal(50, rig.Model.VolumePercent);
        Assert.NotNull(rig.Model.VolumeError);
    }

    [Fact]
    public async Task LosingTheDeviceMidDragAppliesAtOnce()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;

        rig.Volume.Raise(VolumeState.Unavailable);
        rig.Flush();

        Assert.False(rig.Model.IsVolumeAvailable);
        Assert.Equal("No audio output device", rig.Model.VolumeNote);
    }

    [Fact]
    public async Task AHeldBackLevelIsForgottenOnceTheUserHasMoved()
    {
        using var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;
        rig.Volume.Raise(Level(0.9f));
        rig.Flush();
        rig.Model.VolumePercent = 40; // moved, so on release the held level is dropped
        await rig.Model.PendingWrite;
        rig.Flush();
        rig.Model.IsAdjusting = false;

        rig.Model.IsAdjusting = true;
        rig.Model.IsAdjusting = false; // a touch without a move must not bring the old outside level back

        Assert.Equal(40, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task AStormOfOutsideChangesNeverMovesTheSliderUnderTheHand()
    {
        using var rig = await new Rig().StartAsync();
        var seen = new List<double>();
        rig.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemPageViewModel.VolumePercent))
                seen.Add(rig.Model.VolumePercent);
        };
        rig.Model.IsAdjusting = true;

        for (var i = 1; i <= 60; i++)
        {
            rig.Model.VolumePercent = i;
            rig.Volume.Raise(Level(((i * 7) % 100) / 100f)); // someone else keeps changing it
            rig.Flush();
        }

        await rig.Model.PendingWrite;
        rig.Flush();
        rig.Model.IsAdjusting = false;

        Assert.Equal(Enumerable.Range(1, 60).Select(i => (double)i), seen); // only the hand moved it, in order
        Assert.Equal(60, rig.Model.VolumePercent);
    }

    [Fact]
    public async Task MovingTheSliderWhileMutedUnmutes()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.Raise(Level(0.5f, muted: true));
        rig.Flush();
        Assert.True(rig.Model.IsMuted);

        rig.Model.VolumePercent = 35;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.False(rig.Model.IsMuted);
        Assert.False(rig.Volume.State.IsMuted);
        Assert.Equal(0.35f, rig.Volume.State.Scalar, precision: 3);
    }

    [Fact]
    public async Task MovingToZeroWhileMutedStaysMuted()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.Raise(Level(0.5f, muted: true));
        rig.Flush();

        rig.Model.VolumePercent = 0;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.True(rig.Model.IsMuted);
        Assert.True(rig.Volume.State.IsMuted);
    }

    [Fact]
    public async Task MovingTheSliderWhileNotMutedNeverTouchesMute()
    {
        using var rig = await new Rig().StartAsync();

        rig.Model.VolumePercent = 80;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.False(rig.Model.IsMuted);
        Assert.False(rig.Volume.State.IsMuted);
    }

    [Fact]
    public async Task ARefusedUnmuteSnapsBackAndSaysSo()
    {
        using var rig = await new Rig().StartAsync();
        rig.Volume.Raise(Level(0.5f, muted: true));
        rig.Flush();
        rig.Volume.WriteFailure = new OperationError(ErrorCode.AccessDenied, "no");

        rig.Model.VolumePercent = 70;
        await rig.Model.PendingWrite;
        rig.Flush();

        Assert.True(rig.Model.IsMuted);
        Assert.Equal(50, rig.Model.VolumePercent);
        Assert.NotNull(rig.Model.VolumeError);
    }

    [Fact]
    public async Task AdjustingRaisesItsChangeOncePerChange()
    {
        using var rig = await new Rig().StartAsync();
        var changes = 0;
        rig.Model.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(SystemPageViewModel.IsAdjusting) ? 1 : 0;

        rig.Model.IsAdjusting = true;
        rig.Model.IsAdjusting = true;
        rig.Model.IsAdjusting = false;
        rig.Model.IsAdjusting = false;

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ReleasingAfterDisposeDoesNothing()
    {
        var rig = await new Rig().StartAsync();
        rig.Model.IsAdjusting = true;
        rig.Volume.Raise(Level(0.9f));
        rig.Flush();

        rig.Dispose();
        rig.Model.IsAdjusting = false;

        Assert.Equal(50, rig.Model.VolumePercent);
    }

    // ---- clock -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheClockFollowsTheRegionalFormat()
    {
        using var rig = new Rig();
        rig.Clock.Culture = CultureInfo.GetCultureInfo("de-DE");
        rig.Clock.Now = new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

        rig.Model.RefreshClock();

        Assert.Equal("14:05", rig.Model.TimeText);
        Assert.Contains("2026", rig.Model.DateText, StringComparison.Ordinal);
        Assert.Equal(1, rig.Clock.Refreshes);
    }

    [Fact]
    public void RefreshingTheClockPicksUpTheNewTime()
    {
        using var rig = new Rig();
        rig.Model.RefreshClock();
        var before = rig.Model.TimeText;

        rig.Clock.Now = rig.Clock.Now.AddHours(3);
        rig.Model.RefreshClock();

        Assert.NotEqual(before, rig.Model.TimeText);
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterDisposeNothingChangesAndDisposeIsIdempotent()
    {
        var rig = await new Rig().StartAsync();
        var percent = rig.Model.VolumePercent;

        rig.Dispose();
        rig.Dispose();
        rig.Volume.Raise(new VolumeState(0.9f, false, CapabilityStatus.Supported));
        rig.Flush();

        Assert.Equal(percent, rig.Model.VolumePercent);
        Assert.False(rig.Model.ToggleMuteCommand.CanExecute(null));
    }

    [Fact]
    public void ConstructorRejectsMissingParts()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentNullException>(() => new SystemPageViewModel(null!, rig.Wifi, rig.Bluetooth, rig.Clock, rig.Launcher, w => w()));
        Assert.Throws<ArgumentNullException>(() => new SystemPageViewModel(rig.Volume, rig.Wifi, rig.Bluetooth, rig.Clock, rig.Launcher, null!));
        rig.Dispose();
    }

    // ---- the command -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AsyncCommandRunsOnceAtATimeAndReportsRunning()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncRelayCommand(async _ =>
        {
            runs++;
            await gate.Task;
        });

        command.Execute(null);
        command.Execute(null); // ignored while running

        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        gate.SetResult();
        await WaitFor(() => !command.IsRunning);
        Assert.Equal(1, runs);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncCommandHandsAFaultToTheHandlerInsteadOfCrashing()
    {
        Exception? seen = null;
        var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("secret"), onFault: ex => seen = ex);

        command.Execute(null);
        await WaitFor(() => !command.IsRunning && seen is not null);

        Assert.IsType<InvalidOperationException>(seen);
    }

    [Fact]
    public async Task AsyncCommandSwallowsCancellationAndStopsAfterShutdown()
    {
        using var cts = new CancellationTokenSource();
        Exception? seen = null;
        var command = new AsyncRelayCommand(ct => Task.FromCanceled(new CancellationToken(true)), onFault: ex => seen = ex, lifetime: cts.Token);

        command.Execute(null);
        await WaitFor(() => !command.IsRunning);
        Assert.Null(seen);

        await cts.CancelAsync();
        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public void AsyncCommandHonoursItsCanExecuteGate()
    {
        var allowed = false;
        var ran = false;
        var command = new AsyncRelayCommand(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, () => allowed);

        command.Execute(null);

        Assert.False(ran);
        allowed = true;
        Assert.True(command.CanExecute(null));
    }
}

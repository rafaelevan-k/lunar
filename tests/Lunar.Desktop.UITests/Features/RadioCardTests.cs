using System.Globalization;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-15: the switch never claims a result before Windows confirmed it. The services here are gated fakes whose answers the
// test controls, so every moment between "pressed" and "confirmed" can be examined.
public sealed class RadioCardTests
{
    /// <summary>A Wi-Fi service that answers a switch request only when the test says so, and can publish or stay silent.</summary>
    private sealed class GatedWifi : IWifiService
    {
        private readonly TestObservable<WifiState> _changes = new();
        private TaskCompletionSource<Result>? _gate;

        public WifiState State { get; set; } = new(CapabilityStatus.Supported, RadioState.Enabled, WifiConnectivity.Connected, true);

        public List<bool> Requests { get; } = [];

        public bool PublishOnChange { get; set; } = true;

        public IObservable<WifiState> Changes => _changes;

        public Task<Result<WifiState>> GetAsync(CancellationToken ct) => Task.FromResult(Result.Success(State));

        public Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            Requests.Add(enabled);
            _gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _gate.Task;
        }

        /// <summary>The service reports a change on its own (someone else switched or connected something).</summary>
        public void Publish(WifiState state)
        {
            State = state;
            _changes.Publish(state);
        }

        /// <summary>Windows answered: the radio changed (or not) and the request ended with <paramref name="result"/>.</summary>
        public void Answer(Result result, RadioState? radioNow = null, bool? canToggle = null)
        {
            if (radioNow is { } radio || canToggle is not null)
            {
                State = State with { Radio = radioNow ?? State.Radio, CanToggle = canToggle ?? State.CanToggle };
                if (PublishOnChange)
                    _changes.Publish(State);
            }

            _gate!.SetResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLocaleClock : ILocaleClock
    {
        public DateTimeOffset LocalNow { get; } = new(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

        public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

        public void Refresh()
        {
        }
    }

    private sealed class Launcher : ISystemSettingsLauncher
    {
        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class Rig : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

        public GatedWifi Wifi { get; } = new();

        public FakeBluetoothService Bluetooth { get; } = new();

        public SystemPageViewModel Model { get; }

        public Rig()
        {
            Model = new SystemPageViewModel(new FakeVolumeService(), Wifi, Bluetooth, new FakeLocaleClock(), new Launcher(), _posted.Enqueue);
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

    private static void Press(Rig rig, System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        rig.Flush();
    }

    private static async Task Until(Rig rig, Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            rig.Flush();
            if (condition() || DateTime.UtcNow >= end)
                break;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not reached in time.");
    }

    // ---- never claim before Windows confirms --------------------------------------------------------------------------

    [Fact]
    public async Task PressingTheSwitchOnlyAsksAndTheCardKeepsTheRealStateWhileWaiting()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Assert.True(card.IsOn);

        Press(rig, card.ToggleCommand);

        Assert.Equal([false], rig.Wifi.Requests);
        Assert.True(card.IsOn); // not flipped: Windows has not answered
        Assert.True(card.IsSwitching);
        Assert.Equal("Turning off…", card.ShownStatusText);
        Assert.Equal(StatusKind.Info, card.ShownStatusKind); // in progress is not a success
        Assert.Equal("Connected", card.StatusText); // the confirmed words are still the real ones
        Assert.False(card.IsSwitchEnabled);
        Assert.False(card.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheCardChangesOnlyOnceWindowsConfirms()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Answer(Result.Success(), RadioState.Disabled);
        await Until(rig, () => !card.IsSwitching);

        Assert.False(card.IsOn);
        Assert.Equal("Off", card.ShownStatusText);
        Assert.Equal(StatusKind.Neutral, card.ShownStatusKind);
        Assert.Equal("Turn on", card.ActionText);
        Assert.Equal("Turn on Wi-Fi", card.SwitchLabel);
        Assert.True(card.IsSwitchEnabled);
        Assert.Null(card.Error);
    }

    [Fact]
    public async Task ASuccessThatWindowsNeverPublishedIsStillReadBack()
    {
        using var rig = await new Rig().StartAsync();
        rig.Wifi.PublishOnChange = false; // the service confirms through its result but sends no change
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Answer(Result.Success(), RadioState.Disabled);
        await Until(rig, () => !card.IsSwitching);

        Assert.False(card.IsOn); // the read after the request found the real state
        Assert.Equal("Off", card.StatusText);
    }

    [Fact]
    public async Task ASuccessResultDoesNotMakeTheCardClaimWhatTheRadioDidNotDo()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Answer(Result.Success()); // "ok", yet the radio is still on
        await Until(rig, () => !card.IsSwitching);

        Assert.True(card.IsOn);
        Assert.Equal("Connected", card.ShownStatusText);
    }

    [Fact]
    public async Task ARefusedRequestLeavesTheSwitchWhereTheRadioIsExplainsAndOffersSettings()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Answer(Result.Failure(new OperationError(ErrorCode.AccessDenied, "denied")), canToggle: false);
        await Until(rig, () => !card.IsSwitching);

        Assert.True(card.IsOn);
        Assert.False(card.CanToggle);
        Assert.True(card.ShowSettingsLink);
        Assert.Equal("Can't be switched from here.", card.Hint);
        Assert.Equal("Windows didn't allow this change. Use Settings to switch Wi-Fi.", card.Error);
        Assert.Equal("Connected", card.ShownStatusText);
    }

    [Fact]
    public async Task ATimeoutNeverClaimsSuccessAndCanBeTriedAgain()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Answer(Result.Failure(new OperationError(ErrorCode.Timeout, "slow", retryable: true)));
        await Until(rig, () => !card.IsSwitching);

        Assert.True(card.IsOn);
        Assert.Equal("Windows didn't confirm the change. Try again.", card.Error);
        Assert.True(card.IsSwitchEnabled);

        Press(rig, card.ToggleCommand); // the retry asks again
        Assert.Equal([false, false], rig.Wifi.Requests);
        Assert.Null(card.Error); // the old error goes as soon as a new request starts
    }

    [Fact]
    public async Task AnOutsideChangeWhileWaitingUpdatesTheConfirmedStateButNotTheProgressWords()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);

        rig.Wifi.Publish(rig.Wifi.State with { Connectivity = WifiConnectivity.Disconnected });
        rig.Flush();

        Assert.Equal("On, not connected", card.StatusText);
        Assert.Equal("Turning off…", card.ShownStatusText); // still waiting for the answer
        Assert.True(card.IsOn);
    }

    [Fact]
    public async Task WhileSwitchingTheWordsDoNotFlipWhenTheStateArrivesBeforeTheAnswer()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Wifi;
        Press(rig, card.ToggleCommand);
        Assert.Equal("Turning off…", card.ShownStatusText);

        // The service publishes the confirmed state a moment before its call returns.
        rig.Wifi.State = rig.Wifi.State with { Radio = RadioState.Disabled };
        rig.Wifi.Answer(Result.Success(), RadioState.Disabled);

        Assert.NotEqual("Turning on…", card.ShownStatusText); // never the opposite of what was asked
        await Until(rig, () => !card.IsSwitching);
        Assert.Equal("Off", card.ShownStatusText);
    }

    [Fact]
    public async Task BluetoothFollowsTheSameRules()
    {
        using var rig = await new Rig().StartAsync();
        var card = rig.Model.Bluetooth;

        Press(rig, card.ToggleCommand);
        await Until(rig, () => !card.IsSwitching);

        Assert.False(card.IsOn);
        Assert.Equal("Off", card.ShownStatusText);
        Assert.Equal("Turn on Bluetooth", card.SwitchLabel);
    }

    [Fact]
    public async Task ThereIsNoSwitchBeforeTheFirstReadingOrWhenWindowsDoesNotAllowIt()
    {
        using var rig = new Rig();
        Assert.False(rig.Model.Wifi.IsSwitchEnabled);

        rig.Wifi.State = new WifiState(CapabilityStatus.Supported, RadioState.Disabled, WifiConnectivity.Disconnected, CanToggle: false);
        await rig.Model.InitializeAsync(TestContext.Current.CancellationToken);
        rig.Flush();

        Assert.False(rig.Model.Wifi.IsSwitchEnabled);
        Assert.False(rig.Model.Wifi.ToggleCommand.CanExecute(null));
        Assert.True(rig.Model.Wifi.ShowSettingsLink);
    }

    [Fact]
    public async Task ThePressedSwitchIsIgnoredWhileARequestIsOut()
    {
        using var rig = await new Rig().StartAsync();
        Press(rig, rig.Model.Wifi.ToggleCommand);

        Press(rig, rig.Model.Wifi.ToggleCommand);
        Press(rig, rig.Model.Wifi.ToggleCommand);

        Assert.Single(rig.Wifi.Requests);
    }

    // ---- words for every state ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CapabilityStatus.Unsupported, RadioState.Unavailable, false, "This PC doesn't allow controlling it.")]
    [InlineData(CapabilityStatus.PermissionDenied, RadioState.Unknown, false, "Windows didn't allow access.")]
    [InlineData(CapabilityStatus.Unknown, RadioState.Unknown, false, "The state couldn't be determined.")]
    [InlineData(CapabilityStatus.Supported, RadioState.Unknown, false, "The state couldn't be determined.")]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, false, "Can't be switched from here.")]
    [InlineData(CapabilityStatus.Supported, RadioState.Disabled, false, "Can't be switched from here.")]
    [InlineData(CapabilityStatus.Supported, RadioState.Enabled, true, null)]
    [InlineData(CapabilityStatus.Supported, RadioState.Disabled, true, null)]
    [InlineData(CapabilityStatus.Unavailable, RadioState.Unavailable, false, null)]
    public void HintsExplainWhyThereIsNoSwitch(CapabilityStatus capability, RadioState radio, bool canToggle, string? hint)
    {
        Assert.Equal(hint, SystemPageText.WifiHint(new WifiState(capability, radio, WifiConnectivity.Unknown, canToggle)));
        Assert.Equal(hint, SystemPageText.BluetoothHint(new BluetoothState(capability, radio, canToggle)));
    }

    [Fact]
    public void ProgressWordsSayWhatWasAskedNotWhatHappened()
    {
        Assert.Equal("Turning on…", SystemPageText.Switching(true));
        Assert.Equal("Turning off…", SystemPageText.Switching(false));
    }

    // ---- no fake scan UI ---------------------------------------------------------------------------------------------

    [Fact]
    public void NothingOnTheCardScansListsNetworksOrNamesANetwork()
    {
        var folder = System.IO.Path.Combine(Themes.ThemeTokenTests.DesktopDirectory, "Features", "System");
        foreach (var file in Directory.GetFiles(folder))
        {
            var text = File.ReadAllText(file);
            text = System.Text.RegularExpressions.Regex.Replace(text, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            var code = string.Join(Environment.NewLine, text.Split(Environment.NewLine).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            var found = System.Text.RegularExpressions.Regex.Match(code, @"(scan|scans|ssid|networks|networklist|paired|devices)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Assert.False(found.Success, $"{System.IO.Path.GetFileName(file)} contains {found.Value}");
        }
    }
}

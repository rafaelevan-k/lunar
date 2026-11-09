using System.Runtime.InteropServices;
using Lunar.Domain.Shared;
using Lunar.Windows.Focus;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Focus;

public sealed class WindowsFocusModeControlTests
{
    private const string Priority = "Microsoft.QuietHoursProfile.PriorityOnly";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WindowsFocusModeControl Control(FakeBackend backend, int attempts = 3) => new(backend, attempts, TimeSpan.Zero);

    [Fact]
    public async Task ReadsOffAndOnWithATokenThatRestoresIt()
    {
        var backend = new FakeBackend();
        var off = await Control(backend).ReadAsync(Ct);
        Assert.False(off.Value.IsOn);
        Assert.Equal(WindowsFocusModeControl.Off, off.Value.RestoreToken);

        backend.Profile = Priority;
        var on = await Control(backend).ReadAsync(Ct);
        Assert.True(on.Value.IsOn);
        Assert.Equal(Priority, on.Value.RestoreToken);
    }

    [Fact]
    public async Task TurningOnSucceedsOnlyOnceWindowsAnnouncesIt()
    {
        var backend = new FakeBackend { AnnounceAfterReads = 2 };

        var result = await Control(backend, attempts: 5).TurnOnAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(WindowsFocusModeControl.AlarmsOnly, backend.Profile);
        Assert.Equal(1, backend.Writes);
    }

    [Fact]
    public async Task IfWindowsNeverAnnouncesItTheEarlierStateIsPutBackAndNothingIsClaimed()
    {
        var backend = new FakeBackend { Profile = Priority, AnnounceAfterReads = int.MaxValue };

        var result = await Control(backend).TurnOnAsync(Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
        Assert.Equal(Priority, backend.Profile);
    }

    [Fact]
    public async Task IfTheProfileDoesNotStickNothingIsClaimed()
    {
        var backend = new FakeBackend { IgnoreWrites = true };

        var result = await Control(backend).TurnOnAsync(Ct);

        Assert.Equal(ErrorCode.Timeout, result.Error!.Code);
    }

    [Fact]
    public async Task AlreadyAlarmsOnlyIsNotWrittenAgain()
    {
        var backend = new FakeBackend { Profile = WindowsFocusModeControl.AlarmsOnly };

        Assert.True((await Control(backend).TurnOnAsync(Ct)).IsSuccess);
        Assert.Equal(0, backend.Writes);
    }

    [Fact]
    public async Task RestorePutsBackExactlyWhatWasThere()
    {
        var backend = new FakeBackend { Profile = WindowsFocusModeControl.AlarmsOnly };

        Assert.True((await Control(backend).RestoreAsync(Priority, Ct)).IsSuccess);
        Assert.Equal(Priority, backend.Profile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Something.Else")]
    public async Task ATokenThatIsNotAProfileIsRefusedAndNothingIsWritten(string token)
    {
        var backend = new FakeBackend();

        Assert.Equal(ErrorCode.InvalidInput, (await Control(backend).RestoreAsync(token, Ct)).Error!.Code);
        Assert.Equal(0, backend.Writes);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040154), ErrorCode.Unsupported)]
    [InlineData(unchecked((int)0x80004002), ErrorCode.Unsupported)]
    [InlineData(unchecked((int)0x80070005), ErrorCode.AccessDenied)]
    [InlineData(unchecked((int)0x80004005), ErrorCode.Unexpected)]
    public async Task WindowsRefusalsAreTypedResultsNeverExceptions(int hresult, ErrorCode expected)
    {
        var backend = new FakeBackend { Throw = hresult };
        var control = Control(backend);

        Assert.Equal(expected, (await control.ReadAsync(Ct)).Error!.Code);
        Assert.Equal(expected, (await control.TurnOnAsync(Ct)).Error!.Code);
        Assert.Equal(expected, (await control.RestoreAsync(Priority, Ct)).Error!.Code);
    }

    [Fact]
    public async Task ACancelledCallIsTyped()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Equal(ErrorCode.Cancelled, (await Control(new FakeBackend()).TurnOnAsync(cancelled.Token)).Error!.Code);
    }

    [Fact]
    public async Task CancellingWhileWaitingForWindowsPutsTheEarlierStateBack()
    {
        var backend = new FakeBackend { Profile = Priority, AnnounceAfterReads = int.MaxValue };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var control = new WindowsFocusModeControl(backend, confirmAttempts: 1000, confirmDelay: TimeSpan.FromMilliseconds(10));

        var result = await control.TurnOnAsync(cancel.Token);

        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        await WaitUntilAsync(() => backend.Profile == Priority);
        Assert.Equal(Priority, backend.Profile);
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheRealControlReadsThisWindows()
    {
        var state = await new WindowsFocusModeControl().ReadAsync(Ct);

        if (state.IsFailure && state.Error!.Code == ErrorCode.Unsupported)
            Assert.Skip("This Windows has no quiet-hours service.");
        Assert.True(state.IsSuccess, state.Error?.SafeMessage);
        Assert.StartsWith("Microsoft.QuietHoursProfile.", state.Value.RestoreToken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealControlTurnsOnAndRestores()
    {
        if (System.Environment.GetEnvironmentVariable("LUNAR_TEST_FOCUS") != "1")
            Assert.Skip("Changes Windows Do Not Disturb for a few seconds; set LUNAR_TEST_FOCUS=1 to run it.");
        var control = new WindowsFocusModeControl();
        var before = await control.ReadAsync(Ct);
        Assert.True(before.IsSuccess, before.Error?.SafeMessage);
        try
        {
            var on = await control.TurnOnAsync(Ct);
            Assert.True(on.IsSuccess, on.Error?.SafeMessage);
            Assert.True((await control.ReadAsync(Ct)).Value.IsOn);
        }
        finally
        {
            Assert.True((await control.RestoreAsync(before.Value.RestoreToken, CancellationToken.None)).IsSuccess);
        }

        Assert.Equal(before.Value.RestoreToken, (await control.ReadAsync(Ct)).Value.RestoreToken);
    }

    private sealed class FakeBackend : IQuietHoursBackend
    {
        private int _announceReads;

        public string Profile { get; set; } = WindowsFocusModeControl.Off;

        public int Writes { get; private set; }

        public int AnnounceAfterReads { get; init; }

        public bool IgnoreWrites { get; init; }

        public int? Throw { get; init; }

        public string GetProfile()
        {
            ThrowIfAsked();
            return Profile;
        }

        public void SetProfile(string profile)
        {
            ThrowIfAsked();
            Writes++;
            if (!IgnoreWrites)
                Profile = profile;
        }

        public int? ReadAnnouncedProfile() =>
            Profile == WindowsFocusModeControl.AlarmsOnly && ++_announceReads > AnnounceAfterReads ? 2 : 0;

        private void ThrowIfAsked()
        {
            if (Throw is { } hresult)
                throw new COMException("from Windows", hresult);
        }
    }
}

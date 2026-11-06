using Lunar.Application.Abstractions.System;
using Lunar.Desktop.Features.Settings;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-26 wiring of BE-29: the switch the Settings window and the tray share. A fake stands in for Windows; the real registry is covered by the
// Infrastructure tests (under a throwaway key) and the BE-29 smoke tests.
public sealed class StartupSwitchTests
{
    internal sealed class FakeRegistration : IStartupRegistration
    {
        public bool On { get; set; }

        public ErrorCode? ReadFails { get; set; }

        public ErrorCode? ChangeFails { get; set; }

        public int Changes { get; private set; }

        public Result<bool> IsEnabled() =>
            ReadFails is { } code ? Result.Failure<bool>(new OperationError(code, "read")) : Result.Success(On);

        public Result Enable() => Change(true);

        public Result Disable() => Change(false);

        private Result Change(bool on)
        {
            Changes++;
            if (ChangeFails is { } code)
                return Result.Failure(new OperationError(code, "change"));
            On = on;
            return Result.Success();
        }
    }

    [Fact]
    public void WithoutARegistrationItIsUnavailableOffAndRefusesToToggle()
    {
        var startup = new StartupSwitch(null);

        Assert.False(startup.IsAvailable);
        Assert.False(startup.IsOn);
        Assert.Equal(ErrorCode.Unsupported, startup.Toggle().Error!.Code);
    }

    [Fact]
    public void AnUnreadableRegistrationIsUnavailableAndIsNeverChanged()
    {
        var fake = new FakeRegistration { ReadFails = ErrorCode.InvalidInput };
        var startup = new StartupSwitch(fake);

        Assert.False(startup.IsAvailable);
        Assert.True(startup.Toggle().IsFailure);
        Assert.Equal(0, fake.Changes);
    }

    [Fact]
    public void ToggleTurnsItOnThenOffAndShowsWhatWindowsHas()
    {
        var fake = new FakeRegistration();
        var startup = new StartupSwitch(fake);
        var raised = 0;
        startup.Changed += (_, _) => raised++;

        Assert.True(startup.IsAvailable);
        Assert.False(startup.IsOn);

        Assert.True(startup.Toggle().IsSuccess);
        Assert.True(startup.IsOn);
        Assert.True(fake.On);

        Assert.True(startup.Toggle().IsSuccess);
        Assert.False(startup.IsOn);
        Assert.False(fake.On);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ARefusedChangeIsReportedAndTheStateStaysWhatWindowsHas()
    {
        var fake = new FakeRegistration { ChangeFails = ErrorCode.AccessDenied };
        var startup = new StartupSwitch(fake);

        var result = startup.Toggle();

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.False(startup.IsOn);
    }

    [Fact]
    public void RefreshPicksUpAChangeMadeOutsideLunar()
    {
        var fake = new FakeRegistration { On = true };
        var startup = new StartupSwitch(fake);
        Assert.True(startup.IsOn);

        fake.On = false; // switched off in Task Manager
        startup.Refresh();

        Assert.False(startup.IsOn);
    }
}

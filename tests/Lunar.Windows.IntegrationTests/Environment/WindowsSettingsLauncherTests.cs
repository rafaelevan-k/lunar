using System.ComponentModel;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;
using Lunar.Windows.Environment;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Environment;

// The real Settings app is never opened by these tests; the shell call is replaced by a recording starter.
public sealed class WindowsSettingsLauncherTests
{
    [Theory]
    [InlineData(SettingsPage.Wifi, "ms-settings:network-wifi")]
    [InlineData(SettingsPage.Bluetooth, "ms-settings:bluetooth")]
    [InlineData(SettingsPage.DateAndTime, "ms-settings:dateandtime")]
    [InlineData(SettingsPage.Display, "ms-settings:display")]
    [InlineData(SettingsPage.Microphone, "ms-settings:privacy-microphone")]
    [InlineData(SettingsPage.Focus, "ms-settings:notifications")]
    public async Task EachWhitelistedPageOpensItsFixedUri(SettingsPage page, string expectedUri)
    {
        var started = new List<string>();
        var launcher = new WindowsSettingsLauncher(started.Add);

        var result = await launcher.OpenAsync(page, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { expectedUri }, started);
    }

    [Fact]
    public void EveryEnumValueHasAnMsSettingsUriAndNothingElseDoes()
    {
        foreach (var page in Enum.GetValues<SettingsPage>())
            Assert.StartsWith("ms-settings:", WindowsSettingsLauncher.UriFor(page));

        Assert.Null(WindowsSettingsLauncher.UriFor((SettingsPage)99));
    }

    [Fact]
    public async Task UndefinedPageFailsWithInvalidInputAndStartsNothing()
    {
        var started = new List<string>();
        var launcher = new WindowsSettingsLauncher(started.Add);

        var result = await launcher.OpenAsync((SettingsPage)99, CancellationToken.None);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(started);
    }

    [Fact]
    public async Task CancelledTokenReportsCancellationAndStartsNothing()
    {
        var started = new List<string>();
        var launcher = new WindowsSettingsLauncher(started.Add);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await launcher.OpenAsync(SettingsPage.Wifi, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(started);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task LaunchFailureIsAFailedResultNotAnException(Type exceptionType)
    {
        var launcher = new WindowsSettingsLauncher(_ => throw (Exception)Activator.CreateInstance(exceptionType)!);

        var result = await launcher.OpenAsync(SettingsPage.Bluetooth, CancellationToken.None);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(ArgumentException))]
    public async Task EveryExpectedLaunchFailureIsAFailedResultNotACrash(Type exceptionType)
    {
        var log = new CollectingLog();
        var launcher = new WindowsSettingsLauncher(_ => throw (Exception)(exceptionType == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("shell")
            : Activator.CreateInstance(exceptionType)!), log);

        var result = await launcher.OpenAsync(SettingsPage.Bluetooth, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain("ms-settings", result.Error.SafeMessage); // nothing internal reaches the user
        var logged = Assert.Single(log.Events);
        Assert.Equal(exceptionType, logged.ExceptionType);
    }

    [Fact]
    public async Task AnUnexpectedProgrammingFaultIsNotHiddenAsAFailedResult()
    {
        var launcher = new WindowsSettingsLauncher(_ => throw new DivideByZeroException());

        await Assert.ThrowsAsync<DivideByZeroException>(() => launcher.OpenAsync(SettingsPage.Wifi, CancellationToken.None));
    }

    [Fact]
    public async Task TheLauncherStaysUsableAfterAFailure()
    {
        var calls = 0;
        var launcher = new WindowsSettingsLauncher(_ => { if (calls++ == 0) throw new Win32Exception(); });

        Assert.True((await launcher.OpenAsync(SettingsPage.Wifi, CancellationToken.None)).IsFailure);
        Assert.True((await launcher.OpenAsync(SettingsPage.Wifi, CancellationToken.None)).IsSuccess);
    }

    private sealed class CollectingLog : Lunar.Application.Abstractions.Diagnostics.IDiagnosticLog
    {
        public List<Lunar.Application.Abstractions.Diagnostics.DiagnosticEvent> Events { get; } = [];

        public void Write(Lunar.Application.Abstractions.Diagnostics.DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }
}

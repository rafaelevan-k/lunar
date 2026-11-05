using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Windows.Environment;
using Microsoft.Win32;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Environment;

// The registry reads and the change notification are real. The notification is proven on a private key of this test under
// HKCU\Software\LunarTests (removed afterwards), so the user's own personalization settings are never written. That Windows
// raises the same notification when the user switches light and dark in Settings is NOT proven here (manual check).
public sealed class WindowsThemeSourceTests : IDisposable
{
    private readonly string _keyPath = @"Software\LunarTests\" + Guid.NewGuid().ToString("N");

    private sealed class CollectingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(null, false)]
    public void OnlyTheValueOneMeansLight(int? stored, bool light)
    {
        using var source = new WindowsThemeSource(() => stored, _keyPath);

        Assert.Equal(light, source.PrefersLight);
    }

    [Fact]
    public void AReadThatIsRefusedMeansDarkAndIsLoggedOnce()
    {
        var log = new CollectingLog();
        using var source = new WindowsThemeSource(() => throw new UnauthorizedAccessException(), _keyPath, log);

        Assert.False(source.PrefersLight);
        Assert.False(source.PrefersLight);

        var logged = Assert.Single(log.Events);
        Assert.Equal("theme.read.failed", logged.EventId);
        Assert.Equal(typeof(UnauthorizedAccessException), logged.ExceptionType);
    }

    [Fact]
    public void TheRealPersonalizationValueCanBeReadWithoutError()
    {
        using var source = new WindowsThemeSource();

        _ = source.PrefersLight; // either answer is fine on any PC; it must not throw
    }

    [Fact]
    public async Task ChangingTheWatchedValueRaisesTheNotificationOnce()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(WindowsThemeSource.ValueName, 0, RegistryValueKind.DWord);
        using var source = new WindowsThemeSource(() => key.GetValue(WindowsThemeSource.ValueName) as int?, _keyPath);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        void Handler()
        {
            Interlocked.Increment(ref count);
            raised.TrySetResult();
        }

        source.Changed += Handler;
        await Task.Delay(150, TestContext.Current.CancellationToken); // the watcher thread is registering
        key.SetValue(WindowsThemeSource.ValueName, 1, RegistryValueKind.DWord);

        Assert.True(await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == raised.Task);
        Assert.True(source.PrefersLight);
        source.Changed -= Handler;
    }

    [Fact]
    public async Task AfterTheLastListenerLeavesNothingIsRaisedAnymore()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        using var source = new WindowsThemeSource(() => null, _keyPath);
        var count = 0;
        void Handler() => Interlocked.Increment(ref count);

        source.Changed += Handler;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        source.Changed -= Handler;
        key.SetValue(WindowsThemeSource.ValueName, 1, RegistryValueKind.DWord);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DisposingEndsTheWatcherAndLaterListenersAreIgnored()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        var source = new WindowsThemeSource(() => null, _keyPath);
        var count = 0;
        source.Changed += () => Interlocked.Increment(ref count);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        source.Dispose();
        source.Changed += () => Interlocked.Increment(ref count);
        key.SetValue(WindowsThemeSource.ValueName, 1, RegistryValueKind.DWord);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ListeningToAKeyThatDoesNotExistEndsQuietly()
    {
        using var source = new WindowsThemeSource(() => null, _keyPath);

        source.Changed += () => { };
        source.Dispose(); // joins the watcher thread; must return
    }
}

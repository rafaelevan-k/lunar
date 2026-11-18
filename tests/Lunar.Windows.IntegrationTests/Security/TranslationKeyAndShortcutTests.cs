using Lunar.Application.Translate;
using Lunar.Windows.Security;
using Lunar.Windows.Shell;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Security;

// BE-48 and BE-49 (ADR-0028): the translation key in DPAPI on this PC, and a real global shortcut.
public sealed class TranslationKeyAndShortcutTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "LunarKeyTests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task TheKeyIsSavedEncryptedReadBackAndRemoved()
    {
        var path = Path.Combine(_folder, "translate-key.dat");
        using var store = new DpapiTranslationKeyStore(path, new DpapiProtector("Lunar.TranslateKey.v1"), null);
        Assert.False(store.HasKey);
        Assert.True((await store.ReadAsync(Ct)).IsFailure);

        Assert.True((await store.SaveAsync("  " + Key + "\n", Ct)).IsSuccess);
        Assert.True(store.HasKey);
        var onDisk = await File.ReadAllBytesAsync(path, Ct);
        Assert.DoesNotContain("0123456789abcdef", System.Text.Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal); // never in clear text
        Assert.Equal(Key, (await store.ReadAsync(Ct)).Value);

        // Another of Lunar's secrets cannot open it: each has its own entropy.
        using var other = new DpapiTranslationKeyStore(path, new DpapiProtector(), null);
        Assert.True((await other.ReadAsync(Ct)).IsFailure);

        Assert.True((await store.SaveAsync("short", Ct)).IsFailure);
        Assert.Equal(Key, (await store.ReadAsync(Ct)).Value); // a refused save keeps the earlier key

        Assert.True((await store.RemoveAsync(Ct)).IsSuccess);
        Assert.False(File.Exists(path));
        Assert.True((await store.RemoveAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task AShortcutIsRegisteredAndAShortcutAlreadyTakenIsSaidSo()
    {
        // An unusual combination, so the test does not collide with the user's own shortcuts.
        var shortcut = ShortcutRules.Parse("Ctrl+Alt+Shift+F11")!;
        using var first = new WindowsGlobalShortcut();
        var registered = await first.RegisterAsync(shortcut, Ct);
        if (registered.IsFailure && registered.Error!.Code == Lunar.Domain.Shared.ErrorCode.AccessDenied)
            Assert.Skip("Another app on this PC holds Ctrl+Alt+Shift+F11.");
        Assert.True(registered.IsSuccess, registered.Error?.SafeMessage);
        Assert.Equal(shortcut, first.Current);

        using var second = new WindowsGlobalShortcut();
        var taken = await second.RegisterAsync(shortcut, Ct);
        Assert.Equal(Lunar.Domain.Shared.ErrorCode.AccessDenied, taken.Error!.Code);
        Assert.Null(second.Current);

        first.Unregister();
        Assert.Null(first.Current);
        Assert.True((await second.RegisterAsync(shortcut, Ct)).IsSuccess); // free again once released
    }

    [Theory]
    [InlineData("T", 0x54u)]
    [InlineData("5", 0x35u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("Space", 0x20u)]
    public void KeyNamesBecomeVirtualKeys(string key, uint expected) => Assert.Equal(expected, WindowsGlobalShortcut.VirtualKey(key));
}

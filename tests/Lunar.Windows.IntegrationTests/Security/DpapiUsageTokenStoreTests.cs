using System.Security.Cryptography;
using System.Text;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Domain.Shared;
using Lunar.Windows.Security;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Security;

// BE-33: the token store. The real Windows DPAPI is used for the round trip (this host, this user); a stand-in protector covers the failure paths.
public sealed class DpapiUsageTokenStoreTests : IDisposable
{
    private const string Token = "sk-ant-oat01-SECRET-TOKEN-VALUE-0123456789abcdef";

    private sealed class Xor : ISecretProtector
    {
        public bool FailProtect { get; set; }

        public bool FailUnprotect { get; set; }

        public byte[] Protect(byte[] plain) => FailProtect ? throw new CryptographicException("no") : [.. plain.Select(b => (byte)(b ^ 0x5A))];

        public byte[] Unprotect(byte[] sealedBytes) => FailUnprotect ? throw new CryptographicException("no") : [.. sealedBytes.Select(b => (byte)(b ^ 0x5A))];
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent) => Events.Add(diagnosticEvent);
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lunar-token-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_folder, "sub", "usage-token.dat");

    private DpapiUsageTokenStore RealStore(RecordingLog? log = null) => new(FilePath, new DpapiProtector(), log);

    private DpapiUsageTokenStore FakeStore(Xor? protector = null, RecordingLog? log = null) => new(FilePath, protector ?? new Xor(), log);

    // ---- the real Windows encryption ---------------------------------------------------------------------------------

    [Fact]
    public async Task ATokenSavedWithDpapiComesBackAndIsNeverInTheFileInClearText()
    {
        using var store = RealStore();

        Assert.True((await store.SaveAsync("  " + Token + "\n", Ct)).IsSuccess);
        var credential = await store.GetAsync(Ct);

        Assert.True(credential.IsSuccess, credential.Error?.SafeMessage);
        Assert.Equal(Token, credential.Value.Token);
        var bytes = await File.ReadAllBytesAsync(FilePath, Ct);
        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetBytes(Token), bytes);
    }

    [Fact]
    public async Task DpapiGivesDifferentBytesEachTimeAndRefusesAnAlteredFile()
    {
        using var store = RealStore();
        await store.SaveAsync(Token, Ct);
        var first = await File.ReadAllBytesAsync(FilePath, Ct);
        await store.SaveAsync(Token, Ct);
        var second = await File.ReadAllBytesAsync(FilePath, Ct);
        Assert.NotEqual(first, second); // a fresh random salt each time

        second[second.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(FilePath, second, Ct);
        var result = await store.GetAsync(Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Contains("Paste your token again", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperatingSystemProtectorRoundTripsAndKeepsItsEntropy()
    {
        var protector = new DpapiProtector();
        var sealedBytes = protector.Protect(Encoding.UTF8.GetBytes("hello"));

        Assert.Equal("hello", Encoding.UTF8.GetString(protector.Unprotect(sealedBytes)));
        Assert.Throws<CryptographicException>(() => protector.Unprotect([1, 2, 3, 4]));
    }

    // ---- behaviour of the store ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WithNoTokenSavedTheAnswerIsPasteItAndNothingIsCreated()
    {
        using var store = FakeStore();

        Assert.False((await store.HasTokenAsync(Ct)).Value);
        var result = await store.GetAsync(Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Contains("Paste your token on the Usage page", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task SavingReplacesAnEarlierTokenWholeAndLeavesNoTemporaryFile()
    {
        using var store = FakeStore();
        await store.SaveAsync("sk-ant-oat01-FIRST-TOKEN-0123456789abcdef", Ct);

        await store.SaveAsync(Token, Ct);

        Assert.Equal(Token, (await store.GetAsync(Ct)).Value.Token);
        Assert.True((await store.HasTokenAsync(Ct)).Value);
        Assert.Equal(["usage-token.dat"], Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public async Task AnInvalidPasteIsRefusedAndNothingIsWritten()
    {
        using var store = FakeStore();

        var result = await store.SaveAsync("not a token", Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AFailedSaveKeepsTheOldTokenAndSaysSo()
    {
        var protector = new Xor();
        using var store = FakeStore(protector);
        await store.SaveAsync(Token, Ct);
        protector.FailProtect = true;

        var result = await store.SaveAsync("sk-ant-oat01-NEW-TOKEN-0123456789abcdefgh", Ct);

        Assert.Equal(ErrorCode.IoFailure, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        protector.FailProtect = false;
        Assert.Equal(Token, (await store.GetAsync(Ct)).Value.Token);
    }

    [Fact]
    public async Task ATokenThatCanNoLongerBeDecryptedMeansPasteItAgain()
    {
        var protector = new Xor();
        var log = new RecordingLog();
        using var store = FakeStore(protector, log);
        await store.SaveAsync(Token, Ct);
        protector.FailUnprotect = true;

        var result = await store.GetAsync(Ct);

        Assert.Equal(ErrorCode.AccessDenied, result.Error!.Code);
        Assert.Contains(log.Events, e => e.EventId == "usage.token.read_failed");
    }

    [Fact]
    public async Task AFileThatIsEmptyOrHoldsSomethingElseIsUnreadableNotACrash()
    {
        using var store = FakeStore();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        await File.WriteAllBytesAsync(FilePath, [], Ct);
        Assert.Equal(ErrorCode.AccessDenied, (await store.GetAsync(Ct)).Error!.Code);

        await File.WriteAllBytesAsync(FilePath, new Xor().Protect(Encoding.UTF8.GetBytes("just some words")), Ct);
        Assert.Equal(ErrorCode.AccessDenied, (await store.GetAsync(Ct)).Error!.Code);

        await File.WriteAllBytesAsync(FilePath, new byte[40_000], Ct);
        Assert.Equal(ErrorCode.AccessDenied, (await store.GetAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task RemovingForgetsTheTokenAndSucceedsWhenThereIsNone()
    {
        using var store = FakeStore();
        Assert.True((await store.RemoveAsync(Ct)).IsSuccess);

        await store.SaveAsync(Token, Ct);
        Assert.True((await store.RemoveAsync(Ct)).IsSuccess);

        Assert.False(File.Exists(FilePath));
        Assert.False((await store.HasTokenAsync(Ct)).Value);
        Assert.Equal(ErrorCode.AccessDenied, (await store.GetAsync(Ct)).Error!.Code);
    }

    [Fact]
    public async Task NoMessageAndNoLogEventEverHoldsTheTokenOrThePath()
    {
        var protector = new Xor();
        var log = new RecordingLog();
        using var store = FakeStore(protector, log);
        var texts = new List<string>();

        texts.Add((await store.SaveAsync("sk-ant-api03-SECRET-TOKEN-VALUE-0123456789abc", Ct)).Error!.SafeMessage);
        await store.SaveAsync(Token, Ct);
        protector.FailProtect = true;
        texts.Add((await store.SaveAsync("sk-ant-oat01-SECRET-SECOND-0123456789abcdef", Ct)).Error!.SafeMessage);
        protector.FailProtect = false;
        protector.FailUnprotect = true;
        texts.Add((await store.GetAsync(Ct)).Error!.SafeMessage);
        var credential = new Lunar.Application.Usage.Models.UsageCredential(Token);

        Assert.All(texts, t => Assert.DoesNotContain("SECRET", t, StringComparison.Ordinal));
        Assert.All(texts, t => Assert.DoesNotContain(_folder, t, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(log.Events);
        Assert.All(log.Events, e => Assert.DoesNotContain("SECRET", e.ToString(), StringComparison.Ordinal));
        Assert.All(log.Events, e => Assert.DoesNotContain(_folder, e.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("UsageCredential", credential.ToString());
    }

    [Fact]
    public async Task ACancelledCallDoesNothing()
    {
        using var store = FakeStore();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        Assert.True((await store.SaveAsync(Token, cancel.Token)).IsCancelled);
        Assert.True((await store.GetAsync(cancel.Token)).IsCancelled);
        Assert.True((await store.RemoveAsync(cancel.Token)).IsCancelled);
        Assert.True((await store.HasTokenAsync(cancel.Token)).IsCancelled);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task ParallelSavesAndReadsNeverSeeAHalfWrittenToken()
    {
        using var store = FakeStore();
        await store.SaveAsync(Token, Ct);
        var other = "sk-ant-oat01-OTHER-TOKEN-VALUE-0123456789abcdef";

        var work = Enumerable.Range(0, 40).Select(async i =>
        {
            if (i % 2 == 0)
            {
                await store.SaveAsync(i % 4 == 0 ? Token : other, Ct);
                return true;
            }

            var read = await store.GetAsync(Ct);
            return read.IsSuccess && read.Value.Token is Token or "sk-ant-oat01-OTHER-TOKEN-VALUE-0123456789abcdef";
        });

        Assert.All(await Task.WhenAll(work), ok => Assert.True(ok));
    }

    [Fact]
    public void TheDefaultPlaceIsUnderTheUsersOwnLocalAppData()
    {
        Assert.StartsWith(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), DpapiUsageTokenStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Lunar", "usage-token.dat"), DpapiUsageTokenStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
    }
}

using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Translate;
using Lunar.Domain.Shared;
using Lunar.Windows.Capture;
using Lunar.Windows.Ocr;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Ocr;

/// <summary>
/// The screen translation pipeline on this PC's real active display (ADR-0028): the real capture, the real Windows OCR and the real grouping and colour
/// rules, with a stand-in translator (a real request needs the owner's key). It reads whatever is on screen, so it is opt-in, and it writes only counts
/// and times, never the text.
/// </summary>
public sealed class RealScreenPipelineTests(ITestOutputHelper output)
{
    private const string OptIn = "LUNAR_SCREEN_TESTS";

    [Fact]
    public async Task TheRealScreenIsReadGroupedAndColouredAndThenWiped()
    {
        if (System.Environment.GetEnvironmentVariable(OptIn) != "1")
            Assert.Skip($"Reads the real screen; set {OptIn}=1 to run it.");

        var settings = new SettingsService(new Repository());
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        await settings.UpdateAsync(s => s with { Translate = s.Translate with { Consented = true } }, TestContext.Current.CancellationToken);
        var translator = new EchoTranslator();
        var service = new ScreenTranslationService(new GdiScreenCapture(new NullLog()), new WindowsScreenTextReader(new NullLog()), translator, new Keys(), settings);

        var started = DateTime.UtcNow;
        var outcome = await service.TranslateScreenAsync(TranslationLanguage.Japanese, TestContext.Current.CancellationToken);
        var took = DateTime.UtcNow - started;

        Assert.True(outcome.IsSuccess, outcome.Problem?.ToString());
        var picture = outcome.Picture!;
        output.WriteLine($"display {picture.Width}x{picture.Height}: {outcome.Blocks.Count} blocks, {translator.Texts} texts sent, {translator.Characters} characters, {took.TotalMilliseconds:0} ms (OCR and rules; no network)");
        Assert.NotEmpty(outcome.Blocks);
        Assert.All(outcome.Blocks, b => Assert.True(Rgb.Contrast(b.Foreground, b.Background) >= 4.5));
        Assert.All(outcome.Blocks, b => Assert.InRange(b.Box.Right, 0, picture.Width + 1));

        service.Close();
        Assert.True(picture.IsCleared);
    }

    private sealed class EchoTranslator : ITextTranslator
    {
        public int Texts { get; private set; }

        public int Characters { get; private set; }

        public Task<Result<TranslationReply>> TranslateAsync(IReadOnlyList<string> texts, TranslationLanguage target, TranslatorCredential credential, CancellationToken ct)
        {
            Texts = texts.Count;
            Characters = texts.Sum(t => t.Length);
            return Task.FromResult(Result.Success(TranslationReply.Of([.. texts.Select(t => new TranslatedText("[" + t.Length + "]", "en"))])));
        }
    }

    private sealed class Keys : ITranslationKeyStore
    {
        public bool HasKey => true;

        public Task<Result> SaveAsync(string key, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result<string>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Success("0123456789abcdef0123456789abcdef"));

        public Task<Result> RemoveAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class Repository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }
}

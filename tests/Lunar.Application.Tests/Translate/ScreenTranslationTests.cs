using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Application.Translate;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Translate;

// BE-49 (ADR-0028): the pure rules over recorded OCR results, and the use case over fakes. The real OCR is proven in BE-47, the real service is NOT_RUN
// until the owner enters a key.
public sealed class ScreenTranslationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- shortcut and key rules --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ctrl+Alt+T", "Ctrl+Alt+T")]
    [InlineData("alt + ctrl + t", "Ctrl+Alt+T")]
    [InlineData("Win+Shift+F8", "Shift+Win+F8")]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("Ctrl+Alt+5", "Ctrl+Alt+5")]
    public void ShortcutsAreReadAndWrittenTheSameWay(string text, string expected) =>
        Assert.Equal(expected, ShortcutRules.Parse(text)!.ToString());

    [Theory]
    [InlineData("T")]
    [InlineData("Shift+T")]
    [InlineData("Ctrl+Ctrl+T")]
    [InlineData("Ctrl+T+Alt")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+Alt+Enter")]
    [InlineData("")]
    [InlineData(null)]
    public void ShortcutsThatStealTypingOrDoNotExistAreRefused(string? text) => Assert.Null(ShortcutRules.Parse(text));

    [Fact]
    public void KeysAndRegionsAreCheckedWithoutRepeatingThem()
    {
        Assert.Equal("0123456789abcdef0123456789abcdef", TranslationKeyRules.ValidateKey("  0123456789abcdef0123456789abcdef \n").Value);
        var tooShort = TranslationKeyRules.ValidateKey("secret-12");
        Assert.True(tooShort.IsFailure);
        Assert.DoesNotContain("secret", tooShort.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.True(TranslationKeyRules.ValidateKey("0123456789 abcdef0123456789abcdef").IsFailure);
        Assert.Null(TranslationKeyRules.ValidateRegion(" ").Value);
        Assert.Null(TranslationKeyRules.ValidateRegion("Global").Value);
        Assert.Equal("southeastasia", TranslationKeyRules.ValidateRegion(" SouthEastAsia ").Value);
        Assert.True(TranslationKeyRules.ValidateRegion("east asia\r\nX-Evil: 1").IsFailure);
    }

    [Fact]
    public void TheDefaultTargetIsTheOtherLanguage()
    {
        Assert.Equal(TranslationLanguage.Indonesian, ScreenTranslationService.DefaultTarget("en-US"));
        Assert.Equal(TranslationLanguage.English, ScreenTranslationService.DefaultTarget("id"));
        Assert.Equal(TranslationLanguage.Indonesian, ScreenTranslationService.DefaultTarget(null));
    }

    [Fact]
    public void BrokenTranslateSettingsAreRepaired()
    {
        var broken = LunarSettings.Defaults with
        {
            Translate = new TranslateSettings { Target = (TranslationLanguage)9, Shortcut = "Q", Provider = "Other", Region = "bad region!" }
        };
        var fixedSettings = SettingsRules.Normalize(broken, out var changed).Translate;
        Assert.True(changed);
        Assert.Null(fixedSettings.Target);
        Assert.Equal(TranslateSettings.DefaultShortcut, fixedSettings.Shortcut);
        Assert.Equal(TranslateSettings.AzureProvider, fixedSettings.Provider);
        Assert.Null(fixedSettings.Region);
        Assert.False(LunarSettings.Defaults.Translate.Consented);
        Assert.True(LunarSettings.Defaults.Translate.Enabled);
    }

    // ---- recognised text -------------------------------------------------------------------------------------------

    [Fact]
    public void JapaneseSpacesFromOcrAreRemovedButLatinSpacesStay()
    {
        Assert.Equal("東京の天気は晴れ", ScreenTextRules.JoinJapanese("東 京 の 天 気 は 晴 れ"));
        Assert.Equal("Windows を閉じる", ScreenTextRules.JoinJapanese("Windows を 閉 じ る"));
        Assert.True(ScreenTextRules.LooksJapanese("ウィンドウを閉じる"));
        Assert.True(ScreenTextRules.LooksJapanese("Windowsを閉じる"));
        Assert.False(ScreenTextRules.LooksJapanese("Close the window"));
        Assert.False(ScreenTextRules.LooksJapanese("Tutup jendela"));
    }

    [Fact]
    public void JapaneseLinesWinWhereBothRecognisersFoundSomething()
    {
        var english = new[] { Line("Save", 10, 10, 60, 20), Line("7 i 1/ 6", 10, 50, 120, 20) };
        var japanese = new[] { Line("保 存 し ま す", 10, 50, 120, 20), Line("Save", 10, 10, 60, 20) };
        var merged = ScreenTextRules.Merge(english, japanese);
        Assert.Equal(["保存します", "Save"], merged.Select(l => l.Text));
    }

    [Fact]
    public void LinesOfOneParagraphBecomeOneBlockAndColumnsStayApart()
    {
        var lines = new[]
        {
            Line("Lunar keeps your notes", 100, 100, 300, 20),
            Line("on this PC only.", 100, 124, 200, 20),
            Line("Settings", 600, 100, 100, 20),
            Line("12:45", 100, 300, 60, 20),
            Line("A new heading", 100, 200, 400, 36)
        };

        var blocks = ScreenTextRules.Group(lines);
        Assert.Equal(["Lunar keeps your notes on this PC only.", "Settings", "A new heading"], blocks.Select(b => b.Text));
        Assert.Equal(new TextBox(100, 100, 300, 44), blocks[0].Box);
        Assert.Equal(20, blocks[0].LineHeight);
        Assert.DoesNotContain(blocks, b => b.Text == "12:45"); // no letters, nothing to translate
    }

    [Fact]
    public void JapaneseLinesJoinWithoutSpaces()
    {
        var blocks = ScreenTextRules.Group([Line("ウィンドウを閉じる前に", 10, 10, 220, 20), Line("保存してください", 10, 34, 160, 20)]);
        Assert.Single(blocks);
        Assert.Equal("ウィンドウを閉じる前に保存してください", blocks[0].Text);
        Assert.True(blocks[0].IsJapanese);
    }

    [Fact]
    public void ColoursComeFromAroundAndInsideTheTextAndAreReadable()
    {
        // Dark blue page, light grey text in the middle of the box.
        var picture = Picture(200, 100, (x, y) => x is >= 60 and < 140 && y is >= 40 and < 60 && (x + y) % 3 == 0 ? new Rgb(200, 200, 200) : new Rgb(10, 20, 80));
        var (background, foreground) = ColorSampling.Sample(picture, new TextBox(50, 35, 100, 30));
        Assert.Equal(new Rgb(10, 20, 80), background);
        Assert.True(Rgb.Contrast(foreground, background) >= ColorSampling.MinimumTextContrast);
        Assert.True(foreground.R > 150);

        // A grey-on-grey original is made readable.
        Assert.True(Rgb.Contrast(ColorSampling.Readable(new Rgb(140, 140, 140), new Rgb(120, 120, 120)), new Rgb(120, 120, 120)) >= 4.5);
    }

    // ---- the use case ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsCapturedWithoutTheSwitchAKeyAndConsent()
    {
        var rig = await Rig.CreateAsync(consented: false);
        var outcome = await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);
        Assert.Equal(TranslationProblem.NotConsented, outcome.Translation);
        Assert.Equal(0, rig.Capture.Calls);

        rig.Keys.Key = null;
        Assert.Equal(TranslationProblem.NoKey, (await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct)).Translation);

        await rig.Settings.UpdateAsync(s => s with { Translate = s.Translate with { Enabled = false, Consented = true } }, Ct);
        rig.Keys.Key = Rig.ValidKey;
        Assert.Equal(ScreenProblem.Disabled, (await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct)).Problem);
        Assert.Equal(0, rig.Capture.Calls);
        Assert.Equal(0, rig.Translator.Calls);
    }

    [Fact]
    public async Task AScreenIsReadGroupedTranslatedAndColoured()
    {
        var rig = await Rig.CreateAsync();
        rig.Reader.English = [Line("Save your changes", 20, 20, 200, 20), Line("Simpan perubahan", 20, 200, 200, 20)];
        rig.Reader.Japanese = [Line("保 存 し て く だ さ い", 20, 100, 200, 20)];
        rig.Translator.Reply = texts => TranslationReply.Of([.. texts.Select(t => t switch
        {
            "Save your changes" => new TranslatedText("Simpan perubahan Anda", "en"),
            "Simpan perubahan" => new TranslatedText("Simpan perubahan", "id"),
            _ => new TranslatedText("Harap simpan", "ja")
        })]);

        var outcome = await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ScreenTranslationState.Showing, rig.Service.State);
        Assert.Equal(["Simpan perubahan Anda", "Harap simpan", null], outcome.Blocks.Select(b => b.Translation)); // the Indonesian line is left alone
        Assert.Equal(new TranslatorCredential(Rig.ValidKey, "southeastasia"), rig.Translator.LastCredential);
        Assert.Equal(TranslationLanguage.Indonesian, rig.Translator.LastTarget);
        Assert.All(outcome.Blocks, b => Assert.True(Rgb.Contrast(b.Foreground, b.Background) >= 4.5));
        Assert.Same(rig.Capture.Last, outcome.Picture);

        rig.Service.Close();
        Assert.True(rig.Capture.Last!.IsCleared); // the overlay closed: nothing of the screen stays
        Assert.Equal(ScreenTranslationState.Idle, rig.Service.State);
    }

    [Fact]
    public async Task JapaneseIsNotSentWhenJapaneseIsTheTargetAndTheLanguageSwitchKeepsThePicture()
    {
        var rig = await Rig.CreateAsync();
        rig.Reader.English = [Line("Close", 20, 20, 80, 20)];
        rig.Reader.Japanese = [Line("閉 じ る", 20, 100, 80, 20)];
        rig.Translator.Reply = texts => TranslationReply.Of([.. texts.Select(_ => new TranslatedText("閉じる", "en"))]);

        var japanese = await rig.Service.TranslateScreenAsync(TranslationLanguage.Japanese, Ct);
        Assert.Equal(["Close"], rig.Translator.LastTexts);
        Assert.Equal(["閉じる", null], japanese.Blocks.Select(b => b.Translation));

        rig.Translator.Reply = texts => TranslationReply.Of([.. texts.Select(t => new TranslatedText(t == "Close" ? "Tutup" : "Tutup", t == "Close" ? "en" : "ja"))]);
        var again = await rig.Service.RetranslateAsync(TranslationLanguage.Indonesian, Ct);
        Assert.Equal(1, rig.Capture.Calls); // no second picture
        Assert.Equal(["Close", "閉じる"], rig.Translator.LastTexts);
        Assert.All(again.Blocks, b => Assert.Equal("Tutup", b.Translation));
    }

    [Fact]
    public async Task EveryFailureIsSaidAndThePictureIsWiped()
    {
        var rig = await Rig.CreateAsync();
        rig.Capture.Fail = true;
        Assert.Equal(ScreenProblem.CaptureRefused, (await rig.Service.TranslateScreenAsync(TranslationLanguage.English, Ct)).Problem);
        rig.Capture.Fail = false;

        rig.Reader.Languages = [];
        Assert.Equal(ScreenProblem.NoRecognizer, (await rig.Service.TranslateScreenAsync(TranslationLanguage.English, Ct)).Problem);
        rig.Reader.Languages = [OcrLanguage.English, OcrLanguage.Japanese];

        rig.Capture.Black = true;
        Assert.Equal(ScreenProblem.Protected, (await rig.Service.TranslateScreenAsync(TranslationLanguage.English, Ct)).Problem);
        Assert.True(rig.Capture.Last!.IsCleared);
        rig.Capture.Black = false;
        Assert.Equal(ScreenProblem.NoText, (await rig.Service.TranslateScreenAsync(TranslationLanguage.English, Ct)).Problem);

        rig.Reader.English = [Line("Hello", 20, 20, 80, 20)];
        foreach (var problem in new[] { TranslationProblem.KeyRefused, TranslationProblem.FreeLimitReached, TranslationProblem.RateLimited, TranslationProblem.NoNetwork })
        {
            rig.Translator.Reply = _ => TranslationReply.Failed(problem);
            var outcome = await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);
            Assert.Equal(ScreenProblem.Translation, outcome.Problem);
            Assert.Equal(problem, outcome.Translation);
            Assert.True(rig.Capture.Last!.IsCleared);
            Assert.Equal(ScreenTranslationState.Failed, rig.Service.State);
        }
    }

    [Fact]
    public async Task ASecondTriggerWhileBusyIsRefusedAndCancelStopsTheRun()
    {
        var rig = await Rig.CreateAsync();
        rig.Reader.English = [Line("Hello", 20, 20, 80, 20)];
        var gate = new TaskCompletionSource();
        rig.Translator.Wait = gate.Task;
        rig.Translator.Reply = texts => TranslationReply.Of([.. texts.Select(_ => new TranslatedText("Halo", "en"))]);

        var first = rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);
        while (rig.Service.State != ScreenTranslationState.Translating)
            await Task.Delay(5, Ct);
        Assert.True(rig.Service.IsBusy);
        Assert.Equal(ScreenProblem.Busy, (await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct)).Problem);

        rig.Service.Cancel();
        gate.SetResult();
        var outcome = await first;
        Assert.Equal(ScreenProblem.Cancelled, outcome.Problem);
        Assert.Equal(ScreenTranslationState.Idle, rig.Service.State);
        Assert.True(rig.Capture.Last!.IsCleared);
    }

    [Fact]
    public async Task NothingOfTheScreenReachesTheLog()
    {
        var rig = await Rig.CreateAsync();
        rig.Reader.English = [Line("Private message from Budi", 20, 20, 300, 20)];
        rig.Translator.Reply = texts => TranslationReply.Of([.. texts.Select(_ => new TranslatedText("Pesan pribadi dari Budi", "en"))]);
        await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);
        rig.Translator.Reply = _ => TranslationReply.Failed(TranslationProblem.KeyRefused);
        await rig.Service.TranslateScreenAsync(TranslationLanguage.Indonesian, Ct);

        var all = string.Join("|", rig.Log.Events.Select(e => $"{e.EventId} {e.Component} {e.Operation}"));
        Assert.NotEmpty(rig.Log.Events);
        Assert.DoesNotContain("Budi", all, StringComparison.Ordinal);
        Assert.DoesNotContain(Rig.ValidKey, all, StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static OcrLine Line(string text, double x, double y, double w, double h)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var step = w / Math.Max(1, words.Length);
        return new OcrLine(text, [.. words.Select((word, i) => new OcrWord(word, new TextBox(x + (i * step), y, step, h)))]);
    }

    private static ScreenPicture Picture(int width, int height, Func<int, int, Rgb> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = color(x, y);
                var i = ((y * width) + x) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 255;
            }
        }

        return new ScreenPicture(new PixelRect(0, 0, width, height), pixels);
    }

    private sealed class Rig
    {
        public const string ValidKey = "0123456789abcdef0123456789abcdef";

        private Rig(SettingsService settings)
        {
            Settings = settings;
            Service = new ScreenTranslationService(Capture, Reader, Translator, Keys, Settings, Log);
        }

        public FakeCapture Capture { get; } = new();

        public FakeReader Reader { get; } = new();

        public FakeTranslator Translator { get; } = new();

        public FakeKeys Keys { get; } = new();

        public RecordingLog Log { get; } = new();

        public SettingsService Settings { get; }

        public ScreenTranslationService Service { get; }

        public static async Task<Rig> CreateAsync(bool consented = true)
        {
            var settings = new SettingsService(new Repository());
            await settings.LoadAsync(Ct);
            await settings.UpdateAsync(s => s with { Translate = s.Translate with { Consented = consented, Region = "southeastasia" } }, Ct);
            return new Rig(settings);
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public bool Black { get; set; }

        public ScreenPicture? Last { get; private set; }

        public Task<Result<ScreenPicture>> CaptureActiveDisplayAsync(CancellationToken ct)
        {
            Calls++;
            if (Fail)
                return Task.FromResult(Result.Failure<ScreenPicture>(new OperationError(ErrorCode.AccessDenied, "No.")));
            Last = Picture(400, 300, (_, _) => Black ? new Rgb(0, 0, 0) : new Rgb(250, 250, 250));
            return Task.FromResult(Result.Success(Last));
        }
    }

    private sealed class FakeReader : IScreenTextReader
    {
        public IReadOnlyList<OcrLanguage> Languages { get; set; } = [OcrLanguage.English, OcrLanguage.Japanese];

        public IReadOnlyList<OcrLine> English { get; set; } = [];

        public IReadOnlyList<OcrLine> Japanese { get; set; } = [];

        public IReadOnlyList<OcrLanguage> AvailableLanguages => Languages;

        public Task<Result<IReadOnlyList<OcrLine>>> ReadAsync(ScreenPicture picture, OcrLanguage language, CancellationToken ct) =>
            Task.FromResult(Result.Success(language == OcrLanguage.Japanese ? Japanese : English));
    }

    private sealed class FakeTranslator : ITextTranslator
    {
        public int Calls { get; private set; }

        public Func<IReadOnlyList<string>, TranslationReply> Reply { get; set; } = texts => TranslationReply.Of([.. texts.Select(t => new TranslatedText(t, null))]);

        public Task? Wait { get; set; }

        public IReadOnlyList<string>? LastTexts { get; private set; }

        public TranslationLanguage? LastTarget { get; private set; }

        public TranslatorCredential? LastCredential { get; private set; }

        public async Task<Result<TranslationReply>> TranslateAsync(IReadOnlyList<string> texts, TranslationLanguage target, TranslatorCredential credential, CancellationToken ct)
        {
            Calls++;
            LastTexts = texts;
            LastTarget = target;
            LastCredential = credential;
            if (Wait is not null)
                await Wait;
            return ct.IsCancellationRequested ? Result.Cancelled<TranslationReply>() : Result.Success(Reply(texts));
        }
    }

    private sealed class FakeKeys : ITranslationKeyStore
    {
        public string? Key { get; set; } = Rig.ValidKey;

        public bool HasKey => Key is not null;

        public Task<Result> SaveAsync(string key, CancellationToken ct)
        {
            Key = key;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> ReadAsync(CancellationToken ct) =>
            Task.FromResult(Key is null ? Result.Failure<string>(new OperationError(ErrorCode.AccessDenied, "No key.")) : Result.Success(Key));

        public Task<Result> RemoveAsync(CancellationToken ct)
        {
            Key = null;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class Repository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        private readonly List<DiagnosticEvent> _events = [];

        public IReadOnlyList<DiagnosticEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (_events)
                _events.Add(diagnosticEvent);
        }
    }
}

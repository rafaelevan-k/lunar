using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Domain.Shared;

namespace Lunar.Application.Translate;

/// <summary>Where screen translation is (ADR-0028).</summary>
public enum ScreenTranslationState
{
    Idle,
    Capturing,
    Reading,
    Translating,
    Showing,
    Failed
}

/// <summary>Why the overlay cannot show a translation, besides the service's own reasons (<see cref="TranslationProblem"/>).</summary>
public enum ScreenProblem
{
    /// <summary>Screen translation is turned off in Settings.</summary>
    Disabled,

    /// <summary>Another translation is already running.</summary>
    Busy,

    /// <summary>Windows did not allow the screen to be read.</summary>
    CaptureRefused,

    /// <summary>No OCR language Lunar uses is installed in Windows.</summary>
    NoRecognizer,

    /// <summary>The picture is black: protected content (DRM) or a blank screen.</summary>
    Protected,

    /// <summary>Nothing readable was found.</summary>
    NoText,

    /// <summary>The service gave no translation; <see cref="ScreenTranslationOutcome.Translation"/> says why.</summary>
    Translation,

    Cancelled
}

/// <summary>One block as the overlay draws it: where, in which colours, and the translation (null when the block is already in the target language).</summary>
public sealed record TranslatedBlock(TextBox Box, string Original, string? Translation, Rgb Background, Rgb Foreground, double LineHeight, int LineCount, bool IsJapanese);

/// <summary>The result of one run: the picture and its blocks, or the reason there are none.</summary>
public sealed record ScreenTranslationOutcome(
    ScreenPicture? Picture,
    IReadOnlyList<TranslatedBlock> Blocks,
    TranslationLanguage Target,
    ScreenProblem? Problem,
    TranslationProblem? Translation)
{
    public bool IsSuccess => Problem is null;

    internal static ScreenTranslationOutcome Fail(ScreenProblem problem, TranslationLanguage target, TranslationProblem? translation = null) =>
        new(null, [], target, problem, translation);
}

/// <summary>
/// Screen translation, end to end (ADR-0028, BE-49): checks the switch, the key and the user's consent first; then takes one picture of the active
/// display, reads it with every installed recogniser Lunar uses, merges and groups the lines into blocks, leaves out blocks already in the target
/// language, translates the rest with the user's key, and samples each block's colours. Only one run at a time; <see cref="Cancel"/> stops it at the
/// next step. <see cref="Close"/> wipes the picture and forgets the texts. Logs carry fixed events and counts only.
/// </summary>
public sealed class ScreenTranslationService : IDisposable
{
    private const string Component = "ScreenTranslate";

    private readonly IScreenCapture _capture;
    private readonly IScreenTextReader _reader;
    private readonly ITextTranslator _translator;
    private readonly ITranslationKeyStore _keys;
    private readonly SettingsService _settings;
    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private ScreenPicture? _picture;
    private IReadOnlyList<RecognizedBlock> _blocks = [];
    private ScreenTranslationState _state;

    public ScreenTranslationService(IScreenCapture capture, IScreenTextReader reader, ITextTranslator translator, ITranslationKeyStore keys, SettingsService settings, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(settings);
        _capture = capture;
        _reader = reader;
        _translator = translator;
        _keys = keys;
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public ScreenTranslationState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>True between the start of a run and its outcome.</summary>
    public bool IsBusy => State is ScreenTranslationState.Capturing or ScreenTranslationState.Reading or ScreenTranslationState.Translating;

    /// <summary>The default target for a UI language: Indonesian for an English Lunar, English for an Indonesian one (ADR-0028 Q5).</summary>
    public static TranslationLanguage DefaultTarget(string? uiLanguage) =>
        uiLanguage is not null && uiLanguage.StartsWith("id", StringComparison.OrdinalIgnoreCase) ? TranslationLanguage.English : TranslationLanguage.Indonesian;

    /// <summary>Takes a new picture and translates it into <paramref name="target"/>.</summary>
    public async Task<ScreenTranslationOutcome> TranslateScreenAsync(TranslationLanguage target, CancellationToken ct)
    {
        var settings = _settings.Current.Translate;
        if (!settings.Enabled)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Disabled, target);
        if (!_keys.HasKey)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Translation, target, TranslationProblem.NoKey);
        if (!settings.Consented)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Translation, target, TranslationProblem.NotConsented);

        var run = Begin(ct);
        if (run is null)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Busy, target);

        try
        {
            ClearPicture();
            var started = DateTime.UtcNow;
            SetState(ScreenTranslationState.Capturing);
            var captured = await _capture.CaptureActiveDisplayAsync(run.Token).ConfigureAwait(false);
            if (captured.IsCancelled || run.IsCancellationRequested)
                return Finish(ScreenTranslationOutcome.Fail(ScreenProblem.Cancelled, target));
            if (captured.IsFailure)
                return Finish(ScreenTranslationOutcome.Fail(ScreenProblem.CaptureRefused, target));
            var picture = captured.Value;
            lock (_gate)
                _picture = picture;

            SetState(ScreenTranslationState.Reading);
            var languages = _reader.AvailableLanguages;
            if (languages.Count == 0)
                return Finish(ScreenTranslationOutcome.Fail(ScreenProblem.NoRecognizer, target));
            IReadOnlyList<OcrLine> english = [];
            IReadOnlyList<OcrLine> japanese = [];
            foreach (var language in languages)
            {
                var read = await _reader.ReadAsync(picture, language, run.Token).ConfigureAwait(false);
                if (read.IsCancelled || run.IsCancellationRequested)
                    return Finish(ScreenTranslationOutcome.Fail(ScreenProblem.Cancelled, target));
                if (read.IsFailure)
                    continue;
                if (language == OcrLanguage.Japanese)
                    japanese = read.Value;
                else
                    english = read.Value;
            }

            var blocks = ScreenTextRules.Group(ScreenTextRules.Merge(english, japanese));
            if (blocks.Count == 0)
                return Finish(ScreenTranslationOutcome.Fail(LooksBlank(picture) ? ScreenProblem.Protected : ScreenProblem.NoText, target));
            lock (_gate)
                _blocks = blocks;

            var outcome = await TranslateBlocksAsync(picture, blocks, target, run.Token).ConfigureAwait(false);
            if (outcome.IsSuccess)
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "screen-translate.done", Component, "translate", Duration: DateTime.UtcNow - started));
            return Finish(outcome);
        }
        finally
        {
            End(run);
        }
    }

    /// <summary>The same picture and blocks into another language (the overlay's language switch). No new picture is taken.</summary>
    public async Task<ScreenTranslationOutcome> RetranslateAsync(TranslationLanguage target, CancellationToken ct)
    {
        ScreenPicture? picture;
        IReadOnlyList<RecognizedBlock> blocks;
        lock (_gate)
        {
            picture = _picture;
            blocks = _blocks;
        }

        if (picture is null || picture.IsCleared || blocks.Count == 0)
            return await TranslateScreenAsync(target, ct).ConfigureAwait(false);

        var run = Begin(ct);
        if (run is null)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Busy, target);
        try
        {
            return Finish(await TranslateBlocksAsync(picture, blocks, target, run.Token).ConfigureAwait(false));
        }
        finally
        {
            End(run);
        }
    }

    /// <summary>Stops the running translation at its next step.</summary>
    public void Cancel()
    {
        lock (_gate)
            _run?.Cancel();
    }

    /// <summary>The overlay closed: the picture is wiped and the texts are forgotten (ADR-0028 §Privacy).</summary>
    public void Close()
    {
        Cancel();
        ClearPicture();
        SetState(ScreenTranslationState.Idle);
    }

    public void Dispose() => Close();

    private async Task<ScreenTranslationOutcome> TranslateBlocksAsync(ScreenPicture picture, IReadOnlyList<RecognizedBlock> blocks, TranslationLanguage target, CancellationToken ct)
    {
        var keyRead = await _keys.ReadAsync(ct).ConfigureAwait(false);
        if (keyRead.IsCancelled)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Cancelled, target);
        if (keyRead.IsFailure)
            return ScreenTranslationOutcome.Fail(ScreenProblem.Translation, target, TranslationProblem.NoKey);

        // Japanese text needs no trip to the service when Japanese is the target; everything else is sent once.
        var toSend = blocks.Select((b, i) => (Block: b, Index: i)).Where(x => !(target == TranslationLanguage.Japanese && x.Block.IsJapanese)).ToList();
        SetState(ScreenTranslationState.Translating);
        var translations = new string?[blocks.Count];
        if (toSend.Count > 0)
        {
            var credential = new TranslatorCredential(keyRead.Value, _settings.Current.Translate.Region);
            var reply = await _translator.TranslateAsync(toSend.Select(x => x.Block.Text).ToArray(), target, credential, ct).ConfigureAwait(false);
            if (reply.IsCancelled || ct.IsCancellationRequested)
                return ScreenTranslationOutcome.Fail(ScreenProblem.Cancelled, target);
            if (reply.IsFailure || !reply.Value.IsSuccess)
                return ScreenTranslationOutcome.Fail(ScreenProblem.Translation, target, reply.IsFailure ? TranslationProblem.ProviderError : reply.Value.Problem);

            var code = target switch { TranslationLanguage.English => "en", TranslationLanguage.Indonesian => "id", _ => "ja" };
            for (var i = 0; i < toSend.Count; i++)
            {
                var translated = reply.Value.Translations![i];
                var same = string.Equals(translated.Text.Trim(), toSend[i].Block.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                var already = translated.DetectedLanguage is { } detected && detected.StartsWith(code, StringComparison.OrdinalIgnoreCase);
                translations[toSend[i].Index] = same || already ? null : translated.Text;
            }
        }

        var drawn = new List<TranslatedBlock>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var (background, foreground) = ColorSampling.Sample(picture, block.Box);
            drawn.Add(new TranslatedBlock(block.Box, block.Text, translations[i], background, foreground, block.LineHeight, block.Lines.Count, block.IsJapanese));
        }

        return new ScreenTranslationOutcome(picture, drawn, target, null, null);
    }

    /// <summary>Nearly every sampled pixel black: protected content or a blank screen.</summary>
    internal static bool LooksBlank(ScreenPicture picture)
    {
        var step = Math.Max(4, picture.Pixels.Length / 4 / 20000) * 4;
        var dark = 0;
        var total = 0;
        for (var i = 0; i + 2 < picture.Pixels.Length; i += step)
        {
            total++;
            if (picture.Pixels[i] < 16 && picture.Pixels[i + 1] < 16 && picture.Pixels[i + 2] < 16)
                dark++;
        }

        return total > 0 && dark >= total * 0.98;
    }

    private CancellationTokenSource? Begin(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_run is not null)
                return null;
            _run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return _run;
        }
    }

    private void End(CancellationTokenSource run)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_run, run))
                _run = null;
        }

        run.Dispose();
    }

    private ScreenTranslationOutcome Finish(ScreenTranslationOutcome outcome)
    {
        if (outcome.Problem == ScreenProblem.Cancelled)
        {
            ClearPicture();
            SetState(ScreenTranslationState.Idle);
        }
        else if (!outcome.IsSuccess)
        {
            ClearPicture();
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "screen-translate.failed", Component, outcome.Problem.ToString()!.ToLowerInvariant()));
            SetState(ScreenTranslationState.Failed);
        }
        else
        {
            SetState(ScreenTranslationState.Showing);
        }

        return outcome;
    }

    private void ClearPicture()
    {
        ScreenPicture? picture;
        lock (_gate)
        {
            picture = _picture;
            _picture = null;
            _blocks = [];
        }

        picture?.Clear();
    }

    private void SetState(ScreenTranslationState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

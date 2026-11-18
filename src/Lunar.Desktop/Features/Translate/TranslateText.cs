using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Translate;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Translate;

/// <summary>Words for screen translation's outcomes, in the user's language (ADR-0028 §13).</summary>
internal static class TranslateText
{
    public static string Problem(TranslationProblem? problem) => problem switch
    {
        TranslationProblem.NoKey => Strings.Get("Translate.ProblemNoKey"),
        TranslationProblem.NotConsented => Strings.Get("Translate.ProblemNotConsented"),
        TranslationProblem.KeyRefused => Strings.Get("Translate.ProblemKeyRefused"),
        TranslationProblem.FreeLimitReached => Strings.Get("Translate.ProblemFreeLimit"),
        TranslationProblem.RateLimited => Strings.Get("Translate.ProblemRateLimited"),
        TranslationProblem.NoNetwork => Strings.Get("Translate.ProblemNoNetwork"),
        _ => Strings.Get("Translate.ProblemProvider")
    };

    public static string Problem(ScreenTranslationOutcome outcome) => outcome.Problem switch
    {
        ScreenProblem.Disabled => Strings.Get("Translate.ProblemDisabled"),
        ScreenProblem.Busy => Strings.Get("Translate.ProblemBusy"),
        ScreenProblem.CaptureRefused => Strings.Get("Translate.ProblemCapture"),
        ScreenProblem.NoRecognizer => Strings.Get("Translate.ProblemNoRecognizer"),
        ScreenProblem.Protected => Strings.Get("Translate.ProblemProtected"),
        ScreenProblem.NoText => Strings.Get("Translate.ProblemNoText"),
        ScreenProblem.Translation => Problem(outcome.Translation),
        _ => string.Empty
    };

    public static string Language(TranslationLanguage language) => language switch
    {
        TranslationLanguage.English => Strings.Get("Translate.English"),
        TranslationLanguage.Indonesian => Strings.Get("Translate.Indonesian"),
        _ => Strings.Get("Translate.Japanese")
    };
}

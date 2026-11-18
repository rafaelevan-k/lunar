using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Translate;

/// <summary>The languages screen translation translates into (ADR-0028).</summary>
public enum TranslationLanguage
{
    English,
    Indonesian,
    Japanese
}

/// <summary>Why a translation did not happen, so the overlay can say it in the user's language (ADR-0028 §13).</summary>
public enum TranslationProblem
{
    /// <summary>No key is saved.</summary>
    NoKey,

    /// <summary>The user has not allowed the first request yet.</summary>
    NotConsented,

    /// <summary>The service refused the key or its region.</summary>
    KeyRefused,

    /// <summary>The free monthly allowance is used up (or the subscription does not allow the request).</summary>
    FreeLimitReached,

    /// <summary>Too many requests in a short time.</summary>
    RateLimited,

    /// <summary>The service could not be reached or did not answer in time.</summary>
    NoNetwork,

    /// <summary>The service answered with something Lunar could not use.</summary>
    ProviderError
}

/// <summary>The user's own key for the translation service and, for a regional resource, its region. Never logged; <see cref="ToString"/> hides it.</summary>
public sealed record TranslatorCredential(string Key, string? Region)
{
    public override string ToString() => "TranslatorCredential(redacted)";
}

/// <summary>One translated text and the language the service detected for its source (a BCP 47 tag such as "en", "id" or "ja"), if it said.</summary>
public sealed record TranslatedText(string Text, string? DetectedLanguage);

/// <summary>Either one translation per input, in order, or the reason there is none.</summary>
public sealed record TranslationReply(IReadOnlyList<TranslatedText>? Translations, TranslationProblem? Problem)
{
    public static TranslationReply Of(IReadOnlyList<TranslatedText> translations) => new(translations, null);

    public static TranslationReply Failed(TranslationProblem problem) => new(null, problem);

    public bool IsSuccess => Translations is not null;
}

/// <summary>
/// Translates recognised screen text through the online service the owner chose (ADR-0028: Microsoft Azure AI Translator, free tier) with the
/// user's own key. Only the given texts are sent, over HTTPS, to the service's documented host. Nothing is logged but fixed events and counts.
/// </summary>
public interface ITextTranslator
{
    /// <summary>
    /// Translates every text into <paramref name="target"/>, detecting each source language. Splits into as many requests as the service's limits need.
    /// Cancellable; a cancelled call returns <see cref="Result.Cancelled{T}"/>.
    /// </summary>
    Task<Result<TranslationReply>> TranslateAsync(IReadOnlyList<string> texts, TranslationLanguage target, TranslatorCredential credential, CancellationToken ct);
}

/// <summary>
/// Keeps the user's translation key on this PC, encrypted for the signed-in Windows user, outside <c>settings.json</c> (ADR-0028 §8). Never logged,
/// never shown again after saving, removable. Messages hold no key or path.
/// </summary>
public interface ITranslationKeyStore
{
    /// <summary>Whether a key is saved, without reading it.</summary>
    bool HasKey { get; }

    /// <summary>Checks and saves the key, replacing an earlier one. An invalid text is <see cref="ErrorCode.InvalidInput"/>.</summary>
    Task<Result> SaveAsync(string key, CancellationToken ct);

    /// <summary>The saved key, or <see cref="ErrorCode.AccessDenied"/> when there is none or it can no longer be read.</summary>
    Task<Result<string>> ReadAsync(CancellationToken ct);

    /// <summary>Forgets the key; succeeds when there is none.</summary>
    Task<Result> RemoveAsync(CancellationToken ct);
}

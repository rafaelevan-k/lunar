using Lunar.Domain.Shared;

namespace Lunar.Application.Translate;

/// <summary>What a translation key and region may look like before they are saved or sent (ADR-0028 §8). The messages never repeat the text.</summary>
public static class TranslationKeyRules
{
    public const int MinKeyLength = 16;
    public const int MaxKeyLength = 256;
    public const int MaxRegionLength = 40;

    /// <summary>The trimmed key: 16 to 256 visible ASCII characters without spaces.</summary>
    public static Result<string> ValidateKey(string? text)
    {
        var key = text?.Trim() ?? string.Empty;
        if (key.Length < MinKeyLength || key.Length > MaxKeyLength || key.Any(c => c <= ' ' || c > '~'))
            return Result.Failure<string>(new OperationError(ErrorCode.InvalidInput, "That does not look like a translation key. Copy it again from the Azure portal."));
        return Result.Success(key);
    }

    /// <summary>
    /// The region of an Azure resource ("westeurope", "southeastasia"), lower case, or null for a global resource. Letters and digits only, so the
    /// value is safe to send as a header.
    /// </summary>
    public static Result<string?> ValidateRegion(string? text)
    {
        var region = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(region) || region == "global")
            return Result.Success<string?>(null);
        if (region.Length > MaxRegionLength || !region.All(char.IsAsciiLetterOrDigit))
            return Result.Failure<string?>(new OperationError(ErrorCode.InvalidInput, "The region should be one word, for example southeastasia, or empty for a global resource."));
        return Result.Success<string?>(region);
    }
}

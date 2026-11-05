using Lunar.Domain.Shared;

namespace Lunar.Application.Usage;

/// <summary>
/// What a pasted token must look like before Lunar keeps it (BE-33). It is a shape check to catch a wrong paste early, not a proof that the token
/// works (only Claude can say that). The text is trimmed (a paste often brings a line break), must be one visible-ASCII word of a sensible length,
/// and must start with <c>sk-ant-</c> like the token that <c>claude setup-token</c> prints. A console API key (<c>sk-ant-api...</c>) is refused with
/// its own advice because it is not the kind of login the usage address takes. A message never repeats the text.
/// </summary>
public static class UsageTokenRules
{
    public const int MinLength = 20;
    public const int MaxLength = 2048;
    private const string Prefix = "sk-ant-";

    /// <summary>The trimmed token, or <see cref="ErrorCode.InvalidInput"/> with a message that says what to do.</summary>
    public static Result<string> Validate(string? pasted)
    {
        var text = pasted?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return Fail("Paste your token first.");
        if (text.Any(c => c is < '!' or > '~'))
            return Fail("A token is one piece of text with no spaces or line breaks. Copy it again, only the token itself.");
        if (text.Length < MinLength)
            return Fail("That is too short to be a token. Copy the whole token.");
        if (text.Length > MaxLength)
            return Fail("That is too long to be a token. Copy only the token.");
        if (text.StartsWith("sk-ant-api", StringComparison.Ordinal))
            return Fail("That looks like an API key, not the token that 'claude setup-token' prints. An API key does not work here.");
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return Fail("That does not look like the token that 'claude setup-token' prints (it starts with sk-ant-). Copy it again.");
        return Result.Success(text);
    }

    private static Result<string> Fail(string message) => Result.Failure<string>(new OperationError(ErrorCode.InvalidInput, message));
}

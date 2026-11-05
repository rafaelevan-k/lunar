using Lunar.Application.Usage;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Usage;

// BE-33: the shape check of a pasted token. It never repeats what was pasted.
public sealed class UsageTokenRulesTests
{
    private const string Good = "sk-ant-oat01-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-";

    [Fact]
    public void ATokenOfTheExpectedShapeIsAcceptedAndTrimmed()
    {
        var result = UsageTokenRules.Validate("  " + Good + "\r\n");

        Assert.True(result.IsSuccess);
        Assert.Equal(Good, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void NothingPastedIsAskedForAgain(string? pasted)
    {
        var result = UsageTokenRules.Validate(pasted);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("Paste your token first", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-ant-oat01-has a space inside it 12345")]
    [InlineData("sk-ant-oat01-line\nbreak-inside-the-token-1234")]
    [InlineData("sk-ant-oat01-tab\tinside-the-token-1234567")]
    [InlineData("sk-ant-oat01-ünïcode-inside-the-token-12345")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9abc")]
    public void SpacesBreaksAndNonAsciiAreRefused(string pasted)
    {
        var result = UsageTokenRules.Validate(pasted);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("no spaces or line breaks", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ATooShortOrTooLongTextIsRefused()
    {
        Assert.Contains("too short", UsageTokenRules.Validate("sk-ant-abc").Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("too long", UsageTokenRules.Validate("sk-ant-" + new string('a', UsageTokenRules.MaxLength)).Error!.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApiKeyGetsItsOwnAdvice()
    {
        var result = UsageTokenRules.Validate("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");

        Assert.Contains("API key", result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("setup-token", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("thisisnotatokenthisisnotatoken1234567890")]
    public void SomethingThatDoesNotStartLikeTheTokenIsRefused(string pasted)
    {
        var result = UsageTokenRules.Validate(pasted);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Contains("sk-ant-", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-ant-api03-SECRET-VALUE-0123456789abcdef")]
    [InlineData("secret-value-0123456789abcdef-no-prefix")]
    [InlineData("sk-ant-oat01-SECRET short")]
    public void NoMessageEverRepeatsWhatWasPasted(string pasted)
    {
        var result = UsageTokenRules.Validate(pasted);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain("SECRET", result.Error!.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }
}

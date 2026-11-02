using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Domain.Tests.Shared;

public sealed class OperationErrorTests
{
    [Fact]
    public void ConstructorStoresValuesAndDefaultsToNotRetryable()
    {
        var error = new OperationError(ErrorCode.DeviceUnavailable, "No audio device.");

        Assert.Equal(ErrorCode.DeviceUnavailable, error.Code);
        Assert.Equal("No audio device.", error.SafeMessage);
        Assert.False(error.Retryable);
        Assert.False(error.IsCancellation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsMissingMessage(string? message)
    {
        Assert.ThrowsAny<ArgumentException>(() => new OperationError(ErrorCode.Unexpected, message!));
    }

    [Fact]
    public void ConstructorRejectsUndefinedCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationError((ErrorCode)999, "x"));
    }

    [Fact]
    public void CancelledFactoryIsCancellationAndNotRetryable()
    {
        var error = OperationError.Cancelled();

        Assert.Equal(ErrorCode.Cancelled, error.Code);
        Assert.True(error.IsCancellation);
        Assert.False(error.Retryable);
        Assert.Equal(OperationError.CancelledMessage, error.SafeMessage);
    }

    [Fact]
    public void EqualityIsByValue()
    {
        var error = new OperationError(ErrorCode.Timeout, "Timed out.", retryable: true);

        Assert.Equal(new OperationError(ErrorCode.Timeout, "Timed out.", true), error);
        Assert.NotEqual(new OperationError(ErrorCode.Timeout, "Timed out.", false), error);
    }
}

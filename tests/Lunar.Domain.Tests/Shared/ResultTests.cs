using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Domain.Tests.Shared;

public sealed class ResultTests
{
    private static readonly OperationError Denied = new(ErrorCode.AccessDenied, "Access denied.");

    [Fact]
    public void SuccessHasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.False(result.IsCancelled);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FailureCarriesError()
    {
        var result = Result.Failure(Denied);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Same(Denied, result.Error);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public void FailureRejectsNullError()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result.Failure<int>(null!));
    }

    [Fact]
    public void CancelledIsFailureWithCancelledCode()
    {
        var result = Result.Cancelled();

        Assert.True(result.IsFailure);
        Assert.True(result.IsCancelled);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public void FailureWithOtherCodeIsNotCancelled()
    {
        Assert.False(Result.Failure(new OperationError(ErrorCode.Timeout, "Timed out.")).IsCancelled);
    }

    [Fact]
    public void GenericSuccessExposesValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(42, value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GenericFailureHasNoValue()
    {
        var result = Result.Failure<string>(Denied);

        Assert.True(result.IsFailure);
        Assert.Same(Denied, result.Error);
        Assert.False(result.TryGetValue(out var value));
        Assert.Null(value);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void GenericCancelledIsCancellation()
    {
        var result = Result.Cancelled<int>();

        Assert.True(result.IsCancelled);
        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void MapTransformsSuccessAndPropagatesFailureWithoutCallingMapper()
    {
        Assert.Equal(6, Result.Success(3).Map(x => x * 2).Value);

        var called = false;
        var failed = Result.Failure<int>(Denied).Map(x =>
        {
            called = true;
            return x;
        });

        Assert.False(called);
        Assert.Same(Denied, failed.Error);
    }

    [Fact]
    public void MatchSelectsBranch()
    {
        Assert.Equal("ok:1", Result.Success(1).Match(v => $"ok:{v}", e => "err"));
        Assert.Equal("err:AccessDenied", Result.Failure<int>(Denied).Match(v => "ok", e => $"err:{e.Code}"));
        Assert.Equal("ok", Result.Success().Match(() => "ok", e => "err"));
        Assert.Equal("err", Result.Failure(Denied).Match(() => "ok", e => "err"));
    }

    [Fact]
    public void ToResultKeepsOutcome()
    {
        Assert.True(Result.Success(1).ToResult().IsSuccess);
        Assert.Same(Denied, Result.Failure<int>(Denied).ToResult().Error);
    }

    [Fact]
    public void NullableValueSuccessIsAllowed()
    {
        var result = Result.Success<string?>(null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }
}

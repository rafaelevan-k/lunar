namespace Lunar.Domain.Shared;

/// <summary>
/// Outcome of an operation without a value. Expected failures are returned as a typed
/// <see cref="OperationError"/>; unexpected faults remain exceptions. Cancellation is reported as a failure
/// whose code is <see cref="ErrorCode.Cancelled"/> (see <see cref="IsCancelled"/>), converted from
/// <see cref="OperationCanceledException"/> at the adapter or use-case boundary.
/// </summary>
public sealed class Result
{
    private static readonly Result Ok = new(null);

    private Result(OperationError? error) => Error = error;

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public bool IsCancelled => Error?.IsCancellation == true;

    /// <summary>Null when <see cref="IsSuccess"/>.</summary>
    public OperationError? Error { get; }

    public static Result Success() => Ok;

    public static Result Failure(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result Cancelled() => new(OperationError.Cancelled());

    public static Result<T> Success<T>(T value) => Result<T>.FromValue(value);

    public static Result<T> Failure<T>(OperationError error) => Result<T>.FromError(error);

    public static Result<T> Cancelled<T>() => Result<T>.FromError(OperationError.Cancelled());

    public TOut Match<TOut>(Func<TOut> onSuccess, Func<OperationError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return Error is null ? onSuccess() : onFailure(Error);
    }
}

/// <summary>Outcome of an operation that yields a <typeparamref name="T"/> on success.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, OperationError? error)
    {
        _value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public bool IsCancelled => Error?.IsCancellation == true;

    /// <summary>Null when <see cref="IsSuccess"/>.</summary>
    public OperationError? Error { get; }

    /// <summary>The success value; throws <see cref="InvalidOperationException"/> on failure.</summary>
    public T Value => Error is null
        ? _value!
        : throw new InvalidOperationException("A failed result has no value. Check IsSuccess or use TryGetValue.");

    public bool TryGetValue([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return Error is null;
    }

    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Error is null ? Result<TOut>.FromValue(map(_value!)) : Result<TOut>.FromError(Error);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<OperationError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return Error is null ? onSuccess(_value!) : onFailure(Error);
    }

    /// <summary>Drops the value, keeping success/failure and the error.</summary>
    public Result ToResult() => Error is null ? Result.Success() : Result.Failure(Error);

    internal static Result<T> FromValue(T value) => new(value, null);

    internal static Result<T> FromError(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }
}

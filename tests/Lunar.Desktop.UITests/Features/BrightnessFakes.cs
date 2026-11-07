using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.UITests.Features;

/// <summary>A display's brightness in memory, for the brightness card tests; reads and writes can fail on request.</summary>
internal sealed class FakeBrightnessService : IBrightnessService
{
    private readonly Lock _gate = new();

    public BrightnessState State { get; set; } = new(40, BrightnessSource.BuiltIn, CapabilityStatus.Supported);

    public ErrorCode? FailReadWith { get; set; }

    public ErrorCode? FailWriteWith { get; set; }

    public List<(string? Display, int Percent)> Writes { get; } = [];

    public List<string?> Reads { get; } = [];

    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    public event Action<string>? Changed;

    public void RaiseChanged(string displayId) => Changed?.Invoke(displayId);

    public Task<Result<BrightnessState>> GetAsync(string? displayId, CancellationToken ct)
    {
        lock (_gate)
            Reads.Add(displayId);
        return Task.FromResult(FailReadWith is { } code
            ? Result.Failure<BrightnessState>(new OperationError(code, "No."))
            : Result.Success(State));
    }

    public Task<Result> SetAsync(string? displayId, int percent, CancellationToken ct)
    {
        lock (_gate)
            Writes.Add((displayId, percent));
        if (FailWriteWith is { } code)
            return Task.FromResult(Result.Failure(new OperationError(code, "No.")));
        State = new BrightnessState(percent, State.Source, State.Capability);
        return Task.FromResult(Result.Success());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A clock the test moves by hand; only timestamps are used by the card.</summary>
internal sealed class ManualTime : TimeProvider
{
    public long Ticks { get; set; } = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Ticks;

    public void Advance(TimeSpan by) => Ticks += by.Ticks;
}

using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;

namespace Lunar.Application.Tests.Fakes;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset LocalNow { get; set; } = now;
}

public sealed class FakeSettingsLauncher : ISystemSettingsLauncher
{
    private readonly List<SettingsPage> _opened = [];

    public IReadOnlyList<SettingsPage> Opened => _opened;

    public bool Fail { get; set; }

    public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());
        if (Fail)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "Settings could not be opened.")));

        _opened.Add(page);
        return Task.FromResult(Result.Success());
    }
}

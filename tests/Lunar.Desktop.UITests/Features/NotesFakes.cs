using Lunar.Application.Abstractions.Notes;
using Lunar.Desktop.Shell;
using Lunar.Domain.Notes;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.UITests.Features;

/// <summary>An in-memory notes file for the Notes page tests; it can fail to load or save on request.</summary>
internal sealed class FakeNotesRepository : INotesRepository
{
    public List<Note> Stored { get; set; } = [];

    public NotesRecovery Recovery { get; set; } = NotesRecovery.None;

    public ErrorCode? FailLoadWith { get; set; }

    public ErrorCode? FailSaveWith { get; set; }

    public int Saves { get; private set; }

    public Task<Result<NotesLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FailLoadWith is { } code
            ? Result.Failure<NotesLoadResult>(new OperationError(code, "Cannot read."))
            : Result.Success(new NotesLoadResult(Stored.ToList(), Recovery)));

    public Task<Result> SaveAsync(IReadOnlyList<Note> notes, CancellationToken cancellationToken)
    {
        Saves++;
        if (FailSaveWith is { } code)
            return Task.FromResult(Result.Failure(new OperationError(code, "Cannot write.")));
        Stored = notes.ToList();
        return Task.FromResult(Result.Success());
    }
}

/// <summary>A one-shot timer the test fires by hand.</summary>
internal sealed class ManualTimer : IOneShotTimer
{
    public Action? Pending { get; private set; }

    public TimeSpan? Delay { get; private set; }

    public void Schedule(TimeSpan delay, Action callback)
    {
        Delay = delay;
        Pending = callback;
    }

    public void Cancel()
    {
        Pending = null;
        Delay = null;
    }

    public void Fire()
    {
        var callback = Pending ?? throw new InvalidOperationException("Nothing is scheduled.");
        Pending = null;
        callback();
    }

    public void Dispose() => Cancel();
}

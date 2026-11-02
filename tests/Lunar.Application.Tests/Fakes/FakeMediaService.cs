using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Tests.Fakes;

public sealed class FakeMediaService : IMediaService
{
    private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();
    private readonly List<string> _commands = [];
    private List<MediaSessionSnapshot> _sessions = [];

    public IReadOnlyList<string> Commands => _commands;

    public bool IsDisposed { get; private set; }

    public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

    public void SetSessions(params MediaSessionSnapshot[] sessions)
    {
        _sessions = [.. sessions];
        _changes.Publish(_sessions.ToArray());
    }

    public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
        Task.FromResult(ct.IsCancellationRequested
            ? Result.Cancelled<IReadOnlyList<MediaSessionSnapshot>>()
            : Result.Success<IReadOnlyList<MediaSessionSnapshot>>(_sessions.ToArray()));

    public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) =>
        Command("playpause", sessionId, session => session.Capabilities.CanPlayPause, ct);

    public Task<Result> NextAsync(string sessionId, CancellationToken ct) =>
        Command("next", sessionId, session => session.Capabilities.CanNext, ct);

    public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) =>
        Command("previous", sessionId, session => session.Capabilities.CanPrevious, ct);

    public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) =>
        Command("seek", sessionId, session => session.CanSeek, ct);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _changes.Complete();
        return ValueTask.CompletedTask;
    }

    private Task<Result> Command(string name, string sessionId, Func<MediaSessionSnapshot, bool> supported, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(Result.Cancelled());

        var session = _sessions.Find(s => s.Id == sessionId);
        if (session is null)
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.InvalidInput, "Unknown media session.")));
        if (!supported(session))
            return Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "The session does not support this command.")));

        _commands.Add(name);
        return Task.FromResult(Result.Success());
    }
}

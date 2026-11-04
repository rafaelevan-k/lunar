using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Tests.Fakes;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Application.Tests.Media;

public sealed class MediaTransportTests
{
    /// <summary>A media service whose commands can be held and answered by the test.</summary>
    private sealed class ScriptedMedia : IMediaService
    {
        private readonly List<TaskCompletionSource<Result>> _held = [];

        public List<string> Sent { get; } = [];

        public bool Hold { get; set; }

        public Result Answer { get; set; } = Result.Success();

        public int ConcurrentMax { get; private set; }

        private int _running;

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => new TestObservable<IReadOnlyList<MediaSessionSnapshot>>();

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Success<IReadOnlyList<MediaSessionSnapshot>>([]));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Run("playpause:" + sessionId);

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Run("next:" + sessionId);

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Run("previous:" + sessionId);

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Run("seek:" + sessionId);

        public void ReleaseOne(Result? result = null)
        {
            var gate = _held[0];
            _held.RemoveAt(0);
            gate.SetResult(result ?? Result.Success());
        }

        public int HeldCount => _held.Count;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<Result> Run(string name)
        {
            Sent.Add(name);
            ConcurrentMax = Math.Max(ConcurrentMax, Interlocked.Increment(ref _running));
            try
            {
                if (!Hold)
                    return Answer;
                var gate = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_held)
                    _held.Add(gate);
                return await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static MediaSessionSnapshot Session(string id = "a", bool playPause = true, bool next = true, bool previous = true) =>
        new(id, "App", "Song", "Artist", MediaPlaybackStatus.Playing, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(100),
            new MediaCapabilities(playPause, next, previous, CanSeek: true), isCurrent: true, ReadOnlyMemory<byte>.Empty);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- what is offered ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MediaCommand.PlayPause, true, false, false, true)]
    [InlineData(MediaCommand.PlayPause, false, true, true, false)]
    [InlineData(MediaCommand.Next, false, true, false, true)]
    [InlineData(MediaCommand.Next, true, false, true, false)]
    [InlineData(MediaCommand.Previous, false, false, true, true)]
    [InlineData(MediaCommand.Previous, true, true, false, false)]
    public void ACommandIsSupportedOnlyWhenTheSessionAdvertisesIt(MediaCommand command, bool playPause, bool next, bool previous, bool expected) =>
        Assert.Equal(expected, MediaTransport.IsSupported(Session(playPause: playPause, next: next, previous: previous), command));

    [Fact]
    public void AnUnknownCommandIsNeverSupported() =>
        Assert.False(MediaTransport.IsSupported(Session(), (MediaCommand)99));

    // ---- sending -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MediaCommand.PlayPause, "playpause:a")]
    [InlineData(MediaCommand.Next, "next:a")]
    [InlineData(MediaCommand.Previous, "previous:a")]
    public async Task ASupportedCommandReachesTheServiceForThatSession(MediaCommand command, string expected)
    {
        var media = new ScriptedMedia();

        var result = await new MediaTransport(media).ExecuteAsync(Session("a"), command, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([expected], media.Sent);
    }

    [Theory]
    [InlineData(MediaCommand.PlayPause)]
    [InlineData(MediaCommand.Next)]
    [InlineData(MediaCommand.Previous)]
    public async Task AnUnsupportedCommandIsRefusedAndNeverSent(MediaCommand command)
    {
        var media = new ScriptedMedia();
        var none = Session(playPause: false, next: false, previous: false);

        var result = await new MediaTransport(media).ExecuteAsync(none, command, Ct);

        Assert.Equal(ErrorCode.Unsupported, result.Error!.Code);
        Assert.Empty(media.Sent);
    }

    [Fact]
    public async Task AnUnknownCommandIsInvalidInputAndNeverSent()
    {
        var media = new ScriptedMedia();

        var result = await new MediaTransport(media).ExecuteAsync(Session(), (MediaCommand)99, Ct);

        Assert.Equal(ErrorCode.InvalidInput, result.Error!.Code);
        Assert.Empty(media.Sent);
    }

    [Fact]
    public async Task AFailureFromTheServiceIsReturnedAsIs()
    {
        var media = new ScriptedMedia { Answer = Result.Failure(new OperationError(ErrorCode.Unexpected, "The player did not accept the command.", retryable: true)) };

        var result = await new MediaTransport(media).ExecuteAsync(Session(), MediaCommand.Next, Ct);

        Assert.Equal(ErrorCode.Unexpected, result.Error!.Code);
        Assert.True(result.Error.Retryable);
    }

    [Fact]
    public async Task NothingIsAssumedAboutTheResultingState()
    {
        var media = new ScriptedMedia();
        var session = Session();

        _ = await new MediaTransport(media).ExecuteAsync(session, MediaCommand.PlayPause, Ct);

        Assert.Equal(MediaPlaybackStatus.Playing, session.Status); // the snapshot is not touched; the state arrives through the service's changes
    }

    // ---- order and cancellation --------------------------------------------------------------------------------------

    [Fact]
    public async Task CommandsGoOneAtATimeInTheOrderGiven()
    {
        var media = new ScriptedMedia { Hold = true };
        var transport = new MediaTransport(media);

        var first = transport.ExecuteAsync(Session("a"), MediaCommand.Next, Ct);
        var second = transport.ExecuteAsync(Session("b"), MediaCommand.Previous, Ct);
        var third = transport.ExecuteAsync(Session("c"), MediaCommand.PlayPause, Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(["next:a"], media.Sent); // the others wait
        media.ReleaseOne();
        await first;
        await WaitFor(() => media.HeldCount == 1);
        Assert.Equal(["next:a", "previous:b"], media.Sent);
        media.ReleaseOne();
        await second;
        await WaitFor(() => media.HeldCount == 1);
        media.ReleaseOne();
        await third;

        Assert.Equal(["next:a", "previous:b", "playpause:c"], media.Sent);
        Assert.Equal(1, media.ConcurrentMax);
    }

    [Fact]
    public async Task AWaitingCommandCanBeCancelledAndIsNeverSent()
    {
        var media = new ScriptedMedia { Hold = true };
        var transport = new MediaTransport(media);
        var first = transport.ExecuteAsync(Session("a"), MediaCommand.Next, Ct);
        await WaitFor(() => media.HeldCount == 1);
        using var cts = new CancellationTokenSource();
        var waiting = transport.ExecuteAsync(Session("b"), MediaCommand.Next, cts.Token);

        await cts.CancelAsync();
        var result = await waiting;

        Assert.True(result.IsCancelled);
        media.ReleaseOne();
        await first;
        Assert.Equal(["next:a"], media.Sent);

        // And the transport still works afterwards.
        media.Hold = false;
        Assert.True((await transport.ExecuteAsync(Session("c"), MediaCommand.Next, Ct)).IsSuccess);
    }

    [Fact]
    public async Task AlreadyCancelledNeverReachesTheService()
    {
        var media = new ScriptedMedia();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new MediaTransport(media).ExecuteAsync(Session(), MediaCommand.Next, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(media.Sent);
    }

    [Fact]
    public async Task ACommandFailingDoesNotBlockTheNext()
    {
        var media = new ScriptedMedia { Answer = Result.Failure(new OperationError(ErrorCode.Unexpected, "x")) };
        var transport = new MediaTransport(media);
        Assert.True((await transport.ExecuteAsync(Session(), MediaCommand.Next, Ct)).IsFailure);

        media.Answer = Result.Success();

        Assert.True((await transport.ExecuteAsync(Session(), MediaCommand.Next, Ct)).IsSuccess);
    }

    [Fact]
    public async Task ManyCommandsFromManyThreadsNeverOverlap()
    {
        var media = new ScriptedMedia();
        var transport = new MediaTransport(media);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => transport.ExecuteAsync(Session("s" + i), MediaCommand.Next, Ct), Ct)));

        Assert.Equal(200, media.Sent.Count);
        Assert.Equal(1, media.ConcurrentMax);
    }

    [Fact]
    public async Task MissingArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MediaTransport(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new MediaTransport(new ScriptedMedia()).ExecuteAsync(null!, MediaCommand.Next, Ct));
        Assert.Throws<ArgumentNullException>(() => MediaTransport.IsSupported(null!, MediaCommand.Next));
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(5, Ct);
        Assert.True(condition(), "Condition not reached in time.");
    }
}

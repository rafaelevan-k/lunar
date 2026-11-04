using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

// These use the real media sessions of the machine they run on and only READ. They never send a command, never change
// playback, and never write a title, artist, app name or any other media detail to the output or a log: only counts and
// yes/no facts are asserted. A machine with no player running (a build server) has no session, which is a valid answer.
// What a real player does when told to play or seek is the work of BE-16 and BE-17.
public sealed class GsmtcRealSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsTheRealSessionsWithoutAdministratorRights()
    {
        await using var service = new GsmtcMediaService();

        var read = await service.GetSessionsAsync(Ct);

        Assert.True(read.IsSuccess, read.Error?.SafeMessage);
        var sessions = read.Value;
        TestContext.Current.SendDiagnosticMessage($"Real media sessions: {sessions.Count}, current {sessions.Count(s => s.IsCurrent)}, seekable {sessions.Count(s => s.CanSeek)}");
        Assert.True(sessions.Count(s => s.IsCurrent) <= 1);
        Assert.Equal(sessions.Count, sessions.Select(s => s.Id).Distinct().Count());
        Assert.All(sessions, session =>
        {
            Assert.False(string.IsNullOrWhiteSpace(session.Id));
            Assert.True(session.Artwork.Length <= MediaSessionSnapshot.MaxArtworkBytes);
            if (session.Duration is { } duration)
            {
                Assert.True(duration > TimeSpan.Zero);
                Assert.NotNull(session.Position);
                Assert.InRange(session.Position!.Value, TimeSpan.Zero, duration);
            }
            else
            {
                Assert.Null(session.Position);
                Assert.False(session.CanSeek);
            }
        });
    }

    [Fact]
    public async Task ReadingRepeatedlyAndDisposingReleasesEverythingCleanly()
    {
        var service = new GsmtcMediaService();
        for (var i = 0; i < 20; i++)
            Assert.True((await service.GetSessionsAsync(Ct)).IsSuccess);

        await service.DisposeAsync();

        Assert.True((await service.GetSessionsAsync(Ct)).IsFailure);
    }

    [Fact]
    public async Task ChangesCompleteOnDisposal()
    {
        var service = new GsmtcMediaService();
        var completed = new TaskCompletionSource();
        service.Changes.Subscribe(new Completion(completed));
        _ = await service.GetSessionsAsync(Ct);

        await service.DisposeAsync();

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task CommandsDoNotTouchTheRealPlayers()
    {
        await using var service = new GsmtcMediaService();
        var sessions = (await service.GetSessionsAsync(Ct)).Value;
        var id = sessions.Count > 0 ? sessions[0].Id : "no-such-session";

        var results = new[]
        {
            await service.PlayPauseAsync(id, Ct),
            await service.NextAsync(id, Ct),
            await service.PreviousAsync(id, Ct),
            await service.SeekAsync(id, TimeSpan.FromSeconds(1), Ct)
        };

        // They fail before reaching Windows (the service has no code path that sends a command yet), and the service still reads.
        // What the sessions do meanwhile is the user's own doing and is not compared.
        Assert.All(results, r => Assert.Equal(ErrorCode.Unsupported, r.Error!.Code));
        Assert.True((await service.GetSessionsAsync(Ct)).IsSuccess);
    }

    private sealed class Completion(TaskCompletionSource done) : IObserver<IReadOnlyList<MediaSessionSnapshot>>
    {
        public void OnNext(IReadOnlyList<MediaSessionSnapshot> value)
        {
        }

        public void OnError(Exception error) => done.TrySetException(error);

        public void OnCompleted() => done.TrySetResult();
    }
}

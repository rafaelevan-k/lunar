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

    // Regression for a real bug: a second read right after the first lost every session (the session objects are bound to the
    // thread that first used them). Counting on a real player being open, so it only asserts when the first read saw one.
    [Fact]
    public async Task ReadsOneAfterTheOtherAgreeInsteadOfLosingTheSessions()
    {
        await using var service = new GsmtcMediaService();
        var first = (await service.GetSessionsAsync(Ct)).Value;
        if (first.Count == 0)
        {
            Assert.Skip("No media session is open on this machine, so there is nothing to lose between reads.");
            return;
        }

        var seen = 0;
        for (var i = 0; i < 10; i++)
        {
            var read = await service.GetSessionsAsync(Ct);
            Assert.True(read.IsSuccess);
            if (read.Value.Any(s => s.Id == first[0].Id))
                seen++;
        }

        Assert.True(seen >= 9, $"The first session was in {seen} of 10 immediate reads.");
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

    // The one test in this file that sends a real command. It is skipped unless LUNAR_REAL_MEDIA_COMMANDS=1 is set, because it
    // really pauses and resumes whatever the current player is playing for a moment. It sends nothing else (no next, no
    // previous, no seek) and leaves the player playing again; it never prints or asserts anything about the media itself.
    [Fact]
    public async Task RealSmokePauseThenPlayTheCurrentPlayer()
    {
        if (System.Environment.GetEnvironmentVariable("LUNAR_REAL_MEDIA_COMMANDS") != "1")
        {
            Assert.Skip("Set LUNAR_REAL_MEDIA_COMMANDS=1 to pause and resume the current player once.");
            return;
        }

        await using var service = new GsmtcMediaService();
        var sessions = (await service.GetSessionsAsync(Ct)).Value;
        var current = sessions.FirstOrDefault(s => s.IsCurrent && s.Status == MediaPlaybackStatus.Playing && s.Capabilities.CanPlayPause);
        if (current is null)
        {
            Assert.Skip("No current session is playing and offering play/pause.");
            return;
        }

        var paused = false;
        try
        {
            var pause = await service.PlayPauseAsync(current.Id, Ct);
            Assert.True(pause.IsSuccess, $"Pause failed: {pause.Error?.Code} {pause.Error?.SafeMessage}");
            paused = true;
            Assert.Equal(MediaPlaybackStatus.Paused, await StatusOf(service, current.Id, MediaPlaybackStatus.Paused));
        }
        finally
        {
            if (paused)
            {
                Assert.True((await service.PlayPauseAsync(current.Id, CancellationToken.None)).IsSuccess);
                Assert.Equal(MediaPlaybackStatus.Playing, await StatusOf(service, current.Id, MediaPlaybackStatus.Playing));
            }
        }
    }

    // Sends one real seek, to a seekable current session, and puts the position back. Skipped unless LUNAR_REAL_MEDIA_SEEK=1.
    // It moves the playback position of the current player by a few seconds and back; it sends nothing else and never prints or
    // asserts anything about the media itself.
    [Fact]
    public async Task RealSmokeSeekTheCurrentPlayerAndPutItBack()
    {
        if (System.Environment.GetEnvironmentVariable("LUNAR_REAL_MEDIA_SEEK") != "1")
        {
            Assert.Skip("Set LUNAR_REAL_MEDIA_SEEK=1 to seek the current player once and put it back.");
            return;
        }

        await using var service = new GsmtcMediaService();
        var sessions = (await service.GetSessionsAsync(Ct)).Value;
        var current = sessions.FirstOrDefault(s => s.IsCurrent && s.CanSeek && s.Position is { } p && s.Duration is { } d && d - p > TimeSpan.FromSeconds(30));
        if (current is null)
        {
            Assert.Skip("No current session is seekable with at least 30 seconds left.");
            return;
        }

        var original = current.Position!.Value;
        var seek = new Lunar.Application.Media.UseCases.SeekMedia(service);
        try
        {
            var outcome = await seek.ExecuteAsync(current, original + TimeSpan.FromSeconds(10), Ct);
            Assert.True(outcome.IsSuccess, $"Seek failed: {outcome.Error?.Code} {outcome.Error?.SafeMessage}");
            TestContext.Current.SendDiagnosticMessage($"Real seek: settled {outcome.Value.Settled}, moved {(outcome.Value.Position - original)?.TotalSeconds:F1} s");
            Assert.NotNull(outcome.Value.Position);
        }
        finally
        {
            var fresh = (await service.GetSessionsAsync(CancellationToken.None)).Value.FirstOrDefault(s => s.Id == current.Id);
            if (fresh is not null)
                _ = await new Lunar.Application.Media.UseCases.SeekMedia(service).ExecuteAsync(fresh, original, CancellationToken.None);
        }
    }

    // Reads until the session shows the wanted state (the player needs a moment) or two seconds pass, and returns what it saw last.
    private static async Task<MediaPlaybackStatus> StatusOf(GsmtcMediaService service, string id, MediaPlaybackStatus wanted)
    {
        var seen = MediaPlaybackStatus.Unknown;
        for (var i = 0; i < 20 && seen != wanted; i++)
        {
            await Task.Delay(100, Ct);
            var read = await service.GetSessionsAsync(Ct);
            seen = read.Value.FirstOrDefault(s => s.Id == id)?.Status ?? MediaPlaybackStatus.Unknown;
        }

        return seen;
    }

    [Fact]
    public async Task CommandsToAnUnknownSessionAreRefusedWithoutTouchingAnyRealPlayer()
    {
        await using var service = new GsmtcMediaService();

        var results = new[]
        {
            await service.PlayPauseAsync("no-such-session", Ct),
            await service.NextAsync("no-such-session", Ct),
            await service.PreviousAsync("no-such-session", Ct)
        };

        Assert.All(results, r => Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code));
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

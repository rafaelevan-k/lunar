using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Media;

/// <summary>
/// Media sessions and transport. Commands succeed only when the OS reports success. Seeking on a session
/// without a finite duration or <see cref="MediaCapabilities.CanSeek"/> fails with
/// <see cref="ErrorCode.Unsupported"/>; an unknown session id fails with <see cref="ErrorCode.InvalidInput"/>.
/// </summary>
public interface IMediaService : IAsyncDisposable
{
    Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct);

    Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct);

    Task<Result> NextAsync(string sessionId, CancellationToken ct);

    Task<Result> PreviousAsync(string sessionId, CancellationToken ct);

    Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct);

    /// <summary>
    /// Emits the full current session list; a previously seen id that is missing means that session closed.
    /// Other observable semantics are those documented on <see cref="Lunar.Application.Abstractions.System.IVolumeService.Changes"/>.
    /// </summary>
    IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes { get; }
}

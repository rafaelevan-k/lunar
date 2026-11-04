using Lunar.Application.Abstractions.Media;
using Lunar.Application.Media.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Media.UseCases;

/// <summary>
/// Play or pause, next and previous for a media session (BE-16). It sits between the UI and <see cref="IMediaService"/> for
/// two reasons: a command a session does not advertise is refused here, with a typed error, before anything is sent (the UI
/// uses <see cref="IsSupported"/> to disable the button, and this is the guard if it is ever asked anyway); and commands go
/// out one at a time, in the order they were given, so two quick presses cannot overtake each other.
///
/// A command succeeds only when the service says Windows and the player accepted it. It does not guess the resulting state:
/// the player's new state arrives through the service's changes. A cancelled call does not undo a command that was already
/// sent. Thread-safe.
/// </summary>
public sealed class MediaTransport
{
    private readonly IMediaService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MediaTransport(IMediaService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <summary>True when the session advertises the command (the rule the UI uses to enable or disable its button).</summary>
    public static bool IsSupported(MediaSessionSnapshot session, MediaCommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        return command switch
        {
            MediaCommand.PlayPause => session.Capabilities.CanPlayPause,
            MediaCommand.Next => session.Capabilities.CanNext,
            MediaCommand.Previous => session.Capabilities.CanPrevious,
            _ => false
        };
    }

    public async Task<Result> ExecuteAsync(MediaSessionSnapshot session, MediaCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(command))
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Unknown media command."));
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (!IsSupported(session, command))
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "This player does not offer that command."));

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }

        try
        {
            if (ct.IsCancellationRequested)
                return Result.Cancelled();

            return command switch
            {
                MediaCommand.PlayPause => await _service.PlayPauseAsync(session.Id, ct).ConfigureAwait(false),
                MediaCommand.Next => await _service.NextAsync(session.Id, ct).ConfigureAwait(false),
                _ => await _service.PreviousAsync(session.Id, ct).ConfigureAwait(false)
            };
        }
        finally
        {
            _gate.Release();
        }
    }
}

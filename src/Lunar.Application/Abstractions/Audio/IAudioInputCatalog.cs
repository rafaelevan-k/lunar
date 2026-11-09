using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Audio;

/// <summary>The sound inputs of this PC (ADR-0022). Listing opens no microphone.</summary>
public interface IAudioInputCatalog
{
    /// <summary>The enabled inputs; none is a successful empty list. Windows' default comes first. Failures are typed, cancellation included.</summary>
    Task<Result<IReadOnlyList<AudioInput>>> GetInputsAsync(CancellationToken ct);

    /// <summary>
    /// An input was added or removed, or the Windows default changed: read the list again. Raised on a background thread, possibly several
    /// times for one change. Handlers must marshal to the UI and must not throw (a throwing handler is isolated).
    /// </summary>
    event Action? Changed;
}

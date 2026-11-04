namespace Lunar.Application.Abstractions.Conversion;

/// <summary>
/// Whether the optional program that lays out Word documents (a separately installed LibreOffice) is available. The planner asks
/// so that a route needing it is refused with a clear message instead of failing later. Detection itself (finding and checking
/// the program, never downloading or installing it) belongs to the renderer task (BE-25).
/// </summary>
public interface IRendererAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

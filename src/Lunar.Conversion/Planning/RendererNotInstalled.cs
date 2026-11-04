using Lunar.Application.Abstractions.Conversion;

namespace Lunar.Conversion.Planning;

/// <summary>
/// Says that no document layout engine is available. This is the honest answer until the renderer task (BE-25) detects an installed
/// LibreOffice and proves its fidelity (ADR-0005): the planner then refuses every Word route with "needs LibreOffice" instead of
/// promising a conversion that cannot run. BE-25 replaces this registration; nothing else changes.
/// </summary>
public sealed class RendererNotInstalled : IRendererAvailability
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
}

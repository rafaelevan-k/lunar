using Lunar.Application.Abstractions.Conversion;
using Lunar.Conversion.Providers;

namespace Lunar.Conversion.Planning;

/// <summary>
/// Whether Word documents can be converted right now (BE-25): true only when the routes are switched on in this version AND the
/// user's own LibreOffice is found. The planner asks this so that a Word route is refused with a clear message instead of failing later.
///
/// <paramref name="routesEnabled"/> is the hold of ADR-0005: Lunar does not promise Word routes until a fidelity check on a real
/// LibreOffice has been done, so the composition root passes <c>false</c> and this answers "no" without even looking. Lifting the hold is
/// that one argument, after the check and with the user's approval.
/// </summary>
public sealed class LibreOfficeAvailability : IRendererAvailability
{
    private readonly ILibreOfficeLocator _locator;
    private readonly bool _routesEnabled;

    public LibreOfficeAvailability(ILibreOfficeLocator locator, bool routesEnabled)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _routesEnabled = routesEnabled;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<bool>(ct);

        // Looking is cheap (two file checks), but it is done off the caller's thread like every file access.
        return _routesEnabled ? Task.Run(() => _locator.Locate() is not null, CancellationToken.None) : Task.FromResult(false);
    }
}

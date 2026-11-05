namespace Lunar.Application.Abstractions.System;

/// <summary>What Windows says about light and dark mode (ADR-0016). Read-only; a failed read means dark.</summary>
public interface ISystemThemeSource
{
    /// <summary>True when Windows apps are set to the light theme.</summary>
    bool PrefersLight { get; }

    /// <summary>Raised, on any thread, when the user changes the Windows theme.</summary>
    event Action? Changed;
}

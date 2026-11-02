namespace Lunar.Desktop.Shell;

/// <summary>
/// What the island currently shows. This is presentation state owned by <see cref="IslandViewModel"/>; the
/// validated shell state machine with guards and timing is BE-09 and will drive this later.
/// </summary>
public enum IslandVisualState
{
    /// <summary>The compact pill.</summary>
    Idle,

    /// <summary>The expansion animation is running.</summary>
    Expanding,

    /// <summary>Expanded and showing its pages.</summary>
    Expanded,

    /// <summary>The collapse animation is running.</summary>
    Collapsing,

    /// <summary>Expanded while a background job (for example a conversion) is active.</summary>
    Busy
}

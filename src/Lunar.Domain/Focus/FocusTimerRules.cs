namespace Lunar.Domain.Focus;

/// <summary>Where a focus session stands (ADR-0023). <see cref="Finished"/> is a resting state: a new session may start from it.</summary>
public enum PomodoroPhase
{
    Idle,
    Running,
    Paused,
    Finished
}

/// <summary>The fixed rules of a focus session (ADR-0023).</summary>
public static class FocusTimerRules
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 180;
    public const int DefaultMinutes = 25;

    public static IReadOnlyList<int> PresetMinutes { get; } = [15, 25, 45, 60];

    public static bool IsValid(int minutes) => minutes is >= MinMinutes and <= MaxMinutes;

    public static bool IsActive(PomodoroPhase phase) => phase is PomodoroPhase.Running or PomodoroPhase.Paused;
}

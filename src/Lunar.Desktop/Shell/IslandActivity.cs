namespace Lunar.Desktop.Shell;

/// <summary>What the collapsed island shows (ADR-0024).</summary>
public enum IslandActivity
{
    Eyes,
    Recording,
    FocusCountdown
}

/// <summary>The one rule for what the collapsed island shows: a recording has controls that must stay reachable, so it wins.</summary>
public static class IslandActivitySelector
{
    public static IslandActivity Choose(bool recording, bool focusSession) =>
        recording ? IslandActivity.Recording
        : focusSession ? IslandActivity.FocusCountdown
        : IslandActivity.Eyes;
}

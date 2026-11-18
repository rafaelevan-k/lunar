namespace Lunar.Desktop.Shell;

/// <summary>What the collapsed island shows (ADR-0024).</summary>
public enum IslandActivity
{
    Eyes,
    Recording,
    FocusCountdown,

    /// <summary>Music or video plays: the thumbnail and sound bars (ADR-0026).</summary>
    MediaPlaying,

    /// <summary>The screen is being read and translated: "Translating…" with Cancel (ADR-0028).</summary>
    Translating
}

/// <summary>The one rule for what the collapsed island shows: a recording has controls that must stay reachable, so it wins.</summary>
public static class IslandActivitySelector
{
    public static IslandActivity Choose(bool recording, bool focusSession) => Choose(recording, focusSession, mediaPlaying: false);

    /// <summary>Recording, then the focus countdown, then music or video, then the eyes (ADR-0024, ADR-0026).</summary>
    public static IslandActivity Choose(bool recording, bool focusSession, bool mediaPlaying) => Choose(recording, translating: false, focusSession, mediaPlaying);

    /// <summary>Recording, then a translation in progress, then the focus countdown, then music or video, then the eyes (ADR-0024, ADR-0026, ADR-0028).</summary>
    public static IslandActivity Choose(bool recording, bool translating, bool focusSession, bool mediaPlaying) =>
        recording ? IslandActivity.Recording
        : translating ? IslandActivity.Translating
        : focusSession ? IslandActivity.FocusCountdown
        : mediaPlaying ? IslandActivity.MediaPlaying
        : IslandActivity.Eyes;
}

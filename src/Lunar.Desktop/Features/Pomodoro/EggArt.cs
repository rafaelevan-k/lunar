namespace Lunar.Desktop.Features.Pomodoro;

/// <summary>The two eggs of the Pomodoro page, one character per pixel (see <see cref="Controls.PixelArtView"/>).</summary>
public static class EggArt
{
    /// <summary>A whole egg, while the time is set or running.</summary>
    public static IReadOnlyList<string> Whole { get; } =
    [
        "....ooo....",
        "...owwwo...",
        "..owwwwwo..",
        "..owhwwwo..",
        ".owwwwwwwo.",
        ".owwwwwwwo.",
        "owwwwwwwwwo",
        "owwwwwwwwso",
        "owwwwwwwwso",
        "owwwwwwwsso",
        ".owwwwwwso.",
        ".owwwwwsso.",
        "..owwssso..",
        "...ooooo...",
    ];

    /// <summary>The egg cracking open, played once between <see cref="Whole"/> and <see cref="Sunny"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Cracking { get; } =
    [
        [
            "....ooo....",
            "...owwwo...",
            "..owwwwwo..",
            "..owhwwwo..",
            ".owwwwwwwo.",
            ".owowwowwo.",
            "owwwowowwwo",
            "owwwwwwwwso",
            "owwwwwwwwso",
            "owwwwwwwsso",
            ".owwwwwwso.",
            ".owwwwwsso.",
            "..owwssso..",
            "...ooooo...",
        ],
        [
            "....ooo....",
            "...owwwo...",
            "..owwwwwo..",
            "..owhwwwo..",
            ".owwwwwwwo.",
            ".owowowowo.",
            "owowowowowo",
            "owwwwwwwwso",
            "owwwwwwwwso",
            "owwwwwwwsso",
            ".owwwwwwso.",
            ".owwwwwsso.",
            "..owwssso..",
            "...ooooo...",
        ],
        [
            ".oo.....oo.",
            "owwo...owwo",
            "owwwo.owwwo",
            ".ooo...ooo.",
            "...........",
            "...........",
            "...........",
            "..o.o.o.o..",
            ".owyyyywwo.",
            "owwyyyyywwo",
            "owwwyyywwso",
            ".owwwwwwso.",
            "..owwssso..",
            "...ooooo...",
        ],
    ];

    /// <summary>A sunny-side-up egg, when the focus time is over.</summary>
    public static IReadOnlyList<string> Sunny { get; } =
    [
        "....oooooo........",
        "..oowwwwwwoooo....",
        ".owwwwwwwwwwwwoo..",
        ".owwwwyyyywwwwwwo.",
        "owwwwyyyyyywwwwwo.",
        "owwwwyyhyyywwwwwwo",
        "owwwwyyyyyywwwwwwo",
        ".owwwwyyyywwwwwwo.",
        ".owwwwwwwwwwwwwo..",
        "..owwwwwwwwwwwo...",
        "...oowwwwwwoo.....",
        ".....oooooo.......",
    ];
}

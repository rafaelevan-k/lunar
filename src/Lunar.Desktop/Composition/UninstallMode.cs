using System.Globalization;

namespace Lunar.Desktop.Composition;

/// <summary>
/// The one command line the installer uses (ADR-0018): <c>--uninstall-cleanup --ui-level=N</c>, run by the MSI only on a full uninstall.
/// N is Windows Installer's UILevel; 2 means silent, so the user is asked about their data only above 2. Anything else on the command
/// line starts Lunar normally. In this mode Lunar shows no island or tray and takes no single-instance role. The installer ignores the
/// exit code (so nothing here can block an uninstall) but writes it to its log, which is how a failed step is diagnosed:
/// 0 done, 2 start-with-Windows entry not removed, 3 data not (fully) deleted, 4 both, 5 unexpected error,
/// 6 refused because the process runs as LocalSystem instead of the user (nothing was changed or asked).
/// </summary>
internal sealed record UninstallMode(bool MayAsk)
{
    internal const string Switch = "--uninstall-cleanup";
    internal const int ExitDone = 0;
    internal const int ExitStartupNotRemoved = 2;
    internal const int ExitDataNotDeleted = 3;
    internal const int ExitBothFailed = 4;
    internal const int ExitUnexpected = 5;
    internal const int ExitRunningAsSystem = 6;
    private const string UiLevelPrefix = "--ui-level=";
    private const int SilentUiLevel = 2;

    /// <summary>The mode for these arguments, or null for a normal start.</summary>
    internal static UninstallMode? From(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase))
            return null;

        var mayAsk = false; // unknown UI level: do not block a possibly unattended uninstall with a question
        foreach (var arg in args.Skip(1))
        {
            if (arg.StartsWith(UiLevelPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg.AsSpan(UiLevelPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var level))
            {
                // UILevel can carry flags above 0x0F (for example 0x20 to hide the cancel button); only the low nibble is the level.
                mayAsk = (level & 0x0F) > SilentUiLevel;
            }
        }

        return new UninstallMode(mayAsk);
    }

    /// <summary>The process exit code for a finished cleanup.</summary>
    internal static int ExitCodeFor(Lunar.Infrastructure.Startup.UninstallCleanupResult result) =>
        (result.Startup.IsSuccess, result.Data.IsSuccess) switch
        {
            (true, true) => ExitDone,
            (false, true) => ExitStartupNotRemoved,
            (true, false) => ExitDataNotDeleted,
            _ => ExitBothFailed
        };
}

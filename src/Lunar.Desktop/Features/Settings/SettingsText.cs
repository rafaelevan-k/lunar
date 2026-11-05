using System.Globalization;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Shell.Models;

namespace Lunar.Desktop.Features.Settings;

/// <summary>The words of the Settings window (FE-23), as pure functions so every case is tested without a window.</summary>
public static class SettingsText
{
    public const string Title = "Settings";

    public const string SavedOnThisPc = "Settings are saved only on this PC.";

    public const string StartupUnavailable = "Not available yet. It arrives with the installer.";

    public const string DiagnosticsHelp = "Writes more detail to Lunar's log files on this PC. Logs never contain file names, file contents, passwords or what you play.";

    public const string DiagnosticsRestart = "Takes effect the next time Lunar starts.";

    public const string FolderDefault = "Next to the files you convert";

    public const string SaveFailed = "Lunar could not save this. Nothing was changed.";

    public const string ReadOnly = "These settings come from a newer version of Lunar, so this version shows them but cannot change them.";

    public const string ResetQuestion = "Reset every setting to its default? This also turns the Usage page off.";

    public const string FolderRejected = "That folder cannot be used. Choose a folder on this PC.";

    /// <summary>The notice for a start-up repair, or null when nothing was repaired.</summary>
    public static string? Recovery(SettingsRecovery recovery) => recovery switch
    {
        SettingsRecovery.RestoredFromBackup => "Your settings file was damaged, so Lunar used the last good copy.",
        SettingsRecovery.ResetToDefaults => "Your settings file was damaged and could not be restored, so Lunar started with the defaults.",
        SettingsRecovery.ValuesReset => "Some saved values were not valid and were set back to their defaults.",
        SettingsRecovery.NewerVersionIgnored => ReadOnly,
        _ => null
    };

    public static string Offset(int pixels) => string.Create(CultureInfo.CurrentCulture, $"{pixels} px");

    /// <summary>"Display 1 (main) 1920 x 1080": a number by position in the list, the main flag, and the size.</summary>
    public static string Monitor(int number, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var main = display.IsPrimary ? " (main)" : string.Empty;
        return string.Create(CultureInfo.CurrentCulture, $"Display {number}{main}, {display.Bounds.Width} x {display.Bounds.Height}");
    }

    public const string MainDisplay = "Main display";

    public const string MissingDisplay = "Saved display (not connected)";
}

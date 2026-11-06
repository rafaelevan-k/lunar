using System.Globalization;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Settings;

/// <summary>The words of the Settings window (FE-23), as pure functions so every case is tested without a window.</summary>
public static class SettingsText
{
    public static string Title => Strings.Get("Settings.Title");

    public static string SavedOnThisPc => Strings.Get("Settings.SavedOnThisPc");

    public static string StartupUnavailable => Strings.Get("Settings.StartupUnavailable");

    public static string StartupFailed => Strings.Get("Settings.StartupFailed");

    /// <summary>The line under "Start with Windows": unavailable, on, or off.</summary>
    public static string Startup(bool available, bool on) =>
        !available ? StartupUnavailable : Strings.Get(on ? "Settings.StartupOn" : "Settings.StartupOff");

    public static string DiagnosticsHelp => Strings.Get("Settings.DiagnosticsHelp");

    public static string DiagnosticsRestart => Strings.Get("Settings.DiagnosticsRestart");

    public static string FolderDefault => Strings.Get("Settings.FolderDefault");

    public static string SaveFailed => Strings.Get("Settings.SaveFailed");

    public static string ReadOnly => Strings.Get("Settings.ReadOnly");

    public static string ResetQuestion => Strings.Get("Settings.ResetQuestion");

    public static string FolderRejected => Strings.Get("Settings.FolderRejected");

    /// <summary>The notice for a start-up repair, or null when nothing was repaired.</summary>
    public static string? Recovery(SettingsRecovery recovery) => recovery switch
    {
        SettingsRecovery.RestoredFromBackup => Strings.Get("Settings.RecoveryBackup"),
        SettingsRecovery.ResetToDefaults => Strings.Get("Settings.RecoveryDefaults"),
        SettingsRecovery.ValuesReset => Strings.Get("Settings.RecoveryValues"),
        SettingsRecovery.NewerVersionIgnored => ReadOnly,
        _ => null
    };

    public static string Offset(int pixels) => Strings.Format("Settings.Offset", pixels);

    /// <summary>"Display 1 (main) 1920 x 1080": a number by position in the list, the main flag, and the size.</summary>
    public static string Monitor(int number, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var main = display.IsPrimary ? Strings.Get("Settings.MonitorMain") : string.Empty;
        return Strings.Format("Settings.Monitor", number, main, display.Bounds.Width, display.Bounds.Height);
    }

    public static string MainDisplay => Strings.Get("Settings.MainDisplay");

    public static string MissingDisplay => Strings.Get("Settings.MissingDisplay");
}

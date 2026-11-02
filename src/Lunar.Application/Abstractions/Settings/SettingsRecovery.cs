namespace Lunar.Application.Abstractions.Settings;

/// <summary>What the repository had to do to produce usable settings; the UI may show a notice for anything but None.</summary>
public enum SettingsRecovery
{
    /// <summary>The file was read as it was.</summary>
    None,

    /// <summary>No file existed; defaults are in use and nothing is written until the first save.</summary>
    CreatedDefaults,

    /// <summary>The main file was unusable; the last good backup was used.</summary>
    RestoredFromBackup,

    /// <summary>The main file and the backup were unusable; defaults are in use.</summary>
    ResetToDefaults,

    /// <summary>The file was read, but one or more invalid values were replaced by their defaults.</summary>
    ValuesReset,

    /// <summary>The file was written by a newer Lunar. It is left untouched; defaults are in use and saving is refused.</summary>
    NewerVersionIgnored
}

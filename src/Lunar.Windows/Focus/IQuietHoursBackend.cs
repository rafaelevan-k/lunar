namespace Lunar.Windows.Focus;

/// <summary>Windows' quiet-hours state, as the control needs it. Failures throw COMException; tests replace this to produce them.</summary>
internal interface IQuietHoursBackend
{
    /// <summary>The selected profile, such as Microsoft.QuietHoursProfile.Unrestricted.</summary>
    string GetProfile();

    void SetProfile(string profile);

    /// <summary>What Windows announces as active: 0 off, 1 priority only, 2 alarms only; null when it cannot be read.</summary>
    int? ReadAnnouncedProfile();
}

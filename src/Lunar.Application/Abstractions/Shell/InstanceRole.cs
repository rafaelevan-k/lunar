namespace Lunar.Application.Abstractions.Shell;

public enum InstanceRole
{
    /// <summary>This process owns the app for the current Windows session.</summary>
    Primary,

    /// <summary>Another instance already runs in this session.</summary>
    Secondary
}

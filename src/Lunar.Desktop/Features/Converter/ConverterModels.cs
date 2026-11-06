using System.Windows.Input;
using Lunar.Desktop.Resources;
using Lunar.Domain.Conversion;

namespace Lunar.Desktop.Features.Converter;

/// <summary>Where one listed file stands after the check.</summary>
public enum ConverterItemStatus
{
    /// <summary>The check has not finished yet.</summary>
    Checking,

    /// <summary>The file passed every check and can be converted to the chosen format.</summary>
    Ready,

    /// <summary>The file was left out on purpose (its result name was taken and the policy is Skip).</summary>
    Skipped,

    /// <summary>The file cannot be converted; <see cref="ConverterItem.Message"/> says why and what to do.</summary>
    Refused,

    /// <summary>The check could not run for the whole list (the message on the page says why).</summary>
    NotChecked
}

/// <summary>One file in the list. The message never holds file content; the name is shown, the full path only in the tooltip.</summary>
/// <param name="FullPath">Where the file is. Shown only as a tooltip, never written to a log.</param>
/// <param name="Name">The file name.</param>
/// <param name="KindText">"PNG picture", "PDF document" ... or "Unknown type".</param>
/// <param name="Status">Where the file stands.</param>
/// <param name="Message">What the status means in words; for a refused file, what to do about it.</param>
/// <param name="RemoveCommand">Takes the file out of the list.</param>
public sealed record ConverterItem(string FullPath, string Name, string KindText, ConverterItemStatus Status, string Message, ICommand RemoveCommand)
{
    public bool IsRefused => Status == ConverterItemStatus.Refused;

    public bool IsNotRefused => Status != ConverterItemStatus.Refused;

    public bool IsReady => Status == ConverterItemStatus.Ready;

    /// <summary>What a screen reader says for the row.</summary>
    public string AutomationName => Strings.Format("Conv.RowAutomation", Name, KindText, Message);
}

/// <summary>One output format the user can pick, with whether it fits every file in the list and why not when it does not.</summary>
/// <param name="Format">The format.</param>
/// <param name="Label">"PNG", "PDF" ...</param>
/// <param name="IsAvailable">True when every recognised file in the list can become this format.</param>
/// <param name="IsSelected">True for the format that will be used.</param>
/// <param name="Note">How it is done when available (for example "one picture per page"), the reason when not.</param>
/// <param name="SelectCommand">Chooses this format; it can run only when the format is available.</param>
public sealed record TargetOption(FileFormat Format, string Label, bool IsAvailable, bool IsSelected, string Note, ICommand SelectCommand)
{
    /// <summary>What a screen reader says for the button.</summary>
    public string AutomationName => Strings.Format("Conv.TargetAutomation", Label, IsAvailable ? (IsSelected ? Strings.Get("Conv.TargetSelectedPrefix") : string.Empty) : Strings.Get("Conv.TargetUnavailablePrefix"), Note);
}

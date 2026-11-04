namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// Lets the user choose a folder for the results. A ViewModel asks through this and never opens a window itself. It is called on the
/// UI thread and returns when the dialog closes: the full path chosen, or null when the user cancelled. It only chooses; it writes nothing.
/// </summary>
public interface IFolderPicker
{
    string? PickFolder();
}

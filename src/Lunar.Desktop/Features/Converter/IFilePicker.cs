namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// Lets the user choose files. A ViewModel asks through this and never opens a window itself. It is called on the UI thread and
/// returns when the dialog closes: the full paths chosen, or null when the user cancelled. It only chooses; it reads nothing.
/// </summary>
public interface IFilePicker
{
    IReadOnlyList<string>? PickFiles();
}

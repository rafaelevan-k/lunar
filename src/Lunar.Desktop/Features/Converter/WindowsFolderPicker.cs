using Lunar.Desktop.Resources;
using Microsoft.Win32;

namespace Lunar.Desktop.Features.Converter;

/// <summary>The standard Windows folder dialog (FE-21). A view-side service; it only chooses a name.</summary>
public sealed class WindowsFolderPicker : IFolderPicker
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = Strings.Get("Picker.FolderTitle"), Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

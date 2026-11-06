using Lunar.Desktop.Resources;
using Lunar.Domain.Conversion;
using Microsoft.Win32;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// The standard Windows "Open" dialog for the files Lunar converts (FE-20). A view-side service: the dialog is the operating
/// system's own, it can choose several files and shows only the supported types by default ("All files" is there too, so a
/// refused file can still be explained instead of being invisible). It only chooses names; nothing is read here.
/// </summary>
public sealed class WindowsFilePicker : IFilePicker
{
    public IReadOnlyList<string>? PickFiles()
    {
        var supported = string.Join(';', FileFormats.All.Select(f => "*" + FileFormats.Extension(f)));
        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Picker.FilesTitle"),
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = $"{Strings.Get("Picker.Supported")} ({supported})|{supported}|{Strings.Get("Picker.All")} (*.*)|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
}

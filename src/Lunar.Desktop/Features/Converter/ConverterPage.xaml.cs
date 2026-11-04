using System.Windows;
using System.Windows.Controls;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// The Converter page (FE-20). Presentation only: the XAML binds to <see cref="ConverterPageViewModel"/>. The one thing done here
/// is turning a drop into a call: the dragged names go to <see cref="ConverterPageViewModel.AddFiles"/> and nothing else happens
/// to them (no file is opened, moved or deleted; the drop is always a copy-style drop that only reads names).
/// </summary>
public partial class ConverterPage : UserControl
{
    public ConverterPage()
    {
        InitializeComponent();
        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => Model?.SetDragActive(false);
        Drop += OnDrop;
        // A PasswordBox cannot be bound. The typed password goes straight to the options (memory only) and is never read back here.
        PdfPasswordBox.PasswordChanged += (_, _) => Model?.Options.SetPdfPassword(PdfPasswordBox.Password);
    }

    private ConverterPageViewModel? Model => DataContext as ConverterPageViewModel;

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDrag(object sender, DragEventArgs e)
    {
        var accepts = HasFiles(e);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        Model?.SetDragActive(accepts);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        Model?.SetDragActive(false);
        if (HasFiles(e) && e.Data.GetData(DataFormats.FileDrop) is string[] names)
            Model?.AddFiles(names);
        e.Handled = true;
    }
}

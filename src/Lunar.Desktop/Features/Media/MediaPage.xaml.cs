using System.Windows.Controls;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The Media page (FE-17). Presentation only: the XAML binds to <see cref="MediaPageViewModel"/> and this class holds no logic
/// and calls no Windows API.
/// </summary>
public partial class MediaPage : UserControl
{
    public MediaPage()
    {
        InitializeComponent();
    }
}

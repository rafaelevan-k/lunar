using System.Windows.Controls;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// The System page (FE-13). Presentation only: the XAML binds to <see cref="SystemPageViewModel"/> and this class holds no
/// logic and calls no Windows API.
/// </summary>
public partial class SystemPage : UserControl
{
    public SystemPage()
    {
        InitializeComponent();
    }
}

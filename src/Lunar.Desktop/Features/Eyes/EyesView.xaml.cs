using System.Windows.Controls;

namespace Lunar.Desktop.Features.Eyes;

/// <summary>
/// Static vector artwork of the two eyes (FE-07). It has no behavior: blink timing (FE-08) and gaze/hover (FE-09)
/// will drive the named transforms in the XAML.
/// </summary>
public partial class EyesView : UserControl
{
    public EyesView() => InitializeComponent();
}

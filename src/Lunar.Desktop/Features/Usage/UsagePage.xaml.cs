using System.Windows;
using System.Windows.Controls;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The Usage page (FE-27, FE-29). Presentation only: it attaches the Claude palette (brand colors, or Windows colors in High Contrast). There is
/// no field to type in: the login is the user's own Claude Code sign-in, read by the credential source (ADR-0015). No logic and no Windows API here.
/// </summary>
public partial class UsagePage : UserControl
{
    public UsagePage()
    {
        InitializeComponent();
        var palette = SystemParameters.HighContrast ? "Colors.Usage.HighContrast.xaml" : "Colors.Usage.xaml";
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Lunar.Desktop;component/Themes/" + palette, UriKind.Relative) });
    }
}

using System.Windows.Controls;

namespace Lunar.Desktop.Features.Notes;

/// <summary>The Notes page with its two buttons (FE-34). Presentation only; everything is bound to <see cref="NotesHostViewModel"/>.</summary>
public partial class NotesHostPage : UserControl
{
    public NotesHostPage()
    {
        InitializeComponent();
    }
}

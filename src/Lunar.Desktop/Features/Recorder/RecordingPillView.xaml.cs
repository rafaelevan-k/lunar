using System.Windows.Controls;
using System.Windows.Input;

namespace Lunar.Desktop.Features.Recorder;

/// <summary>The collapsed island while recording (FE-35). Presentation only. Pause and Stop handle their own clicks, so only the rest of the pill reaches <see cref="ExpandCommand"/>.</summary>
public partial class RecordingPillView : UserControl
{
    public RecordingPillView()
    {
        InitializeComponent();
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    /// <summary>Run by a click on the pill outside the buttons.</summary>
    public ICommand? ExpandCommand { get; set; }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RequestExpand();

    internal void RequestExpand()
    {
        if (ExpandCommand?.CanExecute(null) == true)
            ExpandCommand.Execute(null);
    }
}

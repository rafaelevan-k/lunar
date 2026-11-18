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
        SizeChanged += (_, e) => FitToHeight(e.NewSize.Height);
    }

    // ADR-0027: the Notch pill is 32 DIP high, so the buttons and lines shrink with it and stay centred.
    private void FitToHeight(double height)
    {
        if (!double.IsFinite(height) || height <= 0)
            return;
        var button = Math.Clamp(height - 8, 20, 28);
        PauseButton.Width = PauseButton.Height = button;
        StopButton.Width = StopButton.Height = button;
        Levels.Height = Math.Clamp(height - 14, 14, 26);
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

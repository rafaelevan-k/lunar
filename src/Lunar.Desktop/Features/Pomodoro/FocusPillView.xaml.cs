using System.Windows.Controls;
using System.Windows.Input;

namespace Lunar.Desktop.Features.Pomodoro;

/// <summary>The collapsed island during a focus session (FE-37). Presentation only: a click anywhere on it runs <see cref="ExpandCommand"/>.</summary>
public partial class FocusPillView : UserControl
{
    public FocusPillView()
    {
        InitializeComponent();
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    /// <summary>Run by a click on the pill.</summary>
    public ICommand? ExpandCommand { get; set; }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RequestExpand();

    internal void RequestExpand()
    {
        if (ExpandCommand?.CanExecute(null) == true)
            ExpandCommand.Execute(null);
    }
}

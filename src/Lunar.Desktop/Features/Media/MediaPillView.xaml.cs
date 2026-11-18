using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// The collapsed island while music or video plays (FE-39). Presentation only: the bars take their colour from the artwork, lifted to 3:1 against the
/// pill (ADR-0026 §9); without artwork, and in High Contrast, they use the primary text colour. A click runs <see cref="ExpandCommand"/>.
/// </summary>
public partial class MediaPillView : UserControl
{
    private INotifyPropertyChanged? _source;

    public MediaPillView()
    {
        InitializeComponent();
        MouseLeftButtonUp += (_, _) => RequestExpand();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => UpdateBarBrush();
        Loaded += (_, _) => UpdateBarBrush();
        SizeChanged += (_, e) => FitToHeight(e.NewSize.Height);
    }

    // The picture is 24 DIP in the 44 DIP Pill, kept clear of the Pill's round end, and 22 DIP in the 32 DIP Notch, whose shell already keeps it clear of
    // the shoulders (2026-11-18, the owner: the picture was too big and too close to the Pill's left end).
    private void FitToHeight(double height)
    {
        if (!double.IsFinite(height) || height <= 0)
            return;
        var pill = height >= 40;
        var size = pill ? 24 : Math.Clamp(height - 10, 18, 22);
        Root.Margin = pill ? new Thickness(16, 0, 18, 0) : new Thickness(4, 0, 6, 0);
        Thumbnail.Width = Thumbnail.Height = size;
        var radius = size / 4;
        Thumbnail.CornerRadius = new CornerRadius(radius);
        ThumbnailClip.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        Bars.Height = Math.Clamp(height - 18, 12, 20);
    }

    /// <summary>Run by a click on the pill: opens the island on the Media page.</summary>
    public ICommand? ExpandCommand { get; set; }

    internal void RequestExpand()
    {
        if (ExpandCommand?.CanExecute(null) == true)
            ExpandCommand.Execute(null);
    }

    /// <summary>The brush the bars use now; for tests.</summary>
    internal Brush? BarBrush => Bars.BarBrush;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_source is not null)
            _source.PropertyChanged -= OnSourceChanged;
        _source = e.NewValue as INotifyPropertyChanged;
        if (_source is not null)
            _source.PropertyChanged += OnSourceChanged;
        UpdateBarBrush();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Artwork" or "HasArtwork")
            UpdateBarBrush();
    }

    private void UpdateBarBrush()
    {
        var text = TryFindResource("Lunar.Brush.TextPrimary") as Brush ?? Brushes.White;
        if (SystemParameters.HighContrast || TryFindResource("Lunar.Color.Background") is not Color background)
        {
            Bars.BarBrush = text;
            return;
        }

        var tint = ArtworkTint.Dominant(ThumbnailImage.Source as BitmapSource);
        if (tint is not { } color)
        {
            Bars.BarBrush = text;
            return;
        }

        var brush = new SolidColorBrush(ArtworkTint.Lift(color, background));
        brush.Freeze();
        Bars.BarBrush = brush;
    }
}

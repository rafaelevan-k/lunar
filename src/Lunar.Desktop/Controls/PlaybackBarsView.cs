using System.Windows;
using System.Windows.Media;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// The media pill's sound bars (ADR-0026): one rounded bar per band, drawn from real readings only; with none, or in silence, they rest at their lowest
/// height. With reduced motion a still three-bar symbol replaces them. Decorative: the pill's name says what plays.
/// </summary>
public sealed class PlaybackBarsView : FrameworkElement
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
        nameof(Levels), typeof(IReadOnlyList<float>), typeof(PlaybackBarsView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(PlaybackBarsView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal const int BarCount = 6;
    private const double BarWidth = 3;
    private const double MinBarHeight = 3;
    private static readonly double[] StillSymbol = [0.45, 0.85, 0.6];

    public IReadOnlyList<float>? Levels
    {
        get => (IReadOnlyList<float>?)GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    // Never more than it is given: in the 32 DIP Notch the bars are 14 DIP high, and asking for more would push them out of the pill.
    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(BarCount * (BarWidth + 2), availableSize.Width), Math.Min(18, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (BarBrush is not { } brush || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (Motion.GetReduceMotion(this))
        {
            var stillStep = ActualWidth / BarCount;
            var left = (ActualWidth - (StillSymbol.Length * stillStep)) / 2;
            for (var i = 0; i < StillSymbol.Length; i++)
                DrawBar(drawingContext, brush, left + (i * stillStep) + ((stillStep - BarWidth) / 2), StillSymbol[i] * ActualHeight);
            return;
        }

        var step = ActualWidth / BarCount;
        var levels = Levels;
        for (var band = 0; band < BarCount; band++)
        {
            var level = levels is not null && band < levels.Count ? Math.Clamp(levels[band], 0f, 1f) : 0f;
            DrawBar(drawingContext, brush, (band * step) + ((step - BarWidth) / 2), Math.Max(MinBarHeight, level * ActualHeight));
        }
    }

    private void DrawBar(DrawingContext context, Brush brush, double x, double height) =>
        context.DrawRoundedRectangle(brush, null, new Rect(x, (ActualHeight - height) / 2, BarWidth, height), BarWidth / 2, BarWidth / 2);
}

using System.Windows;
using System.Windows.Media;
using Lunar.Application.Abstractions.Audio;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// The microphone's frequency bands as vertical lines (ADR-0024), drawn from real readings only: with none, or in silence, they lie flat.
/// With reduced motion a still dot replaces the moving lines. Decorative: the timer and the state are said in words elsewhere.
/// </summary>
public sealed class VoiceLevelsView : FrameworkElement
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
        nameof(Levels), typeof(IReadOnlyList<float>), typeof(VoiceLevelsView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(VoiceLevelsView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double BarWidth = 3;
    private const double MinBarHeight = 3;
    private const double DotDiameter = 10;

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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (BarBrush is not { } brush || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        if (Motion.GetReduceMotion(this))
        {
            drawingContext.DrawEllipse(brush, null, new Point(ActualWidth / 2, ActualHeight / 2), DotDiameter / 2, DotDiameter / 2);
            return;
        }

        var step = ActualWidth / VoiceLevelBands.Count;
        var levels = Levels;
        for (var band = 0; band < VoiceLevelBands.Count; band++)
        {
            var level = levels is not null && band < levels.Count ? Math.Clamp(levels[band], 0f, 1f) : 0f;
            var height = Math.Max(MinBarHeight, level * ActualHeight);
            var x = (band * step) + ((step - BarWidth) / 2);
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(x, (ActualHeight - height) / 2, BarWidth, height), BarWidth / 2, BarWidth / 2);
        }
    }
}

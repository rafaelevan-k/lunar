using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// A small burst of colored pieces that rises from the top of the island and falls while it fades (the UI revision: the celebration
/// when a conversion finishes). It draws only (it never takes the pointer or focus), uses the plain black-and-white theme brushes so it follows
/// dark, light and high contrast, removes every piece when its animation ends, and plays nothing at all when reduced motion is on.
/// Only one burst runs at a time; asking for another while one plays does nothing. Must be used on the UI thread.
/// </summary>
public sealed class ConfettiBurst : Canvas
{
    internal const int Pieces = 26;

    private static readonly string[] Colors = ["Lunar.Brush.TextPrimary", "Lunar.Brush.TextSecondary", "Lunar.Brush.TextDisabled", "Lunar.Brush.Accent", "Lunar.Brush.Border"];

    private readonly Random _random;
    private int _active;

    public ConfettiBurst()
        : this(Random.Shared)
    {
    }

    internal ConfettiBurst(Random random)
    {
        _random = random;
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>How many pieces are still in the air.</summary>
    public int ActivePieces => _active;

    /// <summary>Plays one burst; returns false when nothing was played (reduced motion, no size yet, or one is already playing).</summary>
    public bool Burst()
    {
        if (Motion.GetReduceMotion(this) || _active > 0 || ActualWidth < 40 || ActualHeight < 40)
            return false;

        var centre = ActualWidth / 2;
        for (var i = 0; i < Pieces; i++)
            Launch(centre);
        return true;
    }

    /// <summary>Removes any pieces at once.</summary>
    public void Clear()
    {
        Children.Clear();
        _active = 0;
    }

    private void Launch(double centre)
    {
        var width = 5 + (_random.NextDouble() * 5);
        var piece = new Border
        {
            Width = width,
            Height = width * (1.2 + (_random.NextDouble() * 0.8)),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        piece.SetResourceReference(Border.BackgroundProperty, Colors[_random.Next(Colors.Length)]);

        var translate = new TranslateTransform();
        var rotate = new RotateTransform();
        piece.RenderTransform = new TransformGroup { Children = { rotate, translate } };
        SetLeft(piece, centre + ((_random.NextDouble() - 0.5) * 60));
        SetTop(piece, 34);
        Children.Add(piece);
        _active++;

        var spread = (_random.NextDouble() - 0.5) * 2 * Math.Min(170, ActualWidth / 2.2);
        var lift = -(40 + (_random.NextDouble() * 70));
        var fall = ActualHeight - 20 + (_random.NextDouble() * 20);
        var life = TimeSpan.FromMilliseconds(900 + (_random.NextDouble() * 450));
        var rise = TimeSpan.FromMilliseconds(life.TotalMilliseconds * 0.35);

        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, spread, life) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var vertical = new DoubleAnimationUsingKeyFrames { Duration = life };
        vertical.KeyFrames.Add(new EasingDoubleKeyFrame(lift, KeyTime.FromTimeSpan(rise), new CubicEase { EasingMode = EasingMode.EaseOut }));
        vertical.KeyFrames.Add(new EasingDoubleKeyFrame(fall, KeyTime.FromTimeSpan(life), new CubicEase { EasingMode = EasingMode.EaseIn }));
        translate.BeginAnimation(TranslateTransform.YProperty, vertical);
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, (_random.NextDouble() - 0.5) * 900, life));

        var fade = new DoubleAnimationUsingKeyFrames { Duration = life };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.6)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) =>
        {
            Children.Remove(piece);
            _active = Math.Max(0, _active - 1);
        };
        piece.BeginAnimation(OpacityProperty, fade);
    }
}

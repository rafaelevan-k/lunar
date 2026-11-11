using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lunar.Desktop.Behaviors;

namespace Lunar.Desktop.Controls;

/// <summary>
/// A small picture drawn from rows of characters, one square per character: <c>o</c> outline, <c>w</c> white, <c>s</c> shade, <c>y</c> yolk, <c>h</c>
/// highlight, anything else empty. Colours come from the theme, so High Contrast follows. With <see cref="IsBouncing"/> it hops a little, but only while
/// it can be seen and motion is allowed. Decorative: the page says everything in words.
/// </summary>
public sealed class PixelArtView : FrameworkElement
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<string>), typeof(PixelArtView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((PixelArtView)d).OnRowsChanged(e)));

    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize), typeof(double), typeof(PixelArtView), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OutlineBrushProperty = Brush(nameof(OutlineBrush));
    public static readonly DependencyProperty WhiteBrushProperty = Brush(nameof(WhiteBrush));
    public static readonly DependencyProperty ShadeBrushProperty = Brush(nameof(ShadeBrush));
    public static readonly DependencyProperty YolkBrushProperty = Brush(nameof(YolkBrush));

    public static readonly DependencyProperty IntroFramesProperty = DependencyProperty.Register(
        nameof(IntroFrames), typeof(IReadOnlyList<IReadOnlyList<string>>), typeof(PixelArtView), new PropertyMetadata(null));

    public static readonly DependencyProperty IsBouncingProperty = DependencyProperty.Register(
        nameof(IsBouncing), typeof(bool), typeof(PixelArtView), new PropertyMetadata(false, (d, _) => ((PixelArtView)d).UpdateBounce()));

    /// <summary>How high the hop goes, in DIPs.</summary>
    internal const double BounceHeight = 4;

    internal static readonly TimeSpan IntroFrameTime = TimeSpan.FromMilliseconds(180);
    private static readonly Duration HalfBounce = new(TimeSpan.FromMilliseconds(420));
    private readonly TranslateTransform _offset = new();
    private System.Windows.Threading.DispatcherTimer? _introTimer;
    private IReadOnlyList<IReadOnlyList<string>>? _intro;
    private int _introFrame;

    public PixelArtView()
    {
        RenderTransform = _offset;

        // Square pixels side by side: without this, smoothing leaves faint lines between them.
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => UpdateBounce();
    }

    public IReadOnlyList<string>? Rows
    {
        get => (IReadOnlyList<string>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double PixelSize
    {
        get => (double)GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public Brush? WhiteBrush
    {
        get => (Brush?)GetValue(WhiteBrushProperty);
        set => SetValue(WhiteBrushProperty, value);
    }

    public Brush? ShadeBrush
    {
        get => (Brush?)GetValue(ShadeBrushProperty);
        set => SetValue(ShadeBrushProperty, value);
    }

    public Brush? YolkBrush
    {
        get => (Brush?)GetValue(YolkBrushProperty);
        set => SetValue(YolkBrushProperty, value);
    }

    /// <summary>
    /// Pictures played once, before a new <see cref="Rows"/> is shown, when that change happens while this view can be seen and motion is allowed. Set it
    /// before changing <see cref="Rows"/>; null plays nothing.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? IntroFrames
    {
        get => (IReadOnlyList<IReadOnlyList<string>>?)GetValue(IntroFramesProperty);
        set => SetValue(IntroFramesProperty, value);
    }

    /// <summary>The intro is playing now. For tests.</summary>
    internal bool IsPlayingIntro => _intro is not null;

    public bool IsBouncing
    {
        get => (bool)GetValue(IsBouncingProperty);
        set => SetValue(IsBouncingProperty, value);
    }

    /// <summary>The hop is running now. For tests.</summary>
    internal bool IsAnimating { get; private set; }

    private IReadOnlyList<string>? Shown => _intro is { } intro ? intro[_introFrame] : Rows;

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Shown;
        if (rows is null || rows.Count == 0)
            return default;
        return new Size(rows.Max(r => r.Length) * PixelSize, rows.Count * PixelSize);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var rows = Shown;
        if (rows is null)
            return;
        var size = PixelSize;
        for (var y = 0; y < rows.Count; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (BrushFor(rows[y][x]) is { } brush)
                    drawingContext.DrawRectangle(brush, null, new Rect(x * size, y * size, size, size));
            }
        }
    }

    private static DependencyProperty Brush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(PixelArtView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush? BrushFor(char pixel) => pixel switch
    {
        'o' => OutlineBrush,
        'w' or 'h' => WhiteBrush,
        's' => ShadeBrush,
        'y' => YolkBrush,
        _ => null
    };

    private void OnRowsChanged(DependencyPropertyChangedEventArgs e)
    {
        StopIntro();
        if (e.OldValue is null || ReferenceEquals(e.OldValue, e.NewValue) || IntroFrames is not { Count: > 0 } frames || !IsVisible || Motion.GetReduceMotion(this))
            return;

        _intro = frames;
        _introFrame = 0;
        _introTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render, Dispatcher) { Interval = IntroFrameTime };
        _introTimer.Tick += OnIntroTick;
        _introTimer.Start();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnIntroTick(object? sender, EventArgs e)
    {
        if (_intro is null || ++_introFrame >= _intro.Count)
            StopIntro();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void StopIntro()
    {
        if (_introTimer is not null)
        {
            _introTimer.Stop();
            _introTimer.Tick -= OnIntroTick;
            _introTimer = null;
        }

        _intro = null;
    }

    private void UpdateBounce()
    {
        var wanted = IsBouncing && IsVisible && !Motion.GetReduceMotion(this);
        if (wanted == IsAnimating)
            return;
        IsAnimating = wanted;
        if (!wanted)
        {
            _offset.BeginAnimation(TranslateTransform.YProperty, null);
            _offset.Y = 0;
            return;
        }

        var hop = new DoubleAnimation(0, -BounceHeight, HalfBounce)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _offset.BeginAnimation(TranslateTransform.YProperty, hop);
    }
}

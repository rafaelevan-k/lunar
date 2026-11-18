using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Translate;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Translate;

/// <summary>
/// The screen translation overlay (ADR-0028 §11, FE-41): a borderless topmost window exactly over the display that was read, showing that moment's picture
/// with each translated block drawn over its original text, on the sampled background, in a readable text colour, in a font size that fits the box
/// (down to 9 DIP; what still does not fit is cut with an ellipsis and shown whole on hover). A small toolbar at the top offers the language, Show
/// original (while held), Copy text and Close. Escape closes. Blocks are text for screen readers, in reading order. It is excluded from capture like
/// every Lunar window. On close the picture is dropped and the keyboard goes back to the app that had it.
/// </summary>
internal sealed class TranslationOverlayWindow : Window
{
    internal const double MinimumFontDip = 9;

    private readonly Action<TranslationLanguage> _retarget;
    private readonly Grid _surface = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _blocks = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
    private readonly Dictionary<TranslationLanguage, RadioButton> _languages = [];
    private readonly Button _original;
    private readonly IntPtr _previousForeground;
    private ScreenTranslationOutcome _outcome;
    private bool _updating;

    public TranslationOverlayWindow(ScreenTranslationOutcome outcome, Action<TranslationLanguage> retarget)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(outcome.Picture);
        ArgumentNullException.ThrowIfNull(retarget);
        _outcome = outcome;
        _retarget = retarget;
        _previousForeground = GetForegroundWindow();

        Title = Strings.Get("Translate.OverlayTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AutomationProperties.SetName(this, Strings.Get("Translate.OverlayTitle"));

        // The picture and the blocks share one coordinate system, the picture's pixels; the Viewbox scales both to the window.
        _surface.Children.Add(_image);
        _surface.Children.Add(_blocks);
        var root = new Grid();
        root.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = _surface });

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var language in Enum.GetValues<TranslationLanguage>())
        {
            var radio = new RadioButton
            {
                Content = TranslateText.Language(language),
                GroupName = "Lunar.Translate.Target",
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            radio.SetResourceReference(ForegroundProperty, "Lunar.Brush.TextPrimary");
            radio.Checked += (_, _) =>
            {
                if (!_updating)
                    _retarget(language);
            };
            AutomationProperties.SetName(radio, Strings.Format("Translate.ToLanguage", TranslateText.Language(language)));
            _languages[language] = radio;
            bar.Children.Add(radio);
        }

        _original = ToolbarButton(Strings.Get("Translate.ShowOriginal"));
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button))
            .AddValueChanged(_original, (_, _) => _blocks.Visibility = _original.IsPressed ? Visibility.Hidden : Visibility.Visible);
        var copy = ToolbarButton(Strings.Get("Translate.Copy"));
        copy.Click += (_, _) => CopyText();
        var close = ToolbarButton(Strings.Get("Translate.Close"));
        close.Click += (_, _) => Close();
        bar.Children.Add(_original);
        bar.Children.Add(copy);
        bar.Children.Add(close);
        _status.SetResourceReference(ForegroundProperty, "Lunar.Brush.TextSecondary");
        bar.Children.Add(_status);

        var toolbar = new Border
        {
            Child = bar,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(1)
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "Lunar.Brush.Background");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "Lunar.Brush.Border");
        AutomationProperties.SetName(toolbar, Strings.Get("Translate.Toolbar"));
        root.Children.Add(toolbar);
        Content = root;

        InputBindings.Add(new KeyBinding(new RelayKeyCommand(Close), Key.Escape, ModifierKeys.None));
        SourceInitialized += (_, _) => PlaceOverPicture();
        Loaded += (_, _) => Keyboard.Focus(_languages[_outcome.Target]);
        Closed += OnClosed;
        Show(outcome);
    }

    /// <summary>The blocks drawn now; for tests.</summary>
    internal IReadOnlyList<Border> Blocks => [.. _blocks.Children.OfType<Border>()];

    /// <summary>Shows the outcome (first, or after the language changed). UI thread only.</summary>
    internal void Show(ScreenTranslationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome.Picture);
        _outcome = outcome;
        var picture = outcome.Picture;
        if (_image.Source is not BitmapSource current || current.PixelWidth != picture.Width || current.PixelHeight != picture.Height)
        {
            var bitmap = BitmapSource.Create(picture.Width, picture.Height, 96, 96, PixelFormats.Bgra32, null, picture.Pixels, picture.Width * 4);
            bitmap.Freeze();
            _image.Source = bitmap;
        }

        _surface.Width = picture.Width;
        _surface.Height = picture.Height;
        _blocks.Children.Clear();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY; // picture pixels per DIP
        foreach (var block in outcome.Blocks.Where(b => b.Translation is not null))
            _blocks.Children.Add(BlockView(block, scale));

        _updating = true;
        _languages[outcome.Target].IsChecked = true;
        _updating = false;
        SetStatus(outcome.Blocks.Any(b => b.Translation is not null) ? null : Strings.Get("Translate.NothingToTranslate"));
    }

    /// <summary>A line under the toolbar's buttons: "Translating…", a problem, or nothing.</summary>
    internal void SetStatus(string? text)
    {
        _status.Text = text ?? string.Empty;
        _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(text))
            AutomationProperties.SetName(_status, text);
    }

    /// <summary>
    /// One translated block: the sampled background a little larger than the original text so no letter peeks out, and the largest font (from the
    /// original line height down to 9 DIP) at which the translation fits.
    /// </summary>
    internal static Border BlockView(TranslatedBlock block, double pixelsPerDip)
    {
        var highContrast = SystemParameters.HighContrast;
        var background = highContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(block.Background.R, block.Background.G, block.Background.B));
        var foreground = highContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(block.Foreground.R, block.Foreground.G, block.Foreground.B));
        var japanese = block.Translation!.Any(ScreenTextRules.IsJapanese);
        var text = new TextBlock
        {
            Text = block.Translation,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = japanese ? new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI") : new FontFamily("Segoe UI Variable Text, Segoe UI"),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        var width = Math.Max(1, block.Box.Width + 4);
        var height = Math.Max(1, block.Box.Height + 2);
        var size = FitFontSize(text, width, height, block.LineHeight * 0.78, MinimumFontDip * pixelsPerDip);
        if (size.Fits)
        {
            text.FontSize = size.Size;
            text.LineHeight = size.Size * 1.2;
        }
        else
        {
            text.FontSize = size.Size;
            text.LineHeight = size.Size * 1.2;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.ToolTip = block.Translation;
        }

        AutomationProperties.SetName(text, block.Translation);
        var border = new Border
        {
            Background = background,
            Child = text,
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };
        Canvas.SetLeft(border, block.Box.X - 2);
        Canvas.SetTop(border, block.Box.Y - 1);
        return border;
    }

    /// <summary>The largest size from <paramref name="start"/> down to <paramref name="minimum"/> at which the text fits the box, in steps of 5 %.</summary>
    internal static (double Size, bool Fits) FitFontSize(TextBlock text, double width, double height, double start, double minimum)
    {
        var size = Math.Max(start, minimum);
        while (true)
        {
            text.FontSize = size;
            text.LineHeight = size * 1.2;
            text.Measure(new Size(width, double.PositiveInfinity));
            if (text.DesiredSize.Height <= height + 0.5 && text.DesiredSize.Width <= width + 0.5)
                return (size, true);
            if (size <= minimum)
                return (minimum, false);
            size = Math.Max(minimum, size * 0.95);
        }
    }

    private void CopyText()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, _outcome.Blocks.Select(b => b.Translation ?? b.Original));
        try
        {
            Clipboard.SetText(text);
            SetStatus(Strings.Get("Translate.Copied"));
        }
        catch (COMException)
        {
            SetStatus(Strings.Get("Translate.CopyFailed")); // another app holds the clipboard
        }
    }

    private Button ToolbarButton(string label)
    {
        var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        button.SetResourceReference(StyleProperty, "Lunar.Style.ActionButton");
        AutomationProperties.SetName(button, label);
        return button;
    }

    // The window covers exactly the display that was read, in physical pixels; WPF then follows that display's scale.
    private void PlaceOverPicture()
    {
        var bounds = _outcome.Picture!.Bounds;
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040 /* SWP_SHOWWINDOW */);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _image.Source = null; // the picture's copy goes with the window
        _blocks.Children.Clear();
        if (_previousForeground != IntPtr.Zero)
            SetForegroundWindow(_previousForeground);
    }

    private sealed class RelayKeyCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

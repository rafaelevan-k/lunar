using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Translate;

/// <summary>
/// A short notice under the island when screen translation cannot show anything (ADR-0028 §13): the reason in words and, when Settings can fix it,
/// an Open Settings button. It never takes focus, closes by itself after 8 seconds, on Close, or when another notice replaces it, and is announced to
/// screen readers.
/// </summary>
internal sealed class TranslateNoticeWindow : Window
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    private static TranslateNoticeWindow? s_current;
    private readonly DispatcherTimer _timer;

    private TranslateNoticeWindow(string message, Action? openSettings)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = Strings.Get("Translate.OverlayTitle");

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "Lunar.Text.Body");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        if (openSettings is not null)
        {
            var settings = Button(Strings.Get("Translate.OpenSettings"));
            settings.Click += (_, _) =>
            {
                Close();
                openSettings();
            };
            row.Children.Add(settings);
        }

        var close = Button(Strings.Get("Translate.Close"));
        close.Click += (_, _) => Close();
        row.Children.Add(close);
        var border = new Border { Child = row, Padding = new Thickness(14, 10, 10, 10), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Lunar.Brush.Background");
        border.SetResourceReference(Border.BorderBrushProperty, "Lunar.Brush.Border");
        Content = border;
        AutomationProperties.SetName(this, message);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + ((area.Width - ActualWidth) / 2);
            Top = area.Top + 64;
            _timer.Start();
            UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (ReferenceEquals(s_current, this))
                s_current = null;
        };
    }

    /// <summary>Shows a notice, replacing an earlier one. UI thread only.</summary>
    public static TranslateNoticeWindow Show(string message, Action? openSettings, Action<Window>? attach)
    {
        s_current?.Close();
        var notice = new TranslateNoticeWindow(message, openSettings);
        attach?.Invoke(notice);
        s_current = notice;
        notice.Show();
        return notice;
    }

    private static Button Button(string label)
    {
        var button = new Button { Content = label, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "Lunar.Style.ActionButton");
        AutomationProperties.SetName(button, label);
        return button;
    }
}

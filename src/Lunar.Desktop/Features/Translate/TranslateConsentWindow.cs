using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Translate;

/// <summary>
/// The one-time question before Lunar sends any recognised text (ADR-0028 §9): what is sent (the text only, never the picture), to which service, and
/// where its privacy statement is. Allow or Cancel; Escape cancels. Nothing is sent before Allow. Built in code: a small fixed dialog.
/// </summary>
internal sealed class TranslateConsentWindow : Window
{
    public TranslateConsentWindow()
    {
        Title = Strings.Get("Translate.ConsentTitle");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Lunar.Brush.Background");
        SetResourceReference(ForegroundProperty, "Lunar.Brush.TextPrimary");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text(Strings.Get("Translate.ConsentTitle"), 18, FontWeights.SemiBold));
        panel.Children.Add(Text(Strings.Get("Translate.ConsentBody"), 14, FontWeights.Normal));
        var privacy = new TextBox
        {
            Text = TranslationSettingsViewModel.PrivacyAddress,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0, 6, 0, 0)
        };
        privacy.SetResourceReference(ForegroundProperty, "Lunar.Brush.TextSecondary");
        AutomationProperties.SetName(privacy, Strings.Get("Translate.PrivacyLink"));
        panel.Children.Add(privacy);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = Strings.Get("Translate.Cancel"), IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var allow = new Button { Content = Strings.Get("Translate.Allow"), IsDefault = true, MinWidth = 88 };
        cancel.SetResourceReference(StyleProperty, "Lunar.Style.ActionButton");
        allow.SetResourceReference(StyleProperty, "Lunar.Style.ActionButton");
        cancel.Click += (_, _) => DialogResult = false;
        allow.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(allow);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => Keyboard.Focus(cancel);
    }

    private static TextBlock Text(string text, double size, FontWeight weight) =>
        new() { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
}

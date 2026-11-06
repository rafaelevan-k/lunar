using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lunar.Desktop.Controls;

/// <summary>
/// An inline, recoverable error (FE-12, ARCHITECTURE §8: a failure shows in place and never forces a global error
/// state). It is collapsed while <see cref="Message"/> is empty, shows a symbol and the message, and offers a dismiss
/// button and an optional action such as Retry. It is an assertive live region so a screen reader announces a new
/// message, and its buttons are normal tab stops. The message must already be user-safe
/// (<c>OperationError.SafeMessage</c>).
/// </summary>
public sealed class InlineError : Control
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(InlineError), new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty DismissCommandProperty =
        DependencyProperty.Register(nameof(DismissCommand), typeof(ICommand), typeof(InlineError));

    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(InlineError));

    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(InlineError), new PropertyMetadata(Lunar.Desktop.Resources.Strings.Get("Common.Retry")));

    static InlineError() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InlineError), new FrameworkPropertyMetadata(typeof(InlineError)));

    public InlineError()
    {
        Focusable = false;
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }

    /// <summary>The user-safe message; null or empty hides the control.</summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Runs when the user dismisses the message. Without it there is no dismiss button.</summary>
    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    /// <summary>An optional recovery action such as Retry. Without it there is no action button.</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (InlineError)d;
        var message = (string?)e.NewValue;
        var shown = !string.IsNullOrWhiteSpace(message);
        control.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(control, shown ? Lunar.Desktop.Resources.Strings.Format("Common.ErrorPrefix", message) : string.Empty);
    }
}

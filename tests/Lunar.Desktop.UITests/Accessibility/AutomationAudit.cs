using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace Lunar.Desktop.UITests.Accessibility;

/// <summary>
/// FE-24: walks the real visual tree of a page or window and reports what a screen reader or a keyboard-only user would trip over.
/// It proves the automation tree (names, tab stops, tab order rules); it does not prove how Narrator speaks them, which stays a manual check.
/// </summary>
internal static class AutomationAudit
{
    /// <summary>Controls a person can use. Template parts (scroll bar buttons, slider thumbs) are not listed: they are never reached on their own.</summary>
    internal static IEnumerable<Control> UsableControls(DependencyObject root) =>
        Descendants(root).OfType<Control>().Where(c => IsUsable(c) && IsShown(c));

    internal static string NameOf(UIElement element)
    {
        var name = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        return peer?.GetName() ?? string.Empty;
    }

    /// <summary>One line per problem; empty when the tree is fine.</summary>
    internal static IReadOnlyList<string> Problems(DependencyObject root)
    {
        var problems = new List<string>();
        var usable = UsableControls(root).ToList();
        if (usable.Count == 0)
            problems.Add("No usable control was found, so nothing was checked.");

        foreach (var control in usable)
        {
            var label = control.GetType().Name + (control is FrameworkElement { Name.Length: > 0 } fe ? " " + fe.Name : string.Empty);
            if (string.IsNullOrWhiteSpace(NameOf(control)))
                problems.Add(label + " has no accessible name.");
            if (control.IsEnabled && (!control.Focusable || !control.IsTabStop))
                problems.Add(label + " is enabled but cannot be reached with the Tab key.");
        }

        // Tab order follows the document order of the XAML; an explicit TabIndex would break the reading order, so none may be used.
        foreach (var element in Descendants(root).OfType<UIElement>())
        {
            if (KeyboardNavigation.GetTabIndex(element) != int.MaxValue)
                problems.Add(element.GetType().Name + " sets its own TabIndex.");
        }

        return problems;
    }

    internal static void AssertAccessible(DependencyObject root)
    {
        var problems = Problems(root);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static bool IsUsable(Control control) =>
        control is ButtonBase and not RepeatButton || control is Slider or TextBoxBase or PasswordBox or Selector;

    private static bool IsShown(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
            // Parts of a control's own template are not reached on their own; the control carries the name.
            if (current != element && current is Slider or ScrollBar)
                return false;
        }

        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}

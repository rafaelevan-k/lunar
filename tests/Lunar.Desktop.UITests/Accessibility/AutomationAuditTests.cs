using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Lunar.Desktop.UITests.Accessibility;

// FE-24: the audit used by the page tests must itself notice the problems it exists for; otherwise "no problems found" proves nothing.
public sealed class AutomationAuditTests
{
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF audit check failed.", failure);
    }

    private static Window Show(UIElement content)
    {
        var window = new Window { Width = 300, Height = 200, Content = content, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    [Fact]
    public void ANamedReachableControlIsFine() => RunOnSta(() =>
    {
        var window = Show(new StackPanel { Children = { new Button { Content = "Save" }, new Slider { Name = "Volume" } } });
        try
        {
            AutomationProperties.SetName(((StackPanel)window.Content).Children[1], "Volume");
            Assert.Empty(AutomationAudit.Problems(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AControlWithNoNameIsReported() => RunOnSta(() =>
    {
        var window = Show(new Button { Content = new Border { Width = 10, Height = 10 } });
        try
        {
            var problems = AutomationAudit.Problems(window);
            Assert.Contains(problems, p => p.Contains("no accessible name"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AnEnabledControlThatSkipsTheTabKeyIsReported() => RunOnSta(() =>
    {
        var window = Show(new Button { Content = "Save", IsTabStop = false });
        try
        {
            Assert.Contains(AutomationAudit.Problems(window), p => p.Contains("Tab key"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AnExplicitTabIndexIsReported() => RunOnSta(() =>
    {
        var button = new Button { Content = "Save" };
        KeyboardNavigation.SetTabIndex(button, 3);
        var window = Show(button);
        try
        {
            Assert.Contains(AutomationAudit.Problems(window), p => p.Contains("TabIndex"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AHiddenUnnamedControlIsNotCounted() => RunOnSta(() =>
    {
        var window = Show(new StackPanel { Children = { new Button { Content = "Save" }, new Button { Content = new Border(), Visibility = Visibility.Collapsed } } });
        try
        {
            Assert.Empty(AutomationAudit.Problems(window));
        }
        finally
        {
            window.Close();
        }
    });
}

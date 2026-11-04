using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Shell;

// Real windows on an STA thread with a fake display environment. The animations run on WPF's own clock, so the tests
// pump the dispatcher until the state settles (bounded by a timeout) instead of sleeping for a fixed time.
public sealed class IslandTransitionControllerTests
{
    private sealed record Rig(IslandWindow Window, IslandViewModel ViewModel, IslandPositionController Position, IslandTransitionController Transition, Border Pill)
        : IDisposable
    {
        public void Dispose()
        {
            Transition.Dispose();
            Position.Dispose();
            Window.Close();
        }
    }

    private static Rig Start(bool reduceMotion)
    {
        var window = new IslandWindow();
        var viewModel = new IslandViewModel();
        window.DataContext = viewModel;
        using (var stream = System.IO.File.OpenRead(System.IO.Path.Combine(Themes.ThemeTokenTests.DesktopDirectory, "Themes", "Spacing.xaml")))
            window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream));
        _ = new WindowInteropHelper(window).EnsureHandle();
        var display = new DisplayInfo("\\\\.\\DISPLAY1", true, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96);
        var position = new IslandPositionController(window, viewModel, new FakeEnvironment(display), new FakeLog(), new IslandSettings());
        var transition = new IslandTransitionController(window, viewModel, reduceMotion);
        position.Start();
        window.Show();
        return new Rig(window, viewModel, position, transition, Assert.IsType<Border>(window.Content));
    }

    private static void Pump(Func<bool> until, int timeoutMs = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(5);
        }
    }

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
            throw new InvalidOperationException("WPF transition check failed.", failure);
    }

    private static bool PillIsAtRest(Border pill) =>
        double.IsNaN(pill.Width) && double.IsNaN(pill.Height) && !pill.HasAnimatedProperties
        && pill.HorizontalAlignment == HorizontalAlignment.Stretch && pill.VerticalAlignment == VerticalAlignment.Stretch;

    [Fact]
    public void ReducedMotionPlacesThePillAtOnceAndCompletesOnTheNextDispatcherTurn() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: true);

        rig.ViewModel.Toggle();

        // Immediately: the transition is not completed from inside the notification that started it.
        Assert.Equal(IslandVisualState.Expanding, rig.ViewModel.State);
        Assert.Equal(380d, rig.Pill.Width);
        Assert.Equal(320d, rig.Pill.Height);
        Assert.False(rig.Pill.HasAnimatedProperties); // no animation

        Pump(() => rig.ViewModel.State == IslandVisualState.Expanded, 1000);
        Assert.Equal(IslandVisualState.Expanded, rig.ViewModel.State);
        Assert.True(PillIsAtRest(rig.Pill));
        Assert.Equal(380d, rig.Window.Width);

        rig.ViewModel.Toggle();
        Assert.Equal(160d, rig.Pill.Width);
        Assert.Equal(44d, rig.Pill.Height);
        Pump(() => rig.ViewModel.State == IslandVisualState.Idle, 1000);
        Assert.Equal(IslandVisualState.Idle, rig.ViewModel.State);
        Assert.True(PillIsAtRest(rig.Pill));
        Assert.Equal(160d, rig.Window.Width);
        Assert.Equal(44d, rig.Window.Height);
    });

    [Fact]
    public void AnimatedExpansionAndCollapseRunToTheirEndStates() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: false);

        rig.ViewModel.Toggle();
        Assert.Equal(IslandVisualState.Expanding, rig.ViewModel.State);
        Assert.True(rig.Pill.HasAnimatedProperties);
        Assert.Equal(380d, rig.Window.Width); // the window is already at the expanded size while the pill grows

        // The pill really grows from the idle size: sizes in between are rendered, and it never starts at full size.
        var heights = new List<double>();
        Pump(() =>
        {
            heights.Add(rig.Pill.ActualHeight);
            return rig.ViewModel.State == IslandVisualState.Expanded;
        });
        Assert.Contains(heights, h => h is > 60 and < 300);
        Assert.True(heights[0] < 100, $"The first rendered height was {heights[0]}, expected near the idle 44.");
        Assert.Equal(heights.OrderBy(h => h), heights.Where(h => h <= 320)); // grows, never shrinks, while expanding
        Pump(() => rig.ViewModel.State == IslandVisualState.Expanded);
        Assert.Equal(IslandVisualState.Expanded, rig.ViewModel.State);
        Assert.True(PillIsAtRest(rig.Pill));

        rig.ViewModel.Toggle();
        Assert.Equal(IslandVisualState.Collapsing, rig.ViewModel.State);
        Assert.Equal(380d, rig.Window.Width); // the window shrinks only when the pill has finished
        Pump(() => rig.ViewModel.State == IslandVisualState.Idle);

        Assert.Equal(IslandVisualState.Idle, rig.ViewModel.State);
        Assert.True(PillIsAtRest(rig.Pill));
        Assert.Equal(160d, rig.Window.Width);
        Assert.Equal(44d, rig.Window.Height);
    });

    [Fact]
    public void AClickDuringAnimationReversesItAndOnlyTheReversalCompletes() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: false);
        var states = new List<IslandVisualState>();
        rig.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.State))
                states.Add(rig.ViewModel.State);
        };

        rig.ViewModel.Toggle(); // Expanding
        Pump(() => rig.Pill.ActualHeight > 60, 1000); // part way
        Assert.Equal(IslandVisualState.Expanding, rig.ViewModel.State);
        rig.ViewModel.Toggle(); // reverse

        Pump(() => rig.ViewModel.State == IslandVisualState.Idle);

        Assert.Equal([IslandVisualState.Expanding, IslandVisualState.Collapsing, IslandVisualState.Idle], states);
        Assert.True(PillIsAtRest(rig.Pill));
        Assert.Equal(160d, rig.Window.Width);
    });

    [Theory]
    [InlineData(false, 7)]
    [InlineData(false, 20)]
    [InlineData(true, 7)]
    [InlineData(true, 20)]
    public void RapidClicksSettleOnTheRightEndState(bool reduceMotion, int clicks) => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion);

        for (var i = 0; i < clicks; i++)
        {
            Assert.True(rig.ViewModel.Toggle());
            Pump(() => false, 8); // a few milliseconds between clicks, so animations are caught mid-way
        }

        var expected = clicks % 2 == 1 ? IslandVisualState.Expanded : IslandVisualState.Idle;
        Pump(() => rig.ViewModel.State == expected);

        Assert.Equal(expected, rig.ViewModel.State);
        Assert.Equal(0, rig.ViewModel.TransitionId);
        Assert.True(PillIsAtRest(rig.Pill));
        Assert.Equal(expected == IslandVisualState.Expanded ? 380d : 160d, rig.Window.Width);
    });

    [Fact]
    public void ThePillSizeDuringAnimationNeverChangesTheSizeTheWindowAsksFor() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: false);
        rig.ViewModel.Toggle();
        var content = rig.Window.MeasureContent(new Size(1000, 1000));
        Pump(() => rig.Pill.ActualHeight > 60, 1000);

        var during = rig.Window.MeasureContent(new Size(1000, 1000));

        Assert.Equal(content, during);
    });

    [Fact]
    public void DisposeStopsAnimatingAndRestoresThePill() => RunOnSta(() =>
    {
        var rig = Start(reduceMotion: false);
        rig.ViewModel.Toggle();
        Assert.True(rig.Pill.HasAnimatedProperties);

        rig.Transition.Dispose();

        Assert.True(PillIsAtRest(rig.Pill));
        rig.Transition.Dispose(); // idempotent
        rig.ViewModel.Toggle(); // no longer animated by the disposed controller
        Assert.False(rig.Pill.HasAnimatedProperties);
        rig.Position.Dispose();
        rig.Window.Close();
    });

    [Fact]
    public void EscapeIsBoundToCollapse() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: true);
        rig.ViewModel.Toggle();
        Pump(() => rig.ViewModel.State == IslandVisualState.Expanded, 1000);

        var binding = rig.Window.InputBindings.OfType<KeyBinding>().Single();

        Assert.Equal(Key.Escape, binding.Key);
        Assert.Same(rig.ViewModel.CollapseCommand, binding.Command);
    });

    [Fact]
    public void ConstructorRejectsNulls() => RunOnSta(() =>
    {
        using var rig = Start(reduceMotion: true);
        Assert.Throws<ArgumentNullException>(() => new IslandTransitionController(null!, rig.ViewModel, false));
        Assert.Throws<ArgumentNullException>(() => new IslandTransitionController(rig.Window, null!, false));
    });

    private sealed class FakeEnvironment(DisplayInfo display) : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Success();

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() => Result.Success<IReadOnlyList<DisplayInfo>>([display]);

        public void Dispose()
        {
        }
    }

    private sealed class FakeLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }
}

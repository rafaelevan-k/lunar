using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lunar.Desktop.Behaviors;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.Eyes;
using Xunit;

namespace Lunar.Desktop.UITests.Controls;

// The celebration and the press feel of the UI revision: they exist, they draw only, and reduced motion switches them off.
public sealed class PlayfulEffectsTests
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
            throw new InvalidOperationException("WPF effect check failed.", failure);
    }

    private static ConfettiBurst Sized(bool reduceMotion)
    {
        var confetti = new ConfettiBurst(new Random(7));
        Motion.SetReduceMotion(confetti, reduceMotion);
        var host = new Grid { Width = 380, Height = 320 };
        host.Children.Add(confetti);
        host.Measure(new Size(380, 320));
        host.Arrange(new Rect(0, 0, 380, 320));
        host.UpdateLayout();
        return confetti;
    }

    [Fact]
    public void ABurstThrowsPiecesThatTakeNoInputAndOnlyOneRunsAtATime() => RunOnSta(() =>
    {
        var confetti = Sized(reduceMotion: false);

        Assert.True(confetti.Burst());
        Assert.Equal(ConfettiBurst.Pieces, confetti.Children.Count);
        Assert.Equal(ConfettiBurst.Pieces, confetti.ActivePieces);
        Assert.False(confetti.IsHitTestVisible);
        Assert.All(confetti.Children.OfType<UIElement>(), piece => Assert.False(piece.IsHitTestVisible));
        Assert.False(confetti.Burst()); // one at a time

        confetti.Clear();
        Assert.Empty(confetti.Children);
        Assert.Equal(0, confetti.ActivePieces);
    });

    [Fact]
    public void ReducedMotionPlaysNoConfetti() => RunOnSta(() =>
    {
        var confetti = Sized(reduceMotion: true);

        Assert.False(confetti.Burst());
        Assert.Empty(confetti.Children);
    });

    [Fact]
    public void ABurstBeforeTheLayerHasASizeDoesNothing() => RunOnSta(() =>
    {
        var confetti = new ConfettiBurst();

        Assert.False(confetti.Burst());
    });

    [Fact]
    public void ThePressFeelIsAnAttachedSwitchThatOnlyChangesARenderTransform() => RunOnSta(() =>
    {
        var button = new Button { Width = 40, Height = 24 };

        Squish.SetEnabled(button, true);
        Assert.True(Squish.GetEnabled(button));
        Assert.Equal(new Point(0.5, 0.5), button.RenderTransformOrigin);
        Assert.Equal(40, button.Width); // layout is never touched

        Squish.SetEnabled(button, false);
        Assert.False(Squish.GetEnabled(button));
    });

    [Fact]
    public void TheEyesCanSmileWithAndWithoutMotion() => RunOnSta(() =>
    {
        var view = new EyesView();
        var cheek = (TranslateTransform)view.FindName("LeftCheekOffset");
        Assert.Equal(30, cheek.Y); // hidden below the eye at rest

        view.Celebrate();
        Assert.True(cheek.HasAnimatedProperties);

        view.Celebrate(); // a second call restarts it without error
        Assert.True(((TranslateTransform)view.FindName("RightCheekOffset")).HasAnimatedProperties);
    });
}

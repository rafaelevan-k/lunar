using Lunar.Desktop.Navigation;
using Xunit;

namespace Lunar.Desktop.UITests.Navigation;

// Pure logic: no WPF is touched.
public sealed class NavigationLogicTests
{
    private static PageNavigationViewModel Navigation() => new(["System", "Media", "Pomodoro", "File converter", "Notes", "Usage"]);

    // ---- PageNavigationViewModel -----------------------------------------------------------------------------------

    [Fact]
    public void StartsOnTheFirstPage()
    {
        var nav = Navigation();

        Assert.Equal(0, nav.SelectedIndex);
        Assert.Equal("System", nav.SelectedTitle);
        Assert.False(nav.CanGoPrevious);
        Assert.True(nav.CanGoNext);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(0)] // already selected
    public void OutOfRangeOrRepeatedSelectionIsRefusedAndChangesNothing(int index)
    {
        var nav = Navigation();
        var changes = new List<string?>();
        nav.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(nav.GoTo(index));

        Assert.Equal(0, nav.SelectedIndex);
        Assert.Empty(changes);
    }

    [Fact]
    public void NextAndPreviousStopAtTheEnds()
    {
        var nav = Navigation();

        Assert.False(nav.Previous());
        Assert.True(nav.Next());
        Assert.True(nav.Next());
        Assert.True(nav.Next());
        Assert.True(nav.Next());
        Assert.True(nav.Next());
        Assert.False(nav.Next());
        Assert.Equal(5, nav.SelectedIndex);
        Assert.False(nav.CanGoNext);
        Assert.True(nav.CanGoPrevious);
        Assert.True(nav.Previous());
        Assert.True(nav.Previous());
        Assert.True(nav.Previous());
        Assert.True(nav.Previous());
        Assert.True(nav.Previous());
        Assert.False(nav.Previous());
        Assert.Equal(0, nav.SelectedIndex);
    }

    [Fact]
    public void ChangingThePageNotifiesEachPropertyAndTheCommands()
    {
        var nav = Navigation();
        var changes = new List<string?>();
        var nextNotices = 0;
        nav.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        nav.NextCommand.CanExecuteChanged += (_, _) => nextNotices++;

        nav.GoTo(5);

        Assert.Equal(["SelectedIndex", "SelectedTitle", "CanGoNext", "CanGoPrevious"], changes);
        Assert.Equal(1, nextNotices);
        Assert.False(nav.NextCommand.CanExecute(null));
        Assert.True(nav.PreviousCommand.CanExecute(null));
        nav.PreviousCommand.Execute(null);
        Assert.Equal(4, nav.SelectedIndex);
    }

    [Fact]
    public void RandomMovesNeverLeaveTheRange()
    {
        var random = new Random(5);
        var nav = Navigation();

        for (var i = 0; i < 5000; i++)
        {
            switch (random.Next(3))
            {
                case 0: nav.Next(); break;
                case 1: nav.Previous(); break;
                default: nav.GoTo(random.Next(-5, 9)); break;
            }

            Assert.InRange(nav.SelectedIndex, 0, 5);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void ExactlySixTitlesAreRequired(int count)
    {
        Assert.Throws<ArgumentException>(() => new PageNavigationViewModel(Enumerable.Repeat("x", count).ToList()));
        Assert.Throws<ArgumentNullException>(() => new PageNavigationViewModel(null!));
        Assert.Throws<ArgumentException>(() => new PageNavigationViewModel(["a", " ", "c", "d", "e", "f"]));
    }

    // ---- SwipeRules ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(378, 55)] // a quarter of 378 is 94, so the fixed 55 DIP applies
    [InlineData(200, 50)] // a quarter of 200 is smaller than 55
    [InlineData(0, 55)]
    [InlineData(double.NaN, 55)]
    public void ThresholdIsTheSmallerOf55DipAndAQuarterOfThePage(double width, double expected) =>
        Assert.Equal(expected, SwipeRules.Threshold(width));

    [Theory]
    [InlineData(1, -54, 0, 1)] // just under the threshold: snaps back
    [InlineData(1, -55, 0, 2)] // at the threshold: next page
    [InlineData(1, 55, 0, 0)] // dragging right goes back
    [InlineData(1, -100, 0, 2)]
    [InlineData(1, 30, 0, 1)]
    public void DistanceDecidesThePageWithoutAFlick(int index, double delta, double velocity, int expected) =>
        Assert.Equal(expected, SwipeRules.TargetIndex(index, 3, delta, velocity, 378));

    [Theory]
    [InlineData(1, -20, -0.8, 2)] // a short fast flick to the left
    [InlineData(1, 20, 0.8, 0)]
    [InlineData(1, -8, -2.0, 1)] // too short to be a flick
    [InlineData(1, -20, 0.8, 1)] // fast, but the finger has turned around
    [InlineData(1, -20, -0.3, 1)] // slow
    public void AFlickChangesThePageEvenBelowTheThreshold(int index, double delta, double velocity, int expected) =>
        Assert.Equal(expected, SwipeRules.TargetIndex(index, 3, delta, velocity, 378));

    [Theory]
    [InlineData(0, 300, 0)] // already on the first page, dragging right
    [InlineData(2, -300, 2)] // already on the last page, dragging left
    [InlineData(0, -300, 1)]
    [InlineData(2, 300, 1)]
    public void TheTargetNeverLeavesTheRange(int index, double delta, int expected) =>
        Assert.Equal(expected, SwipeRules.TargetIndex(index, 3, delta, 0, 378));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void NonFiniteInputKeepsTheCurrentPage(double bad, double velocity)
    {
        Assert.Equal(1, SwipeRules.TargetIndex(1, 3, bad, velocity, 378));
        Assert.Equal(1, SwipeRules.TargetIndex(1, 3, -100, bad, 378));
        Assert.Equal(2, SwipeRules.TargetIndex(7, 3, 0, 0, 378)); // an out-of-range index is clamped first
        Assert.Equal(0, SwipeRules.ContentOffset(1, 3, bad, 378));
    }

    [Fact]
    public void ContentFollowsTheFingerInsideTheRangeButNeverMoreThanOnePage()
    {
        Assert.Equal(-40, SwipeRules.ContentOffset(1, 3, -40, 378));
        Assert.Equal(40, SwipeRules.ContentOffset(1, 3, 40, 378));
        Assert.Equal(-378, SwipeRules.ContentOffset(1, 3, -2000, 378));
        Assert.Equal(378, SwipeRules.ContentOffset(1, 3, 2000, 378));
    }

    [Fact]
    public void PastTheEndsTheContentIsDampedAndCapped()
    {
        Assert.Equal(40 * SwipeRules.RubberBandFactor, SwipeRules.ContentOffset(0, 3, 40, 378), 6);
        Assert.Equal(-40 * SwipeRules.RubberBandFactor, SwipeRules.ContentOffset(2, 3, -40, 378), 6);
        Assert.Equal(378 * SwipeRules.RubberBandMaxShare, SwipeRules.ContentOffset(0, 3, 5000, 378), 6);
        Assert.Equal(-378 * SwipeRules.RubberBandMaxShare, SwipeRules.ContentOffset(2, 3, -5000, 378), 6);
        Assert.Equal(-40, SwipeRules.ContentOffset(0, 3, -40, 378)); // toward the neighbour, free
    }

    // ---- SwiperController ------------------------------------------------------------------------------------------

    private static SwiperController Controller(PageNavigationViewModel nav) => new(nav) { PageWidth = 378 };

    [Fact]
    public void ADragPastTheThresholdSelectsTheNextPage()
    {
        var nav = Navigation();
        var swiper = Controller(nav);

        Assert.True(swiper.Begin(300, 0));
        Assert.True(swiper.IsDragging);
        Assert.Equal(-30, swiper.Move(270, 400)); // slow: no flick
        Assert.Equal(-80, swiper.Move(220, 800));
        var result = swiper.End(1200);

        Assert.True(result.Changed);
        Assert.Equal(1, result.TargetIndex);
        Assert.Equal(1, nav.SelectedIndex);
        Assert.False(swiper.IsDragging);
        Assert.Equal(0, swiper.Offset);
    }

    [Fact]
    public void AShortSlowDragSnapsBack()
    {
        var nav = Navigation();
        var swiper = Controller(nav);
        swiper.Begin(300, 0);
        swiper.Move(280, 500);

        var result = swiper.End(1000);

        Assert.False(result.Changed);
        Assert.Equal(0, nav.SelectedIndex);
    }

    [Fact]
    public void AQuickFlickChangesThePage()
    {
        var nav = Navigation();
        var swiper = Controller(nav);
        swiper.Begin(300, 0);
        swiper.Move(285, 10);
        swiper.Move(270, 20); // 30 DIP in 20 ms: 1.5 DIP/ms

        Assert.True(swiper.End(25).Changed);
        Assert.Equal(1, nav.SelectedIndex);
    }

    [Fact]
    public void PausingBeforeReleaseCancelsTheFlick()
    {
        var nav = Navigation();
        var swiper = Controller(nav);
        swiper.Begin(300, 0);
        swiper.Move(285, 10);
        swiper.Move(270, 20);

        var result = swiper.End(400); // the finger rested for 380 ms

        Assert.False(result.Changed);
        Assert.Equal(0, nav.SelectedIndex);
    }

    [Fact]
    public void DraggingPastTheFirstPageStretchesButNeverChangesTheIndex()
    {
        var nav = Navigation();
        var swiper = Controller(nav);
        swiper.Begin(100, 0);

        var offset = swiper.Move(400, 500);

        Assert.True(offset > 0 && offset <= 378 * SwipeRules.RubberBandMaxShare);
        Assert.False(swiper.End(1000).Changed);
        Assert.Equal(0, nav.SelectedIndex);
    }

    [Fact]
    public void RepeatedDragsAtTheEndNeverLeaveTheRange()
    {
        var nav = Navigation();
        var swiper = Controller(nav);

        for (var i = 0; i < 10; i++)
        {
            swiper.Begin(300, i * 2000L);
            swiper.Move(100, (i * 2000L) + 500);
            swiper.End((i * 2000L) + 600);
            Assert.InRange(nav.SelectedIndex, 0, 5);
        }

        Assert.Equal(5, nav.SelectedIndex);
    }

    [Fact]
    public void OnlyOneDragRunsAtATimeAndCancelChangesNothing()
    {
        var nav = Navigation();
        var swiper = Controller(nav);
        Assert.True(swiper.Begin(300, 0));
        Assert.False(swiper.Begin(200, 1)); // already dragging

        swiper.Move(100, 500);
        swiper.Cancel();

        Assert.False(swiper.IsDragging);
        Assert.Equal(0, swiper.Offset);
        Assert.Equal(0, nav.SelectedIndex);
        Assert.False(swiper.End(900).Changed); // ending without a drag is harmless
    }

    [Fact]
    public void ADragCannotStartUntilThePageWidthIsKnownOrWithBadInput()
    {
        var nav = Navigation();
        Assert.False(new SwiperController(nav).Begin(10, 0)); // width 0
        var swiper = Controller(nav);
        Assert.False(swiper.Begin(double.NaN, 0));
        Assert.Equal(0, swiper.Move(5, 1)); // move without a drag is ignored
        Assert.Throws<ArgumentNullException>(() => new SwiperController(null!));
    }

    // ---- HorizontalWheelGesture ------------------------------------------------------------------------------------

    [Fact]
    public void OneNotchIsOneStepAndTheRestOfTheSwipeIsIgnored()
    {
        var gesture = new HorizontalWheelGesture();

        Assert.Equal(0, gesture.Add(60, 0));
        Assert.Equal(1, gesture.Add(60, 20)); // 120 reached
        for (var t = 40; t < 400; t += 20)
            Assert.Equal(0, gesture.Add(120, t)); // inertia of the same swipe: locked

        Assert.Equal(1, gesture.Add(120, 800)); // a pause of more than 250 ms starts a new gesture
        Assert.Equal(0, gesture.Add(120, 820)); // and that one is locked as well
    }

    [Fact]
    public void ANewSwipeAfterAPauseStepsAgainInEitherDirection()
    {
        var gesture = new HorizontalWheelGesture();
        Assert.Equal(1, gesture.Add(120, 0));
        Assert.Equal(-1, gesture.Add(-120, 1000));
        Assert.Equal(0, gesture.Add(-120, 1020)); // locked within the gesture
        Assert.Equal(-1, gesture.Add(-240, 2000));
    }

    [Fact]
    public void SmallDeltasAddUpAndAReversalRestartsTheCount()
    {
        var gesture = new HorizontalWheelGesture();

        Assert.Equal(0, gesture.Add(40, 0));
        Assert.Equal(0, gesture.Add(40, 10));
        Assert.Equal(0, gesture.Add(-40, 20)); // the finger turned around: count restarts
        Assert.Equal(0, gesture.Add(-40, 30));
        Assert.Equal(-1, gesture.Add(-40, 40));
        Assert.Equal(0, gesture.Add(0, 50)); // a zero delta does nothing
    }

    [Fact]
    public void ResetForgetsTheGesture()
    {
        var gesture = new HorizontalWheelGesture();
        gesture.Add(120, 0);

        gesture.Reset();

        Assert.Equal(1, gesture.Add(120, 10)); // not locked any more
    }
}

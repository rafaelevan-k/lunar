using Xunit;

namespace Lunar.Desktop.UITests.Lifecycle;

// The counter used by the FE-25 leak checks must itself count correctly; otherwise "back to the baseline" proves nothing.
public sealed class EventAuditTests
{
    private sealed class Publisher
    {
        public event EventHandler? Raised;

        public void Fire() => Raised?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Derived : System.ComponentModel.BindingList<int>
    {
    }

    [Fact]
    public void ItCountsTheHandlersOfAFieldLikeEvent()
    {
        var publisher = new Publisher();
        Assert.Equal(0, EventAudit.HandlerCount(publisher, nameof(Publisher.Raised)));

        EventHandler one = (_, _) => { };
        EventHandler two = (_, _) => { };
        publisher.Raised += one;
        publisher.Raised += two;
        Assert.Equal(2, EventAudit.HandlerCount(publisher, nameof(Publisher.Raised)));

        publisher.Raised -= one;
        publisher.Raised -= two;
        Assert.Equal(0, EventAudit.HandlerCount(publisher, nameof(Publisher.Raised)));
        publisher.Fire();
    }

    [Fact]
    public void AnEventThatIsNotFieldLikeIsReportedInsteadOfCountedAsZero() =>
        Assert.Throws<InvalidOperationException>(() => EventAudit.HandlerCount(new Derived(), "NoSuchEvent"));
}

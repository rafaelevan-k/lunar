using Lunar.Windows.Media;
using Xunit;

namespace Lunar.Windows.IntegrationTests.Media;

public sealed class SubscriptionDiffTests
{
    private sealed class Item(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void NewItemsAreAddedAndItemsThatLeftAreRemoved()
    {
        Item a = new("a"), b = new("b"), c = new("c");

        var (added, removed) = SubscriptionDiff.Compare([a, b], [b, c]);

        Assert.Equal([c], added);
        Assert.Equal([a], removed);
    }

    [Fact]
    public void ItemsThatStayAreNeitherAddedNorRemoved()
    {
        Item a = new("a"), b = new("b");

        var (added, removed) = SubscriptionDiff.Compare([a, b], [b, a]);

        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void ItemsAreComparedByReferenceNotByContent()
    {
        var (added, removed) = SubscriptionDiff.Compare([new Item("x")], [new Item("x")]);

        Assert.Single(added); // a new session object with the same name is a new session: it gets its own subscription
        Assert.Single(removed);
    }

    [Fact]
    public void FromNothingEverythingIsAddedAndToNothingEverythingIsRemoved()
    {
        Item a = new("a"), b = new("b");

        Assert.Equal([a, b], SubscriptionDiff.Compare([], [a, b]).Added);
        Assert.Equal([a, b], SubscriptionDiff.Compare([a, b], []).Removed);
    }

    [Fact]
    public void RepeatedItemsAreReportedOnce()
    {
        var a = new Item("a");

        var (added, removed) = SubscriptionDiff.Compare([a, a], [new Item("n"), a]);

        Assert.Single(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void EveryPlayerThatCameAndWentLeavesNothingSubscribed()
    {
        // Simulates 500 reads in which players open and close: the set subscribed always equals the current list.
        var subscribed = new List<Item>();
        var current = new List<Item>();
        var random = new Random(42);
        for (var step = 0; step < 500; step++)
        {
            var previous = current;
            current = [.. previous.Where(_ => random.Next(3) != 0)];
            if (random.Next(2) == 0)
                current.Add(new Item("p" + step));

            var (added, removed) = SubscriptionDiff.Compare(previous, current);
            foreach (var item in removed)
                Assert.True(subscribed.Remove(item));
            foreach (var item in added)
            {
                Assert.DoesNotContain(item, subscribed);
                subscribed.Add(item);
            }

            Assert.Equal(current.Count, subscribed.Count);
            Assert.All(current, item => Assert.Contains(item, subscribed));
        }
    }

    [Fact]
    public void NullListsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SubscriptionDiff.Compare<Item>(null!, []));
        Assert.Throws<ArgumentNullException>(() => SubscriptionDiff.Compare<Item>([], null!));
    }
}

namespace Lunar.Windows.Media;

/// <summary>
/// Which items entered and which left a tracked list, by reference. The real backend uses it so that exactly the sessions in the
/// current list carry event subscriptions: a new one is subscribed once, one that left is unsubscribed once, and one that stays is
/// left alone. Pure, so the rule is tested without Windows.
/// </summary>
internal static class SubscriptionDiff
{
    public static (List<T> Added, List<T> Removed) Compare<T>(IReadOnlyList<T> previous, IReadOnlyList<T> current)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var removed = previous.Where(p => !current.Any(c => ReferenceEquals(c, p))).Distinct().ToList();
        var added = current.Where(c => !previous.Any(p => ReferenceEquals(c, p))).Distinct().ToList();
        return (added, removed);
    }
}

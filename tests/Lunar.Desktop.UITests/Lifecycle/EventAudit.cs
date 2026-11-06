using System.Reflection;

namespace Lunar.Desktop.UITests.Lifecycle;

// FE-25: the subscription-leak checks count how many handlers an event holds, so "a short-lived object let go of a long-lived publisher" is
// a number a test can compare, not a hope. Works for field-like events (the C# default), which is what the app's publishers use.
internal static class EventAudit
{
    /// <summary>The number of handlers currently attached to <paramref name="eventName"/> of <paramref name="publisher"/>.</summary>
    internal static int HandlerCount(object publisher, string eventName)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        for (var type = publisher.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is null)
                continue;
            return field.GetValue(publisher) is Delegate handlers ? handlers.GetInvocationList().Length : 0;
        }

        throw new InvalidOperationException($"{publisher.GetType().Name}.{eventName} is not a field-like event.");
    }
}

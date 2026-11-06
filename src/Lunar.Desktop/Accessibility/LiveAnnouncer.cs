using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Lunar.Desktop.Accessibility;

/// <summary>
/// Tells a screen reader about a change that happens without keyboard focus moving (a page change, a pop-up that never takes focus), FE-24.
/// The one place that raises a UI Automation notification. When no screen reader listens, the event is a no-op, so calling it is always safe.
/// Text is words for people only: it never carries file names, titles or other private data beyond what the screen already shows.
/// </summary>
internal static class LiveAnnouncer
{
    /// <summary>Raises a polite notification from <paramref name="anchor"/>; false when there is nothing to say or no peer to say it from.</summary>
    internal static bool Announce(UIElement anchor, string text, string activityId, bool interrupt = false)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var peer = UIElementAutomationPeer.FromElement(anchor) ?? UIElementAutomationPeer.CreatePeerForElement(anchor);
        if (peer is null)
            return false;

        peer.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
            text,
            activityId);
        return true;
    }
}

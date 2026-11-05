using Lunar.Application.Usage.Models;

namespace Lunar.Desktop.Features.Usage;

/// <summary>What the reset pop-up says (FE-28). <see cref="AutomationName"/> is what a screen reader announces.</summary>
public sealed record ResetToastContent(string Title, string Message, string AutomationName)
{
    public const string HeadingText = "Claude limit reset";

    /// <summary>
    /// The words for one batch of resets: "Your 5-hour limit has reset. It was at 92%." for one limit (the share is left out when unknown),
    /// "Your 5-hour and weekly limits have reset." for both. Resets are given in any order; a limit named twice counts once, the latest wins.
    /// </summary>
    public static ResetToastContent For(IReadOnlyCollection<UsageLimitReset> resets)
    {
        ArgumentNullException.ThrowIfNull(resets);
        if (resets.Count == 0)
            throw new ArgumentException("At least one reset is needed.", nameof(resets));

        var five = resets.LastOrDefault(r => r.Kind == UsageWindowKind.FiveHour);
        var week = resets.LastOrDefault(r => r.Kind == UsageWindowKind.Weekly);
        string message;
        if (five is not null && week is not null)
        {
            message = "Your 5-hour and weekly limits have reset.";
        }
        else
        {
            var only = five ?? week!;
            var name = only.Kind == UsageWindowKind.FiveHour ? "5-hour" : "weekly";
            message = $"Your {name} limit has reset." + (only.PreviousUsedPercent is { } percent ? $" It was at {percent}%." : string.Empty);
        }

        return new ResetToastContent(HeadingText, message, "Claude: " + message);
    }
}

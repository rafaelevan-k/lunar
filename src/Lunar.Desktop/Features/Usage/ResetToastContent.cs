using Lunar.Application.Usage.Models;
using Lunar.Desktop.Resources;

namespace Lunar.Desktop.Features.Usage;

/// <summary>What the reset pop-up says (FE-28). <see cref="AutomationName"/> is what a screen reader announces.</summary>
public sealed record ResetToastContent(string Title, string Message, string AutomationName)
{
    public static string HeadingText => Strings.Get("Toast.Heading");

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
            message = Strings.Get("Toast.Both");
        }
        else
        {
            var only = five ?? week!;
            message = Strings.Get(only.Kind == UsageWindowKind.FiveHour ? "Toast.OneFive" : "Toast.OneWeek")
                + (only.PreviousUsedPercent is { } percent ? Strings.Format("Toast.WasAt", percent) : string.Empty);
        }

        return new ResetToastContent(HeadingText, message, Strings.Format("Toast.AutomationName", message));
    }
}

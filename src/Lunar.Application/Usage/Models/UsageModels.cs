namespace Lunar.Application.Usage.Models;

/// <summary>Which of the account's usage limits a reading is about.</summary>
public enum UsageWindowKind
{
    /// <summary>The rolling limit that resets about every five hours.</summary>
    FiveHour,

    /// <summary>The weekly limit.</summary>
    Weekly
}

/// <summary>
/// One limit as the service reports it. Both values are exactly what the service said and are null when it said nothing, so a screen never
/// shows an invented number: <see cref="UsedFraction"/> is 0 to 1 (0.67 is 67 percent used) and <see cref="ResetsAt"/> is when the limit resets.
/// </summary>
public sealed record UsageWindow(UsageWindowKind Kind, double? UsedFraction, DateTimeOffset? ResetsAt)
{
    /// <summary>Percent used as a whole number 0 to 100, or null when the service gave no figure.</summary>
    public int? UsedPercent => UsedFraction is { } f ? (int)Math.Round(Math.Clamp(f, 0, 1) * 100, MidpointRounding.AwayFromZero) : null;
}

/// <summary>A reading of the account's limits at one moment. A limit the service did not report is null.</summary>
/// <param name="FiveHour">The five-hour limit, or null.</param>
/// <param name="Weekly">The weekly limit, or null.</param>
/// <param name="FetchedAt">When this reading was taken (local clock).</param>
public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? Weekly, DateTimeOffset FetchedAt);

/// <summary>
/// The user's own login for the usage service, held in memory only. It prints as nothing but its type name, so a log line or a debugger
/// text of an object that holds it never shows the token. Never store it, log it or put it in a message.
/// </summary>
public sealed class UsageCredential
{
    public UsageCredential(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Token = token;
    }

    /// <summary>The bearer token. Use it for one request and let go.</summary>
    public string Token { get; }

    public override string ToString() => nameof(UsageCredential);
}

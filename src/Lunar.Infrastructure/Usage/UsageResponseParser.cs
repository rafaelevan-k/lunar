using System.Globalization;
using System.Text.Json;
using Lunar.Application.Usage.Models;
using Lunar.Domain.Shared;

namespace Lunar.Infrastructure.Usage;

/// <summary>
/// Reads the reply of the usage service into a <see cref="UsageSnapshot"/>. The format is NOT documented by Anthropic (ADR-0014): it is
/// what community tools report, so this reads defensively and shows nothing it cannot read:
/// - Windows are looked for as <c>five_hour</c> and <c>seven_day</c> at the top or inside a <c>limits</c> object.
/// - Used: <c>utilization_pct</c> is a percent; <c>utilization</c> is a fraction when it is 1 or less and a percent above 1. Anything else, or not a number, is "no figure".
/// - Reset: an ISO 8601 text, or a number of seconds (or milliseconds) since 1970. Anything else is "no time".
/// - A window that is null or missing is simply absent. A reply with neither window, or that is not an object, is a typed failure and nothing is invented.
/// Input is bounded (depth and size are checked by the caller); this never throws for bad data.
/// </summary>
internal static class UsageResponseParser
{
    public static Result<UsageSnapshot> Parse(ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unrecognised();

            var nested = root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object;
            // The windows are looked for inside "limits" first (one reported shape), then at the top.
            var five = (nested ? Window(limits, "five_hour", UsageWindowKind.FiveHour) : null) ?? Window(root, "five_hour", UsageWindowKind.FiveHour);
            var week = (nested ? Window(limits, "seven_day", UsageWindowKind.Weekly) : null) ?? Window(root, "seven_day", UsageWindowKind.Weekly);
            return five is null && week is null ? Unrecognised() : Result.Success(new UsageSnapshot(five, week, now));
        }
        catch (JsonException)
        {
            return Unrecognised();
        }
        catch (InvalidOperationException)
        {
            return Unrecognised();
        }
    }

    private static UsageWindow? Window(JsonElement scope, string name, UsageWindowKind kind)
    {
        if (!scope.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        return new UsageWindow(kind, UsedFraction(window), ResetsAt(window));
    }

    private static double? UsedFraction(JsonElement window)
    {
        if (Number(window, "utilization_pct") is { } percent)
            return Clamp(percent / 100);
        if (Number(window, "utilization") is { } value)
            return Clamp(value <= 1 ? value : value / 100);
        return null;
    }

    private static double? Clamp(double value) => double.IsFinite(value) && value >= 0 ? Math.Min(value, 1) : null;

    private static double? Number(JsonElement window, string name) =>
        window.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;

    private static DateTimeOffset? ResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var element))
            return null;
        try
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String when DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out var parsed):
                    return parsed;
                case JsonValueKind.Number when element.TryGetInt64(out var epoch) && epoch > 0:
                    return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // a number too large to be a date
        }

        return null;
    }

    private static Result<UsageSnapshot> Unrecognised() =>
        Result.Failure<UsageSnapshot>(new OperationError(ErrorCode.Unexpected, "Claude's usage reply was not in a form Lunar understands, so nothing is shown."));
}

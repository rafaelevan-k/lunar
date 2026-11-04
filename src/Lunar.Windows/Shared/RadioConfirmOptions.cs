namespace Lunar.Windows.Shared;

/// <summary>How the service waits for Windows to confirm a radio change.</summary>
internal sealed record RadioConfirmOptions
{
    public static RadioConfirmOptions Default { get; } = new();

    /// <summary>How many times the radio is read back after a write before giving up. The wait is bounded: attempts x interval.</summary>
    public int ConfirmAttempts { get; init; } = 10;

    public TimeSpan ConfirmInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Replaced by tests so confirmation does not take real time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

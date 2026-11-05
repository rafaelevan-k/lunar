namespace Lunar.Application.Usage;

/// <summary>Timing of the usage monitor (ADR-0014 decision 3). The defaults are proposals: few, slow requests, never a busy loop.</summary>
public sealed record UsageMonitorOptions
{
    /// <summary>How often to look while the island is open.</summary>
    public TimeSpan OpenInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to look while the island is closed.</summary>
    public TimeSpan ClosedInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest wait after repeated failures; the wait doubles with each failure up to this.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>The shortest time between two requests of the loop, whatever else asks for one.</summary>
    public TimeSpan MinDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A refresh asked for by the user while the last reading is good is answered from it when it is younger than this.</summary>
    public TimeSpan RefreshGap { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After a known reset time, how long to wait before looking, so the service has rolled over.</summary>
    public TimeSpan ResetGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A new reset time must be later than the old one by more than this to count as a new period (small shifts are noise).</summary>
    public TimeSpan ResetTolerance { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Waits for the given time or until cancelled. Replaced by tests; the default is <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

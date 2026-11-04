namespace Lunar.Conversion.Pipeline;

/// <summary>
/// How the queue paces work. The defaults are conservative proposals for the user to confirm: two conversions at a time (page
/// rasterization and picture decoding are memory-hungry), room for 500 waiting and running jobs, and 15 minutes per job.
/// </summary>
public sealed record ConversionQueueOptions
{
    /// <summary>Conversions running at the same time.</summary>
    public int MaxConcurrency { get; init; } = 2;

    /// <summary>Jobs that may be waiting, running or cancelling together.</summary>
    public int MaxQueued { get; init; } = 500;

    /// <summary>How long one job may run before it is stopped and reported as failed.</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How many final jobs the queue remembers; the oldest are forgotten first.</summary>
    public int MaxRetainedFinished { get; init; } = 200;

    public ConversionQueueOptions Validated()
    {
        if (MaxConcurrency < 1 || MaxConcurrency > 16)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "Between 1 and 16 conversions may run at once.");
        if (MaxQueued < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxQueued), MaxQueued, "The queue must hold at least one job.");
        if (JobTimeout <= TimeSpan.Zero || JobTimeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(JobTimeout), JobTimeout, "The timeout must be positive and at most 24 hours.");
        if (MaxRetainedFinished < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetainedFinished), MaxRetainedFinished, "Cannot be negative.");
        return this;
    }
}

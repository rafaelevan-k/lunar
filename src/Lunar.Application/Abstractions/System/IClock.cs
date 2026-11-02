namespace Lunar.Application.Abstractions.System;

/// <summary>Injectable local time so clock-dependent logic is deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset LocalNow { get; }
}

using Lunar.Application.Abstractions.System;

namespace Lunar.Desktop.Composition;

/// <summary>
/// Plain local time for view models created without a container (tests, design-time). The app registers the Windows
/// clock (<c>WindowsClock</c>, BE-14) instead, which also knows the time zone and regional format.
/// </summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset LocalNow => TimeProvider.System.GetLocalNow();
}

using Lunar.Application.Abstractions.System;

namespace Lunar.Desktop.Composition;

/// <summary>
/// Local time from the system, the only <see cref="IClock"/> until the Windows adapter (BE-14) is built. It lives in
/// the composition root so the island state machine can be created without that task.
/// </summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset LocalNow => TimeProvider.System.GetLocalNow();
}

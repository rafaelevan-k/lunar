namespace Lunar.Application.Abstractions.Audio;

/// <summary>
/// A sound input Windows offers (ADR-0022). <see cref="Id"/> is opaque and may be saved as the user's choice. <see cref="Name"/> is only for
/// showing to the user: neither is ever logged.
/// </summary>
public sealed record AudioInput(string Id, string Name, bool IsWindowsDefault);

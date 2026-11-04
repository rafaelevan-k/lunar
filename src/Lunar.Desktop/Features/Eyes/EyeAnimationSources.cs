namespace Lunar.Desktop.Features.Eyes;

/// <summary>Random numbers in [0, 1) so tests can script every draw.</summary>
internal interface IRandomSource
{
    double NextDouble();
}

internal sealed class SystemRandomSource : IRandomSource
{
    public static SystemRandomSource Instance { get; } = new();

    public double NextDouble() => Random.Shared.NextDouble();
}

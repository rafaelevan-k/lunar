using Lunar.Domain.Shared;

namespace Lunar.Application.Abstractions.Audio;

/// <summary>Records one microphone to one MP3 file (ADR-0022).</summary>
public interface IVoiceCaptureEngine
{
    /// <summary>
    /// Opens the input (null means the Windows default) and starts writing a new file at <paramref name="partFilePath"/>, which must not
    /// exist yet. Fails with <see cref="ErrorCode.DeviceUnavailable"/> (no such input, or none at all), <see cref="ErrorCode.AccessDenied"/>
    /// (Windows' microphone privacy), <see cref="ErrorCode.DependencyMissing"/> (no MP3 encoder on this PC), <see cref="ErrorCode.IoFailure"/>
    /// (the file cannot be created) or <see cref="ErrorCode.Cancelled"/>. On failure nothing is left behind and the microphone is released.
    /// </summary>
    Task<Result<IVoiceCaptureSession>> StartAsync(string? inputId, string partFilePath, CancellationToken ct);
}

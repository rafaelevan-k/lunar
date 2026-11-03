namespace Lunar.Windows.Audio;

/// <summary>What went wrong at the audio layer, in terms the service can act on.</summary>
internal enum AudioFailure
{
    /// <summary>The endpoint went away or changed under the call (unplugged, disabled, driver reset, audio service restart). Rebinding may help.</summary>
    DeviceGone,

    /// <summary>Windows refused the call.</summary>
    AccessDenied,

    /// <summary>Anything else; carries the HRESULT for the log.</summary>
    Other
}

/// <summary>A failed audio call. Carries only a kind and an HRESULT, never device names or user data.</summary>
internal sealed class AudioException : Exception
{
    public AudioException(AudioFailure failure, int hresult, string message)
        : base(message)
    {
        Failure = failure;
        HResult = hresult; // the inherited HRESULT property, so logs and callers read it the usual way
    }

    public AudioFailure Failure { get; }
}

/// <summary>One reading of the master volume.</summary>
internal readonly record struct VolumeReading(float Scalar, bool IsMuted);

/// <summary>
/// The default render endpoint's master volume, as the service needs it. The COM implementation is
/// <see cref="ComAudioBackend"/>; tests replace it to simulate unplugged devices, driver errors and device changes
/// without touching real audio hardware.
/// </summary>
internal interface IAudioEndpoint : IDisposable
{
    VolumeReading Read();

    void SetVolume(float scalar);

    void SetMute(bool muted);

    /// <summary>Raised on an arbitrary thread when the volume or mute changes, by anyone (including this app).</summary>
    event Action<VolumeReading>? Changed;
}

/// <summary>Finds the default output device and reports when it changes.</summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>Opens the current default output endpoint, or returns null when there is none. Throws <see cref="AudioException"/> on failure.</summary>
    IAudioEndpoint? OpenDefault();

    /// <summary>Raised on an arbitrary thread when the default output device changed, was added or removed.</summary>
    event Action? DefaultDeviceChanged;
}

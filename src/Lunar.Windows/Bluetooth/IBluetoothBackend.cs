namespace Lunar.Windows.Bluetooth;

/// <summary>What went wrong at the Windows radio API, in terms the service can act on.</summary>
internal enum BluetoothFailure
{
    /// <summary>This Windows (or edition) has no radio API, or the call is not supported.</summary>
    NotSupported,

    /// <summary>Windows refused the call.</summary>
    AccessDenied,

    /// <summary>Anything else; carries the HRESULT for the log.</summary>
    Other
}

/// <summary>A failed radio call. Carries only a kind and an HRESULT, never a device name, address or user data.</summary>
internal sealed class BluetoothException : Exception
{
    public BluetoothException(BluetoothFailure failure, int hresult, string message)
        : base(message)
    {
        Failure = failure;
        HResult = hresult;
    }

    public BluetoothFailure Failure { get; }
}

/// <summary>State of one Bluetooth radio as Windows reports it. <see cref="Disabled"/> means the system disabled it and an app cannot turn it on.</summary>
internal enum BluetoothRadioMode
{
    Unknown,
    On,
    Off,
    Disabled
}

/// <summary>What Windows answered to a request to change a radio.</summary>
internal enum BluetoothAccess
{
    Allowed,
    DeniedByUser,
    DeniedBySystem,
    Unspecified
}

/// <summary>One Bluetooth radio. No name, address or device id is kept: the service has no use for them and they are private.</summary>
internal readonly record struct BluetoothRadioReading(BluetoothRadioMode Mode);

/// <summary>
/// The Windows radio API as the service needs it. The real implementation is <see cref="WinRtBluetoothBackend"/>; tests
/// replace it to simulate no radio, a disabled radio, a refusal and radio changes without touching the machine's Bluetooth.
/// </summary>
internal interface IBluetoothBackend : IDisposable
{
    /// <summary>Reads every Bluetooth radio (none is an empty list). Throws <see cref="BluetoothException"/> on failure.</summary>
    Task<IReadOnlyList<BluetoothRadioReading>> ReadAsync(CancellationToken ct);

    /// <summary>
    /// Asks Windows to turn every switchable Bluetooth radio on or off and returns the first answer that is not
    /// <see cref="BluetoothAccess.Allowed"/>, or Allowed. Throws <see cref="BluetoothException"/> on failure, including
    /// when no radio can be switched.
    /// </summary>
    Task<BluetoothAccess> SetAsync(bool on, CancellationToken ct);

    /// <summary>
    /// Raised on an arbitrary thread when a radio changed state or one was added or removed. It carries no data: the
    /// subscriber reads again.
    /// </summary>
    event Action? Changed;
}

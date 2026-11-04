namespace Lunar.Windows.Network;

/// <summary>What went wrong at the Native Wi-Fi layer, in terms the service can act on.</summary>
internal enum WlanFailure
{
    /// <summary>The WLAN AutoConfig service is not running or cannot be reached. It may come back, so this is a state, not a verdict.</summary>
    ServiceUnavailable,

    /// <summary>This Windows has no Native Wi-Fi at all (no wlanapi, no service), or the driver does not support the call.</summary>
    NotSupported,

    /// <summary>Windows refused the call (for example a policy or a missing privilege for a radio write).</summary>
    AccessDenied,

    /// <summary>Anything else; carries the Win32 code for the log.</summary>
    Other
}

/// <summary>A failed Native Wi-Fi call. Carries only a kind and a Win32 code, never an SSID, interface name or address.</summary>
internal sealed class WlanException : Exception
{
    public WlanException(WlanFailure failure, int code, string message)
        : base(message)
    {
        Failure = failure;
        Code = code;
    }

    public WlanFailure Failure { get; }

    public int Code { get; }
}

/// <summary>One radio switch (software or hardware) of a PHY, as Windows reports it.</summary>
internal enum RadioSwitch
{
    Unknown,
    On,
    Off
}

/// <summary>Native <c>WLAN_INTERFACE_STATE</c>, reduced to what the service distinguishes.</summary>
internal enum WlanInterfaceState
{
    Unknown,
    NotReady,
    Connected,
    AdHocFormed,
    Disconnecting,
    Disconnected,
    Associating,
    Discovering,
    Authenticating
}

/// <summary>The radio of one PHY: the software switch (Windows) and the hardware switch (a key, a BIOS setting).</summary>
internal readonly record struct PhyRadio(RadioSwitch Software, RadioSwitch Hardware);

/// <summary>
/// One Wi-Fi adapter. <see cref="Phys"/> is null when the driver would not report its radio, which is different from a
/// radio reported as off. No name, GUID or SSID is kept: the service has no use for them and they are private.
/// </summary>
internal sealed record WlanInterfaceReading(WlanInterfaceState State, IReadOnlyList<PhyRadio>? Phys);

/// <summary>
/// The Native Wi-Fi API as the service needs it. The real implementation is <see cref="NativeWlanBackend"/>; tests replace
/// it to simulate no adapter, a stopped service, a hardware switch, refused writes and radio changes without touching the
/// machine's Wi-Fi.
/// </summary>
internal interface IWlanBackend : IDisposable
{
    /// <summary>Reads every adapter (none is an empty list). Throws <see cref="WlanException"/> on failure.</summary>
    IReadOnlyList<WlanInterfaceReading> Read();

    /// <summary>Sets the software radio switch of every PHY of every adapter. Throws <see cref="WlanException"/> on failure.</summary>
    void SetSoftwareRadio(bool on);

    /// <summary>
    /// Raised on an arbitrary thread when something about the adapters changed (radio, connection, adapter added or removed).
    /// It carries no data: the subscriber reads again.
    /// </summary>
    event Action? Changed;
}

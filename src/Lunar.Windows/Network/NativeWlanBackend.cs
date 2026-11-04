using System.Runtime.InteropServices;

namespace Lunar.Windows.Network;

/// <summary>
/// The real Native Wi-Fi (wlanapi) implementation of <see cref="IWlanBackend"/> (ADR-0002). It reads adapter state and
/// the two radio switches and writes only the software radio switch. It never asks for the SSID, the current connection,
/// a scan or a profile, so no network name or credential can reach this process. Nothing here needs administrator rights
/// to read; whether Windows allows the radio write is decided by Windows, and a refusal comes back as a typed failure.
///
/// The WLAN client handle is opened on first use and kept so that notifications can be received; it is closed and
/// reopened once if Windows invalidates it (service restart). The notification callback arrives on a wlanapi thread and
/// only raises <see cref="Changed"/>; it never calls back into wlanapi (which would deadlock). Every unmanaged buffer is
/// freed with <c>WlanFreeMemory</c> and the handle is closed on disposal. Calls are serialized by the owning service.
/// </summary>
internal sealed class NativeWlanBackend : IWlanBackend
{
    private const uint ClientVersion = 2;
    private const int RadioStateOpcode = 4; // wlan_intf_opcode_radio_state
    private const uint SourceAcm = 0x8;     // WLAN_NOTIFICATION_SOURCE_ACM: adapter arrival/removal, connect, disconnect
    private const uint SourceMsm = 0x10;    // WLAN_NOTIFICATION_SOURCE_MSM: includes the radio state change
    private const int MaxInterfaces = 64;
    private const int InterfaceInfoSize = 532; // GUID (16) + WCHAR[256] description (512) + state (4)
    private const int InterfaceStateOffset = 528;
    private const int PhyRadioSize = 12;       // index, software state, hardware state
    private const int MaxPhys = 64;

    private readonly object _gate = new();
    private readonly NotificationCallback _callback; // kept alive for as long as it is registered
    private IntPtr _handle;
    private bool _disposed;

    public NativeWlanBackend()
    {
        _callback = OnNotification;
    }

    public event Action? Changed;

    public IReadOnlyList<WlanInterfaceReading> Read() => WithHandle(ReadAll);

    public void SetSoftwareRadio(bool on) =>
        WithHandle(handle =>
        {
            SetAll(handle, on);
            return true;
        });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseHandle();
        }
    }

    // ---- handle lifecycle -------------------------------------------------------------------------------------------

    // Runs a call with the open handle; if Windows says the handle is no longer valid (the service restarted) it reopens once.
    private T WithHandle<T>(Func<IntPtr, T> call)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return call(EnsureOpen());
                }
                catch (WlanException ex) when (attempt == 0 && ex.Code == Win32.InvalidHandle)
                {
                    CloseHandle();
                }
                catch (WlanException ex) when (ex.Failure == WlanFailure.ServiceUnavailable)
                {
                    CloseHandle(); // the next call opens a fresh handle once the service is back
                    throw;
                }
            }
        }
    }

    private IntPtr EnsureOpen()
    {
        if (_handle != IntPtr.Zero)
            return _handle;

        try
        {
            var open = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle);
            if (open != 0)
                throw Fail(open, "WlanOpenHandle");

            // Notifications are a convenience: without them reads still work, only changes are noticed late.
            _ = WlanRegisterNotification(handle, SourceAcm | SourceMsm, false, _callback, IntPtr.Zero, IntPtr.Zero, out _);

            _handle = handle;
            return handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new WlanException(WlanFailure.NotSupported, 0, "Native Wi-Fi is not available on this Windows.");
        }
    }

    // Callers hold the gate.
    private void CloseHandle()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        try
        {
            _ = WlanRegisterNotification(handle, 0, false, null, IntPtr.Zero, IntPtr.Zero, out _);
            _ = WlanCloseHandle(handle, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing was open without the library.
        }
    }

    private void OnNotification(IntPtr data, IntPtr context)
    {
        // wlanapi thread: do not touch wlanapi here. The subscriber reads again on its own thread.
        Changed?.Invoke();
    }

    // ---- reading and writing ----------------------------------------------------------------------------------------

    private static IReadOnlyList<WlanInterfaceReading> ReadAll(IntPtr handle)
    {
        var list = IntPtr.Zero;
        try
        {
            var enumerate = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (enumerate != 0)
                throw Fail(enumerate, "WlanEnumInterfaces");

            var count = Marshal.ReadInt32(list);
            if (count < 0 || count > MaxInterfaces)
                throw new WlanException(WlanFailure.Other, 0, "Windows reported an invalid adapter count.");

            var readings = new List<WlanInterfaceReading>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = list + 8 + i * InterfaceInfoSize;
                var id = ReadGuid(entry);
                var state = ToState(Marshal.ReadInt32(entry, InterfaceStateOffset));
                readings.Add(new WlanInterfaceReading(state, ReadPhys(handle, id)));
            }

            return readings;
        }
        finally
        {
            if (list != IntPtr.Zero)
                WlanFreeMemory(list);
        }
    }

    // The radio of one adapter, or null when the driver does not report it. A stopped service still fails the whole read.
    private static IReadOnlyList<PhyRadio>? ReadPhys(IntPtr handle, Guid id)
    {
        var data = IntPtr.Zero;
        try
        {
            var query = WlanQueryInterface(handle, ref id, RadioStateOpcode, IntPtr.Zero, out var size, out data, out _);
            if (query != 0)
            {
                if (Classify(query) == WlanFailure.ServiceUnavailable || query == Win32.InvalidHandle)
                    throw Fail(query, "WlanQueryInterface");
                return null;
            }

            if (size < 4 || data == IntPtr.Zero)
                return null;

            var phys = Marshal.ReadInt32(data);
            if (phys < 0 || phys > MaxPhys || 4L + (long)phys * PhyRadioSize > size)
                return null;

            var result = new List<PhyRadio>(phys);
            for (var p = 0; p < phys; p++)
            {
                var software = ToSwitch(Marshal.ReadInt32(data, 8 + p * PhyRadioSize));
                var hardware = ToSwitch(Marshal.ReadInt32(data, 12 + p * PhyRadioSize));
                result.Add(new PhyRadio(software, hardware));
            }

            return result;
        }
        finally
        {
            if (data != IntPtr.Zero)
                WlanFreeMemory(data);
        }
    }

    private static void SetAll(IntPtr handle, bool on)
    {
        var list = IntPtr.Zero;
        var wrote = false;
        try
        {
            var enumerate = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (enumerate != 0)
                throw Fail(enumerate, "WlanEnumInterfaces");

            var count = Marshal.ReadInt32(list);
            if (count < 0 || count > MaxInterfaces)
                throw new WlanException(WlanFailure.Other, 0, "Windows reported an invalid adapter count.");

            for (var i = 0; i < count; i++)
            {
                var id = ReadGuid(list + 8 + i * InterfaceInfoSize);
                var phyCount = PhyIndexes(handle, id, out var indexes);
                for (var p = 0; p < phyCount; p++)
                {
                    SetPhy(handle, id, indexes[p], on);
                    wrote = true;
                }
            }
        }
        finally
        {
            if (list != IntPtr.Zero)
                WlanFreeMemory(list);
        }

        if (!wrote)
            throw new WlanException(WlanFailure.NotSupported, 0, "No adapter reports a radio that can be switched.");
    }

    private static int PhyIndexes(IntPtr handle, Guid id, out int[] indexes)
    {
        indexes = [];
        var data = IntPtr.Zero;
        try
        {
            var query = WlanQueryInterface(handle, ref id, RadioStateOpcode, IntPtr.Zero, out var size, out data, out _);
            if (query != 0 || size < 4 || data == IntPtr.Zero)
                return 0;

            var phys = Marshal.ReadInt32(data);
            if (phys < 0 || phys > MaxPhys || 4L + (long)phys * PhyRadioSize > size)
                return 0;

            indexes = new int[phys];
            for (var p = 0; p < phys; p++)
                indexes[p] = Marshal.ReadInt32(data, 4 + p * PhyRadioSize);
            return phys;
        }
        finally
        {
            if (data != IntPtr.Zero)
                WlanFreeMemory(data);
        }
    }

    // WLAN_PHY_RADIO_STATE: index, software state (the one being set), hardware state (ignored by Windows on a write).
    private static void SetPhy(IntPtr handle, Guid id, int phyIndex, bool on)
    {
        var buffer = Marshal.AllocHGlobal(PhyRadioSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, phyIndex);
            Marshal.WriteInt32(buffer, 4, on ? 1 : 2);
            Marshal.WriteInt32(buffer, 8, 0);
            var set = WlanSetInterface(handle, ref id, RadioStateOpcode, PhyRadioSize, buffer, IntPtr.Zero);
            if (set != 0)
                throw Fail(set, "WlanSetInterface");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Guid ReadGuid(IntPtr address)
    {
        var bytes = new byte[16];
        Marshal.Copy(address, bytes, 0, 16);
        return new Guid(bytes);
    }

    // ---- classification ---------------------------------------------------------------------------------------------

    internal static WlanInterfaceState ToState(int value) => value switch
    {
        0 => WlanInterfaceState.NotReady,
        1 => WlanInterfaceState.Connected,
        2 => WlanInterfaceState.AdHocFormed,
        3 => WlanInterfaceState.Disconnecting,
        4 => WlanInterfaceState.Disconnected,
        5 => WlanInterfaceState.Associating,
        6 => WlanInterfaceState.Discovering,
        7 => WlanInterfaceState.Authenticating,
        _ => WlanInterfaceState.Unknown
    };

    internal static RadioSwitch ToSwitch(int value) => value switch
    {
        1 => RadioSwitch.On,
        2 => RadioSwitch.Off,
        _ => RadioSwitch.Unknown
    };

    internal static WlanFailure Classify(uint win32) => win32 switch
    {
        Win32.AccessDenied => WlanFailure.AccessDenied,
        Win32.ServiceNotActive or Win32.RpcServerUnavailable or Win32.RpcCallFailed => WlanFailure.ServiceUnavailable,
        Win32.NotSupported or Win32.ServiceDoesNotExist or Win32.CallNotImplemented => WlanFailure.NotSupported,
        _ => WlanFailure.Other
    };

    private static WlanException Fail(uint win32, string call) =>
        new(Classify(win32), unchecked((int)win32), call + " failed (Win32 " + win32 + ").");

    private static class Win32
    {
        public const uint AccessDenied = 5;
        public const uint InvalidHandle = 6;
        public const uint NotSupported = 50;
        public const uint CallNotImplemented = 120;
        public const uint ServiceDoesNotExist = 1060;
        public const uint ServiceNotActive = 1062;
        public const uint RpcServerUnavailable = 1722;
        public const uint RpcCallFailed = 1726;
    }

    // ---- native -----------------------------------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NotificationCallback(IntPtr data, IntPtr context);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int opcodeType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(IntPtr handle, ref Guid id, int opcode, uint size, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanRegisterNotification(IntPtr handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, NotificationCallback? callback, IntPtr context, IntPtr reserved, out uint previousSource);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

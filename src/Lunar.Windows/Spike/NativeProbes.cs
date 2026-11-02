using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lunar.Windows.Spike;

internal static class NativeProbes
{
    internal static object Audio()
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!);
            var enumerator = (IMMDeviceEnumerator)enumeratorObject!;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out endpointObject));
            var endpoint = (IAudioEndpointVolume)endpointObject;
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var scalar));
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
            if (!float.IsFinite(scalar) || scalar < 0 || scalar > 1)
                throw new InvalidOperationException("Invalid scalar");
            return new { Status = "Available", ScalarInRange = true, Muted = muted,
                Writes = "NOT_RUN: endpoint read only" };
        }
        finally
        {
            if (endpointObject is not null) Marshal.FinalReleaseComObject(endpointObject);
            if (device is not null) Marshal.FinalReleaseComObject(device);
            if (enumeratorObject is not null) Marshal.FinalReleaseComObject(enumeratorObject);
        }
    }

    internal static object Wifi()
    {
        var open = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (open != 0) return new { Status = "Unavailable", Win32 = open };
        IntPtr list = IntPtr.Zero;
        try
        {
            var enumerate = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (enumerate != 0) return new { Status = "Unavailable", Win32 = enumerate };
            var count = Marshal.ReadInt32(list);
            if (count < 0 || count > 64) throw new InvalidOperationException("Invalid interface count");
            var states = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WifiInterface>(list + 8 + i * Marshal.SizeOf<WifiInterface>());
                IntPtr radio = IntPtr.Zero;
                try
                {
                    var query = WlanQueryInterface(handle, ref info.Id, 4, IntPtr.Zero,
                        out var size, out radio, out _);
                    var phys = new List<object>();
                    if (query == 0 && size >= 4)
                    {
                        var n = Marshal.ReadInt32(radio);
                        if (n < 0 || n > 64 || 4L + n * 12L > size)
                            throw new InvalidOperationException("Invalid radio structure");
                        for (var p = 0; p < n; p++)
                            phys.Add(new { Software = RadioState(Marshal.ReadInt32(radio, 8 + p * 12)),
                                Hardware = RadioState(Marshal.ReadInt32(radio, 12 + p * 12)) });
                    }
                    states.Add(new { InterfaceState = WifiState(info.State), RadioQueryWin32 = query, Phys = phys });
                }
                finally { if (radio != IntPtr.Zero) WlanFreeMemory(radio); }
            }
            return new { Status = count == 0 ? "DeviceAbsent" : "Available", Count = count, Interfaces = states,
                SensitiveQueries = "NOT_RUN: SSID, current_connection and scans omitted",
                Toggle = "NOT_RUN: no radio writes" };
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            var close = WlanCloseHandle(handle, IntPtr.Zero);
            if (close != 0) throw new Win32Exception((int)close);
        }
    }

    internal static object Monitors()
    {
        var awarenessSet = SetProcessDpiAwarenessContext(new IntPtr(-4));
        var monitors = new List<object>();
        var callbackError = 0;
        MonitorCallback callback = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                callbackError = Marshal.GetLastWin32Error();
                return false;
            }
            var hr = GetDpiForMonitor(monitor, 0, out var x, out var y);
            monitors.Add(new { Primary = (info.Flags & 1) != 0,
                Bounds = new[] { info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom },
                WorkArea = new[] { info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom },
                DpiHResult = $"0x{hr:X8}", DpiX = x, DpiY = y });
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(callbackError != 0 ? callbackError : Marshal.GetLastWin32Error());
        return new { Status = "Available", PerMonitorV2Requested = awarenessSet, Count = monitors.Count, Monitors = monitors };
    }

    private static string RadioState(int value) => value switch { 1 => "On", 2 => "Off", _ => "Unknown" };
    private static string WifiState(int value) => value switch
    {
        0 => "NotReady", 1 => "Connected", 2 => "AdHoc", 3 => "Disconnecting",
        4 => "Disconnected", 5 => "Associating", 6 => "Discovering", 7 => "Authenticating", _ => "Unknown"
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WifiInterface
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int opcodeType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

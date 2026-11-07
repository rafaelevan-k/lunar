using System.Runtime.InteropServices;
using Lunar.Windows.Interop;

namespace Lunar.Windows.Display;

/// <summary>An external monitor's brightness in its own units (often 0–100, but not always).</summary>
internal readonly record struct DdcReading(int Minimum, int Current, int Maximum)
{
    /// <summary>The reading on Lunar's 0–100 scale.</summary>
    public int Percent => Maximum <= Minimum ? 0 : (int)Math.Round((Current - Minimum) * 100.0 / (Maximum - Minimum), MidpointRounding.AwayFromZero);

    /// <summary>A 0–100 value in the monitor's units.</summary>
    public int ToRaw(int percent) => Minimum + (int)Math.Round((Maximum - Minimum) * percent / 100.0, MidpointRounding.AwayFromZero);
}

/// <summary>The display-side calls of <see cref="WindowsBrightnessService"/>, so the routing can be tested without real monitors.</summary>
internal interface IDisplayDirectory
{
    /// <summary>The primary display's device name (\\.\DISPLAYn), or null when none can be read.</summary>
    string? PrimaryDisplay();

    /// <summary>
    /// The WMI-style instance prefixes (DISPLAY\model\instance) of the monitors shown on the display; empty when it is not connected.
    /// </summary>
    IReadOnlyList<string> MonitorInstances(string displayId);

    /// <summary>The DDC/CI reading of the display's first monitor; null when the monitor does not answer.</summary>
    DdcReading? ReadDdc(string displayId);

    /// <summary>Sets the display's first monitor through DDC/CI in its own units; false when it refuses.</summary>
    bool WriteDdc(string displayId, int raw);
}

/// <summary>
/// The real <see cref="IDisplayDirectory"/>: display device names from <c>EnumDisplayMonitors</c>, monitor ids from <c>EnumDisplayDevices</c>,
/// and DDC/CI through <c>dxva2.dll</c>. Physical-monitor handles are opened per call and always destroyed. DDC/CI calls can take tens of
/// milliseconds; callers run them off the UI thread.
/// </summary>
internal sealed class Win32DisplayDirectory : IDisplayDirectory
{
    private const uint EddGetDeviceInterfaceName = 0x1;
    private const int DisplayDeviceActive = 0x1;

    public string? PrimaryDisplay()
    {
        string? primary = null;
        foreach (var (_, info) in Monitors())
        {
            if ((info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
                primary = info.Device;
        }

        return primary;
    }

    public IReadOnlyList<string> MonitorInstances(string displayId)
    {
        var instances = new List<string>();
        for (uint index = 0; index < 8; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(displayId, index, ref device, EddGetDeviceInterfaceName))
                break;
            if ((device.StateFlags & DisplayDeviceActive) != 0 && ToInstance(device.DeviceId) is { } instance)
                instances.Add(instance);
        }

        return instances;
    }

    /// <summary>
    /// \\?\DISPLAY#AUO2992#4&amp;2138af8c&amp;0&amp;UID265988#{guid} becomes DISPLAY\AUO2992\4&amp;2138af8c&amp;0&amp;UID265988, the start of
    /// the WMI instance name of the same monitor.
    /// </summary>
    internal static string? ToInstance(string? interfaceName)
    {
        if (string.IsNullOrEmpty(interfaceName))
            return null;
        var parts = interfaceName.Split('#');
        return parts.Length >= 4 && parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? $@"DISPLAY\{parts[1]}\{parts[2]}"
            : null;
    }

    public DdcReading? ReadDdc(string displayId) => WithPhysicalMonitor(displayId, handle =>
        GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) && maximum > minimum
            ? new DdcReading((int)minimum, (int)current, (int)maximum)
            : (DdcReading?)null);

    public bool WriteDdc(string displayId, int raw) =>
        raw >= 0 && WithPhysicalMonitor(displayId, handle => (bool?)SetMonitorBrightness(handle, (uint)raw)) == true;

    private static T? WithPhysicalMonitor<T>(string displayId, Func<IntPtr, T?> use)
        where T : struct
    {
        var monitor = Monitors().FirstOrDefault(m => string.Equals(m.Info.Device, displayId, StringComparison.OrdinalIgnoreCase)).Handle;
        if (monitor == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0 || count > 16)
            return null;

        var physical = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            return null;
        try
        {
            return use(physical[0].Handle);
        }
        finally
        {
            DestroyPhysicalMonitors(count, physical);
        }
    }

    private static List<(IntPtr Handle, NativeMethods.MonitorInfoEx Info)> Monitors()
    {
        var monitors = new List<(IntPtr, NativeMethods.MonitorInfoEx)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
            if (NativeMethods.GetMonitorInfo(handle, ref info))
                monitors.Add((handle, info));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
}

using System.Runtime.InteropServices;

namespace Lunar.Windows.Display;

/// <summary>One built-in panel as WMI reports it: its instance name (DISPLAY\…\…_0) and current brightness.</summary>
internal sealed record WmiPanel(string InstanceName, int Percent);

/// <summary>The built-in panel route of <see cref="WindowsBrightnessService"/>, so the routing can be tested without WMI.</summary>
internal interface IWmiBrightness
{
    /// <summary>Every panel WMI can dim; empty when there is none. Throws <see cref="COMException"/> when WMI fails.</summary>
    IReadOnlyList<WmiPanel> Read();

    /// <summary>Sets one panel. Throws <see cref="COMException"/> when WMI refuses.</summary>
    void Set(string instanceName, int percent);

    /// <summary>Calls <paramref name="changed"/> with the instance name whenever a panel's brightness changes, until disposed.</summary>
    IDisposable Watch(Action<string> changed);
}

/// <summary>
/// WMI <c>root\wmi</c> brightness through COM, with no extra package (ADR-0021): <c>WmiMonitorBrightness</c> to read,
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c> to write, and <c>WmiMonitorBrightnessEvent</c> to learn of outside changes. Calls
/// run on the calling thread, which must be an MTA thread (the thread pool); each call opens and releases its own connection. No elevation.
/// </summary>
internal sealed class WmiBrightness : IWmiBrightness
{
    private const string Namespace = @"root\wmi";

    public IReadOnlyList<WmiPanel> Read()
    {
        using var connection = WmiConnection.Open(Namespace);
        var panels = new List<WmiPanel>();
        foreach (var item in connection.Query("SELECT InstanceName, CurrentBrightness, Active FROM WmiMonitorBrightness"))
        {
            using (item)
            {
                if (item.Get("Active") is false)
                    continue;
                if (item.Get("InstanceName") is string name && item.Get("CurrentBrightness") is { } value)
                    panels.Add(new WmiPanel(name, Math.Clamp(Convert.ToInt32(value, global::System.Globalization.CultureInfo.InvariantCulture), 0, 100)));
            }
        }

        return panels;
    }

    public void Set(string instanceName, int percent)
    {
        using var connection = WmiConnection.Open(Namespace);
        string? path = null;
        // SELECT * on purpose: an object from a narrower SELECT has no __PATH, which the method call needs.
        foreach (var item in connection.Query("SELECT * FROM WmiMonitorBrightnessMethods"))
        {
            using (item)
            {
                if (path is null && item.Get("InstanceName") is string name && string.Equals(name, instanceName, StringComparison.OrdinalIgnoreCase))
                    path = item.Get("__PATH") as string;
            }
        }

        if (path is null)
            throw new COMException("The panel is not offered by WMI.", WmiConnection.WbemNotFound);
        connection.SetBrightness(path, percent);
    }

    public IDisposable Watch(Action<string> changed) => new Watcher(changed);

    /// <summary>A background MTA thread that waits for brightness events with a short timeout, so it can stop promptly.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly Action<string> _changed;
        private readonly Thread _thread;
        private volatile bool _stopping;

        public Watcher(Action<string> changed)
        {
            _changed = changed;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lunar brightness events" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private void Run()
        {
            try
            {
                using var connection = WmiConnection.Open(Namespace);
                using var events = connection.Notifications("SELECT * FROM WmiMonitorBrightnessEvent");
                while (!_stopping)
                {
                    var item = events.Next(500);
                    if (item is null)
                        continue;
                    using (item)
                    {
                        if (item.Get("InstanceName") is string name)
                            _changed(name);
                    }
                }
            }
            catch (COMException)
            {
                // No brightness events here (no built-in panel, or WMI refused): there is nothing to watch.
            }
        }
    }
}

/// <summary>A connection to one WMI namespace and the few calls Lunar needs. Every COM object is released once.</summary>
internal sealed class WmiConnection : IDisposable
{
    internal const int WbemNotFound = unchecked((int)0x80041002);
    private const int WbemFlagReturnImmediately = 0x10;
    private const int WbemFlagForwardOnly = 0x20;
    private const int WbemInfinite = -1;
    private const int WbemTimedOut = 0x40004;

    private static readonly Guid LocatorClass = new("4590f811-1d3a-11d0-891f-00aa004b2e24");

    private IWbemServices? _services;

    private WmiConnection(IWbemServices services) => _services = services;

    public static WmiConnection Open(string ns)
    {
        IWbemLocator? locator = null;
        try
        {
            locator = (IWbemLocator)Activator.CreateInstance(Type.GetTypeFromCLSID(LocatorClass, throwOnError: true)!)!;
            Check(locator.ConnectServer(ns, null, null, null, 0, null, IntPtr.Zero, out var services));
            SetSecurity(services, typeof(IWbemServices));
            return new WmiConnection(services);
        }
        finally
        {
            if (locator is not null)
                Marshal.ReleaseComObject(locator);
        }
    }

    public IEnumerable<WmiObject> Query(string wql)
    {
        Check(Services.ExecQuery("WQL", wql, WbemFlagReturnImmediately | WbemFlagForwardOnly, IntPtr.Zero, out var enumerator));
        SetSecurity(enumerator, typeof(IEnumWbemClassObject));
        try
        {
            var buffer = new IWbemClassObject[1];
            while (true)
            {
                Check(enumerator.Next(WbemInfinite, 1, buffer, out var returned));
                if (returned == 0)
                    yield break;
                yield return new WmiObject(buffer[0]);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public WmiEvents Notifications(string wql)
    {
        Check(Services.ExecNotificationQuery("WQL", wql, WbemFlagReturnImmediately | WbemFlagForwardOnly, IntPtr.Zero, out var enumerator));
        SetSecurity(enumerator, typeof(IEnumWbemClassObject));
        return new WmiEvents(enumerator);
    }

    public void SetBrightness(string instancePath, int percent)
    {
        IWbemClassObject? type = null, signature = null, outSignature = null, input = null, output = null;
        try
        {
            Check(Services.GetObject("WmiMonitorBrightnessMethods", 0, IntPtr.Zero, out type, IntPtr.Zero));
            Check(type.GetMethod("WmiSetBrightness", 0, out signature, out outSignature));
            Check(signature.SpawnInstance(0, out input));
            object timeout = 0;
            object brightness = (byte)percent;
            Check(input.Put("Timeout", 0, ref timeout, 0));
            Check(input.Put("Brightness", 0, ref brightness, 0));
            Check(Services.ExecMethod(instancePath, "WmiSetBrightness", 0, IntPtr.Zero, input, out output, IntPtr.Zero));
        }
        finally
        {
            foreach (var item in new object?[] { output, input, outSignature, signature, type })
            {
                if (item is not null)
                    Marshal.ReleaseComObject(item);
            }
        }
    }

    public void Dispose()
    {
        if (_services is not null)
            Marshal.ReleaseComObject(_services);
        _services = null;
    }

    private IWbemServices Services => _services ?? throw new ObjectDisposedException(nameof(WmiConnection));

    internal static void Check(int hr)
    {
        if (hr < 0)
            throw new COMException("WMI call failed.", hr);
    }

    /// <summary>
    /// Calls through a WMI proxy run as the user (impersonate), which WMI requires. The setting belongs to one interface of the proxy, so it
    /// is applied to the interface the calls go through (setting it on IUnknown alone leaves the calls refused with access denied).
    /// </summary>
    private static void SetSecurity(object proxy, Type face)
    {
        var pointer = Marshal.GetComInterfaceForObject(proxy, face);
        try
        {
            _ = CoSetProxyBlanket(pointer, 10 /* WINNT */, 0, IntPtr.Zero, 3 /* CALL */, 3 /* IMPERSONATE */, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal sealed class WmiEvents(IEnumWbemClassObject enumerator) : IDisposable
    {
        private IEnumWbemClassObject? _enumerator = enumerator;

        /// <summary>The next event, or null when none arrived within <paramref name="timeoutMs"/>.</summary>
        public WmiObject? Next(int timeoutMs)
        {
            var buffer = new IWbemClassObject[1];
            var hr = (_enumerator ?? throw new ObjectDisposedException(nameof(WmiEvents))).Next(timeoutMs, 1, buffer, out var returned);
            if (hr == WbemTimedOut || returned == 0)
                return null;
            Check(hr);
            return new WmiObject(buffer[0]);
        }

        public void Dispose()
        {
            if (_enumerator is not null)
                Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket(IntPtr proxy, uint authnService, uint authzService, IntPtr serverPrincipalName,
        uint authnLevel, uint impersonationLevel, IntPtr authInfo, uint capabilities);
}

/// <summary>One WMI object; disposing releases it.</summary>
internal sealed class WmiObject(IWbemClassObject item) : IDisposable
{
    private IWbemClassObject? _item = item;

    public object? Get(string property)
    {
        var hr = (_item ?? throw new ObjectDisposedException(nameof(WmiObject))).Get(property, 0, out var value, out _, out _);
        return hr < 0 || value is DBNull ? null : value;
    }

    public void Dispose()
    {
        if (_item is not null)
            Marshal.ReleaseComObject(_item);
        _item = null;
    }
}

// ---- the WMI COM interfaces, in vtable order; members Lunar never calls take IntPtr so nothing is marshaled for them ----

[ComImport, Guid("dc12a687-737f-11cf-884d-00aa004b2e24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemLocator
{
    [PreserveSig]
    int ConnectServer([MarshalAs(UnmanagedType.BStr)] string networkResource, [MarshalAs(UnmanagedType.BStr)] string? user,
        [MarshalAs(UnmanagedType.BStr)] string? password, [MarshalAs(UnmanagedType.BStr)] string? locale, int securityFlags,
        [MarshalAs(UnmanagedType.BStr)] string? authority, IntPtr context, out IWbemServices services);
}

[ComImport, Guid("9556dc99-828c-11cf-a37e-00aa003240c7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemServices
{
    [PreserveSig] int OpenNamespace(IntPtr a, int b, IntPtr c, IntPtr d, IntPtr e);
    [PreserveSig] int CancelAsyncCall(IntPtr a);
    [PreserveSig] int QueryObjectSink(int a, IntPtr b);

    [PreserveSig]
    int GetObject([MarshalAs(UnmanagedType.BStr)] string objectPath, int flags, IntPtr context, out IWbemClassObject item, IntPtr callResult);

    [PreserveSig] int GetObjectAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int PutClass(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int PutClassAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int DeleteClass(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int DeleteClassAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int CreateClassEnum(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int CreateClassEnumAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int PutInstance(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int PutInstanceAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int DeleteInstance(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int DeleteInstanceAsync(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int CreateInstanceEnum(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int CreateInstanceEnumAsync(IntPtr a, int b, IntPtr c, IntPtr d);

    [PreserveSig]
    int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query, int flags, IntPtr context,
        out IEnumWbemClassObject enumerator);

    [PreserveSig] int ExecQueryAsync(IntPtr a, IntPtr b, int c, IntPtr d, IntPtr e);

    [PreserveSig]
    int ExecNotificationQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query, int flags,
        IntPtr context, out IEnumWbemClassObject enumerator);

    [PreserveSig] int ExecNotificationQueryAsync(IntPtr a, IntPtr b, int c, IntPtr d, IntPtr e);

    [PreserveSig]
    int ExecMethod([MarshalAs(UnmanagedType.BStr)] string objectPath, [MarshalAs(UnmanagedType.BStr)] string methodName, int flags, IntPtr context,
        IWbemClassObject inParameters, out IWbemClassObject outParameters, IntPtr callResult);
}

[ComImport, Guid("027947e1-d731-11ce-a357-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumWbemClassObject
{
    [PreserveSig] int Reset();

    [PreserveSig]
    int Next(int timeout, uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IWbemClassObject[] objects, out uint returned);
}

[ComImport, Guid("dc12a681-737f-11cf-884d-00aa004b2e24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemClassObject
{
    [PreserveSig] int GetQualifierSet(IntPtr a);

    [PreserveSig]
    int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, [MarshalAs(UnmanagedType.Struct)] out object? value, out int type, out int flavor);

    [PreserveSig]
    int Put([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, [In, MarshalAs(UnmanagedType.Struct)] ref object value, int type);

    [PreserveSig] int Delete(IntPtr a);
    [PreserveSig] int GetNames(IntPtr a, int b, IntPtr c, IntPtr d);
    [PreserveSig] int BeginEnumeration(int a);
    [PreserveSig] int Next(int a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
    [PreserveSig] int EndEnumeration();
    [PreserveSig] int GetPropertyQualifierSet(IntPtr a, IntPtr b);
    [PreserveSig] int Clone(IntPtr a);
    [PreserveSig] int GetObjectText(int a, IntPtr b);
    [PreserveSig] int SpawnDerivedClass(int a, IntPtr b);

    [PreserveSig]
    int SpawnInstance(int flags, out IWbemClassObject instance);

    [PreserveSig] int CompareTo(int a, IntPtr b);
    [PreserveSig] int GetPropertyOrigin(IntPtr a, IntPtr b);
    [PreserveSig] int InheritsFrom(IntPtr a);

    [PreserveSig]
    int GetMethod([MarshalAs(UnmanagedType.LPWStr)] string name, int flags, out IWbemClassObject inSignature, out IWbemClassObject outSignature);
}

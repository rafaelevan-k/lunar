using System.Runtime.InteropServices;

namespace Lunar.Windows.Audio;

/// <summary>
/// The real Core Audio (MMDevice and EndpointVolume) implementation of <see cref="IAudioBackend"/> (ADR-0002). All COM
/// objects are released deterministically when the endpoint or backend is disposed, one <see cref="Marshal.ReleaseComObject"/>
/// per acquisition (never FinalRelease: .NET hands every user of the same native object the same wrapper, so releasing it
/// completely would break other services that share it, which a test with four services found), and the notification callbacks are unregistered first, so no native callback can reach a disposed object.
/// Callbacks arrive on arbitrary COM threads and only copy their data into events; they never call back into COM.
/// </summary>
internal sealed class ComAudioBackend : IAudioBackend
{
    private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private readonly object _gate = new();
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private bool _disposed;

    public ComAudioBackend()
    {
        object? created = null;
        try
        {
            created = Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClass, throwOnError: true)!);
            _enumerator = (IMMDeviceEnumerator)created!;
            _client = new NotificationClient(this);
            Check(_enumerator.RegisterEndpointNotificationCallback(_client));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or PlatformNotSupportedException)
        {
            ReleaseAll(created);
            throw ToException(ex);
        }
    }

    public event Action? DefaultDeviceChanged;

    public IAudioEndpoint? OpenDefault()
    {
        IMMDeviceEnumerator enumerator;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            enumerator = _enumerator!;
        }

        IMMDevice? device = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out device);
            if (hr == HResults.NotFound)
                return null; // no output device at all
            Check(hr);
            return ComAudioEndpoint.Open(device!);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw ToException(ex);
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // Unregister first: after this returns no more device callbacks start, then the objects can be released.
        try
        {
            if (_enumerator is not null && _client is not null)
                _ = _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch (COMException)
        {
            // The enumerator is going away anyway.
        }

        ReleaseAll(_enumerator);
        _enumerator = null;
        _client = null;
    }

    internal static AudioException ToException(Exception ex)
    {
        var hr = ex is COMException com ? com.HResult : ex.HResult;
        return new AudioException(Classify(hr), hr, "Core Audio call failed (0x" + hr.ToString("X8") + ").");
    }

    internal static AudioFailure Classify(int hr) => hr switch
    {
        HResults.DeviceInvalidated or HResults.NotFound or HResults.ServiceNotRunning or HResults.DeviceNotConnected or HResults.ElementNotFound => AudioFailure.DeviceGone,
        HResults.AccessDenied => AudioFailure.AccessDenied,
        _ => AudioFailure.Other
    };

    internal static void Check(int hr)
    {
        if (hr < 0)
            throw new AudioException(Classify(hr), hr, "Core Audio call failed (0x" + hr.ToString("X8") + ").");
    }

    private static void ReleaseAll(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    private void RaiseDefaultChanged()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        try
        {
            DefaultDeviceChanged?.Invoke();
        }
        catch (Exception)
        {
            // A callback runs on a COM thread: nothing may escape into native code.
        }
    }

    private const int EDataFlowRender = 0;
    private const int ERoleConsole = 0;

    /// <summary>Only the default-device change of the render/console role matters; every other callback is ignored.</summary>
    private sealed class NotificationClient(ComAudioBackend owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState)
        {
            owner.RaiseDefaultChanged(); // the default device may have been disabled or enabled
            return 0;
        }

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId)
        {
            owner.RaiseDefaultChanged();
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            if (flow == EDataFlowRender && role == ERoleConsole)
                owner.RaiseDefaultChanged();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}

/// <summary>The master volume of one render endpoint, with its change callback registered.</summary>
internal sealed class ComAudioEndpoint : IAudioEndpoint
{
    private static readonly Guid AudioEndpointVolumeInterface = typeof(IAudioEndpointVolume).GUID;
    private readonly Guid _eventContext = Guid.NewGuid();
    private readonly object _gate = new();
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _callback;
    private bool _disposed;

    private ComAudioEndpoint()
    {
    }

    public event Action<VolumeReading>? Changed;

    internal static ComAudioEndpoint Open(IMMDevice device)
    {
        var endpoint = new ComAudioEndpoint();
        object? activated = null;
        try
        {
            var iid = AudioEndpointVolumeInterface;
            ComAudioBackend.Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated));
            endpoint._volume = (IAudioEndpointVolume)activated;
            endpoint._callback = new VolumeCallback(endpoint);
            ComAudioBackend.Check(endpoint._volume.RegisterControlChangeNotify(endpoint._callback));
            return endpoint;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            endpoint.Dispose();
            throw ComAudioBackend.ToException(ex);
        }
        catch (AudioException)
        {
            endpoint.Dispose();
            throw;
        }
    }

    public VolumeReading Read()
    {
        var volume = Volume();
        try
        {
            ComAudioBackend.Check(volume.GetMasterVolumeLevelScalar(out var scalar));
            ComAudioBackend.Check(volume.GetMute(out var muted));
            return new VolumeReading(Math.Clamp(float.IsFinite(scalar) ? scalar : 0f, 0f, 1f), muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw ComAudioBackend.ToException(ex);
        }
    }

    public void SetVolume(float scalar)
    {
        var volume = Volume();
        var context = _eventContext;
        try
        {
            ComAudioBackend.Check(volume.SetMasterVolumeLevelScalar(scalar, ref context));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw ComAudioBackend.ToException(ex);
        }
    }

    public void SetMute(bool muted)
    {
        var volume = Volume();
        var context = _eventContext;
        try
        {
            ComAudioBackend.Check(volume.SetMute(muted, ref context));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw ComAudioBackend.ToException(ex);
        }
    }

    public void Dispose()
    {
        IAudioEndpointVolume? volume;
        VolumeCallback? callback;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            volume = _volume;
            callback = _callback;
            _volume = null;
            _callback = null;
        }

        try
        {
            if (volume is not null && callback is not null)
                _ = volume.UnregisterControlChangeNotify(callback);
        }
        catch (COMException)
        {
            // The device may already be gone; releasing below is still correct.
        }

        if (volume is not null && Marshal.IsComObject(volume))
            Marshal.ReleaseComObject(volume);
    }

    private IAudioEndpointVolume Volume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _volume!;
        }
    }

    private void RaiseChanged(VolumeReading reading)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        try
        {
            Changed?.Invoke(reading);
        }
        catch (Exception)
        {
            // Runs on a COM thread: nothing may escape into native code.
        }
    }

    private const uint ClsCtxAll = 23;

    private sealed class VolumeCallback(ComAudioEndpoint owner) : IAudioEndpointVolumeCallback
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: Guid context (16), BOOL muted (4), float master volume (4), uint channels (4), ...
        public int OnNotify(IntPtr notifyData)
        {
            if (notifyData == IntPtr.Zero)
                return 0;

            try
            {
                var muted = Marshal.ReadInt32(notifyData, 16) != 0;
                var volume = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(notifyData, 20));
                owner.RaiseChanged(new VolumeReading(Math.Clamp(float.IsFinite(volume) ? volume : 0f, 0f, 1f), muted));
            }
            catch (Exception)
            {
                // Never let a managed exception cross into the audio engine.
            }

            return 0;
        }
    }
}

internal static class HResults
{
    internal const int NotFound = unchecked((int)0x80070490); // E_NOTFOUND
    internal const int ElementNotFound = unchecked((int)0x80070490);
    internal const int AccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED
    internal const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
    internal const int ServiceNotRunning = unchecked((int)0x88890010); // AUDCLNT_E_SERVICE_NOT_RUNNING
    internal const int DeviceNotConnected = unchecked((int)0x8007048F); // ERROR_DEVICE_NOT_CONNECTED
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
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

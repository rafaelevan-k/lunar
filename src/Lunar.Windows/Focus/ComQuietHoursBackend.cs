using System.Runtime.InteropServices;

namespace Lunar.Windows.Focus;

/// <summary>
/// The undocumented quiet-hours service of the Windows shell (ADR-0023, proved on build 26300), the only place in Lunar that calls it. The announced profile
/// is read from a separate undocumented state that Windows itself publishes; writing that state has no effect, which is why it is only ever read.
/// </summary>
internal sealed class ComQuietHoursBackend : IQuietHoursBackend
{
    private const uint LocalServer = 4;
    private const ulong AnnouncedProfileState = 0x0D83063EA3BF1C75;
    private static readonly Guid ClassId = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");
    private static readonly Guid InterfaceId = new("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F");

    public string GetProfile() => Use(settings =>
    {
        Marshal.ThrowExceptionForHR(settings.GetUserSelectedProfile(out var profile));
        return profile;
    });

    public void SetProfile(string profile) => Use(settings =>
    {
        Marshal.ThrowExceptionForHR(settings.SetUserSelectedProfile(profile));
        return profile;
    });

    public int? ReadAnnouncedProfile()
    {
        var name = AnnouncedProfileState;
        var buffer = new byte[sizeof(int)];
        var size = buffer.Length;
        return NtQueryWnfStateData(ref name, 0, 0, out _, buffer, ref size) == 0 && size == sizeof(int) ? BitConverter.ToInt32(buffer) : null;
    }

    private static T Use<T>(Func<IQuietHoursSettings, T> action)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClassId, 0, LocalServer, InterfaceId, out var instance));
        try
        {
            return action((IQuietHoursSettings)instance);
        }
        finally
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    // DllImport rather than LibraryImport: the generator would require AllowUnsafeBlocks for the whole project.
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, byte[] buffer, ref int bufferSize);

    [ComImport]
    [Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig]
        int GetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profile);

        [PreserveSig]
        int SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }
}

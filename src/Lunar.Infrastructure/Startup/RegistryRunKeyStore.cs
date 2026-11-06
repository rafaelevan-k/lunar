using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lunar.Infrastructure.Startup;

/// <summary>HKCU only: no elevation, no machine-wide entry.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryRunKeyStore : IRunKeyStore
{
    internal const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Task Manager and Windows Settings keep the per-entry on/off switch for the Run list. Not a documented API: the value is
    /// binary and its first byte is even (0x02, 0x06) when the entry may start and odd (0x03, 0x07) when the user turned it off.
    /// Anything unreadable is treated as "not turned off", which is how Windows treats a missing value.
    /// </summary>
    internal const string DefaultApprovalSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _subKey;
    private readonly string _approvalSubKey;

    public RegistryRunKeyStore(string subKey = DefaultSubKey, string approvalSubKey = DefaultApprovalSubKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalSubKey);
        _subKey = subKey;
        _approvalSubKey = approvalSubKey;
    }

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool IsTurnedOffByUser(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_approvalSubKey, writable: false);
        return key?.GetValue(name) is byte[] { Length: > 0 } state && (state[0] & 0x01) != 0;
    }

    public void ClearUserChoice(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_approvalSubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

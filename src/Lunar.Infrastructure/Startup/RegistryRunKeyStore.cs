using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lunar.Infrastructure.Startup;

/// <summary>HKCU only: no elevation, no machine-wide entry.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryRunKeyStore : IRunKeyStore
{
    internal const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _subKey;

    public RegistryRunKeyStore(string subKey = DefaultSubKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        _subKey = subKey;
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
}

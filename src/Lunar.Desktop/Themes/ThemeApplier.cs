using System.Windows;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Settings.Models;

namespace Lunar.Desktop.Themes;

/// <summary>
/// Puts the colour palette the user chose on the application (ADR-0016): dark, light, or follow Windows. Windows' High Contrast always
/// wins. Only the palette dictionary (index 0 of the application's merged dictionaries) is replaced; every brush and style follows it
/// because they use dynamic references. A change of the Windows theme or of High Contrast while Lunar runs is followed at once. Use on
/// the UI thread.
/// </summary>
internal sealed class ThemeApplier : IDisposable
{
    internal const string Dark = "Colors.Dark.xaml";
    internal const string Light = "Colors.Light.xaml";
    internal const string HighContrast = "Colors.HighContrast.xaml";

    private readonly ResourceDictionary _resources;
    private readonly ISystemThemeSource _system;
    private readonly Func<bool> _highContrast;
    private readonly Func<string, ResourceDictionary> _load;
    private readonly Action<Action> _post;
    private ThemePreference _preference = ThemePreference.System;
    private string? _current;
    private bool _disposed;

    internal ThemeApplier(
        ResourceDictionary resources,
        ISystemThemeSource system,
        Func<bool> highContrast,
        Func<string, ResourceDictionary> load,
        Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(highContrast);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(post);
        _resources = resources;
        _system = system;
        _highContrast = highContrast;
        _load = load;
        _post = post;
        _system.Changed += OnSystemChanged;
    }

    /// <summary>The file name of the palette in use, or null before the first <see cref="Apply"/>.</summary>
    internal string? Current => _current;

    /// <summary>The palette for this choice and the current Windows state.</summary>
    internal string Choose(ThemePreference preference)
    {
        if (_highContrast())
            return HighContrast;
        return preference switch
        {
            ThemePreference.Dark => Dark,
            ThemePreference.Light => Light,
            _ => _system.PrefersLight ? Light : Dark
        };
    }

    internal void Apply(ThemePreference preference)
    {
        _preference = preference;
        Swap();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _system.Changed -= OnSystemChanged;
    }

    private void Swap()
    {
        if (_disposed)
            return;
        var name = Choose(_preference);
        if (name == _current)
            return;
        _resources.MergedDictionaries[0] = _load(name);
        _current = name;
    }

    // Raised on any thread by the Windows adapter.
    private void OnSystemChanged() => _post(Swap);
}

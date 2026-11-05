using System.Windows;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Themes;

namespace Lunar.Desktop.Features.Settings;

/// <summary>
/// Makes the saved settings take effect (ADR-0016). The same <see cref="Apply"/> runs once at start-up and again after every saved change,
/// so a setting means the same thing in both cases. It also follows Windows' own animation and High Contrast switches. It owns no
/// behaviour: it tells each part (theme, island position, pop-up, motion) what the settings now say. Use on the UI thread.
/// </summary>
internal sealed class SettingsApplier : IDisposable
{
    private readonly SettingsService _settings;
    private readonly Action<Action> _post;
    private readonly Func<bool> _windowsAnimationsOn;
    private readonly ThemeApplier _theme;
    private readonly Action<IslandSettings> _position;
    private readonly Action<IslandSettings, bool> _toast;
    private readonly Action<bool> _interfaceMotionOff;
    private readonly Action<bool> _eyeMotionOff;
    private readonly Action<string?> _defaultFolder;
    private readonly bool _watchWindows;
    private bool _disposed;

    internal SettingsApplier(
        SettingsService settings,
        Action<Action> post,
        Func<bool> windowsAnimationsOn,
        ThemeApplier theme,
        Action<IslandSettings> position,
        Action<IslandSettings, bool> toast,
        Action<bool> interfaceMotionOff,
        Action<bool> eyeMotionOff,
        Action<string?> defaultFolder,
        bool watchWindows = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(windowsAnimationsOn);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentNullException.ThrowIfNull(interfaceMotionOff);
        ArgumentNullException.ThrowIfNull(eyeMotionOff);
        ArgumentNullException.ThrowIfNull(defaultFolder);
        _settings = settings;
        _post = post;
        _windowsAnimationsOn = windowsAnimationsOn;
        _theme = theme;
        _position = position;
        _toast = toast;
        _interfaceMotionOff = interfaceMotionOff;
        _eyeMotionOff = eyeMotionOff;
        _defaultFolder = defaultFolder;
        _watchWindows = watchWindows;

        _settings.Changed += OnChanged;
        if (_watchWindows)
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    internal void Apply(LunarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
            return;

        var animations = _windowsAnimationsOn();
        var interfaceOff = MotionPolicy.InterfaceMotionOff(settings.Appearance, animations);
        _theme.Apply(settings.Appearance.Theme);
        _position(settings.Island);
        _toast(settings.Island, interfaceOff);
        _interfaceMotionOff(interfaceOff);
        _eyeMotionOff(MotionPolicy.EyeMotionOff(settings.Appearance, animations));
        _defaultFolder(settings.Conversion.OutputDirectory);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnChanged;
        if (_watchWindows)
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    // Raised on the thread that finished the save.
    private void OnChanged(object? sender, SettingsChangedEventArgs e) => _post(() => Apply(e.Current));

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            _post(() => Apply(_settings.Current));
    }
}

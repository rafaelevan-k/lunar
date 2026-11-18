using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Application.Translate;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Translate;

/// <summary>
/// Ties screen translation together on the UI side (ADR-0028, FE-41). It owns the global shortcut and follows the Settings switch and shortcut. One
/// trigger runs a translation (the shortcut, the island button or the tray). It asks for consent once, and points to Settings when the key is
/// missing. It shows the overlay or a short notice, and wipes everything when the overlay closes. The same trigger closes an open overlay. Use on
/// the UI thread.
/// </summary>
public sealed class TranslateCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly ScreenTranslationService _service;
    private readonly IGlobalShortcut _shortcut;
    private readonly SettingsService _settings;
    private readonly TranslationSettingsViewModel _settingsCard;
    private readonly Action<Action> _post;
    private readonly Action _openSettings;
    private readonly Func<string?> _uiLanguage;
    private readonly Func<Window, bool?> _askConsent;
    private readonly Action<string, bool> _notice;
    private readonly Action<Window>? _attach;
    private readonly IDiagnosticLog? _log;
    private TranslationOverlayWindow? _overlay;
    private bool _busy;
    private bool _disposed;

    internal TranslateCoordinator(
        ScreenTranslationService service,
        IGlobalShortcut shortcut,
        SettingsService settings,
        TranslationSettingsViewModel settingsCard,
        Action<Action> post,
        Action openSettings,
        Func<string?> uiLanguage,
        Action<string, bool> notice,
        Action<Window>? attach = null,
        Func<Window, bool?>? askConsent = null,
        IDiagnosticLog? log = null)
    {
        _service = service;
        _shortcut = shortcut;
        _settings = settings;
        _settingsCard = settingsCard;
        _post = post;
        _openSettings = openSettings;
        _uiLanguage = uiLanguage;
        _notice = notice;
        _attach = attach;
        _askConsent = askConsent ?? (w => w.ShowDialog());
        _log = log;
        TranslateCommand = new RelayCommand(() => _ = TriggerAsync());
        CancelCommand = new RelayCommand(() => _service.Cancel());
        _service.StateChanged += OnServiceChanged;
        _shortcut.Pressed += OnShortcut;
        _settings.Changed += OnSettingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>True while the screen is being read and translated: the collapsed island shows "Translating…" with Cancel.</summary>
    public bool ShowsInIsland => _busy;

    /// <summary>True while the overlay is open.</summary>
    public bool IsOverlayOpen => _overlay is not null;

    public ICommand TranslateCommand { get; }

    public ICommand CancelCommand { get; }

    internal TranslationOverlayWindow? Overlay => _overlay;

    /// <summary>Registers the shortcut the settings name, or none when translation is off. Call once at start.</summary>
    public Task StartAsync() => ApplyShortcutAsync();

    /// <summary>Runs a translation, or closes the overlay when one is open (the shortcut toggles).</summary>
    public async Task TriggerAsync()
    {
        if (_disposed)
            return;
        if (_overlay is not null)
        {
            _overlay.Close();
            return;
        }

        if (_service.IsBusy)
            return;

        var target = _settings.Current.Translate.Target ?? ScreenTranslationService.DefaultTarget(_uiLanguage());
        var outcome = await _service.TranslateScreenAsync(target, CancellationToken.None).ConfigureAwait(true);
        if (outcome.Translation == TranslationProblem.NotConsented)
        {
            var consent = new TranslateConsentWindow();
            _attach?.Invoke(consent);
            if (_askConsent(consent) != true)
                return;
            await _settings.UpdateAsync(s => s with { Translate = s.Translate with { Consented = true } }, CancellationToken.None).ConfigureAwait(true);
            outcome = await _service.TranslateScreenAsync(target, CancellationToken.None).ConfigureAwait(true);
        }

        Present(outcome);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.StateChanged -= OnServiceChanged;
        _shortcut.Pressed -= OnShortcut;
        _settings.Changed -= OnSettingsChanged;
        _overlay?.Close();
        _shortcut.Unregister();
    }

    private void Present(ScreenTranslationOutcome outcome)
    {
        if (_disposed || outcome.Problem == ScreenProblem.Cancelled)
            return;
        if (!outcome.IsSuccess)
        {
            var needsSettings = outcome.Translation is TranslationProblem.NoKey or TranslationProblem.KeyRefused || outcome.Problem == ScreenProblem.Disabled;
            _notice(TranslateText.Problem(outcome), needsSettings);
            return;
        }

        _overlay = new TranslationOverlayWindow(outcome, language => _ = RetargetAsync(language));
        _attach?.Invoke(_overlay);
        _overlay.Closed += OnOverlayClosed;
        _overlay.Show();
        _overlay.Activate();
        Raise(nameof(IsOverlayOpen));
    }

    private async Task RetargetAsync(TranslationLanguage language)
    {
        var overlay = _overlay;
        if (overlay is null)
            return;
        overlay.SetStatus(Strings.Get("Translate.Working"));
        var outcome = await _service.RetranslateAsync(language, CancellationToken.None).ConfigureAwait(true);
        if (!ReferenceEquals(overlay, _overlay))
            return;
        if (outcome.IsSuccess)
            overlay.Show(outcome);
        else if (outcome.Problem != ScreenProblem.Cancelled)
            overlay.SetStatus(TranslateText.Problem(outcome));
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is TranslationOverlayWindow overlay)
            overlay.Closed -= OnOverlayClosed;
        _overlay = null;
        _service.Close(); // the picture is wiped and the texts forgotten
        Raise(nameof(IsOverlayOpen));
    }

    private void OnServiceChanged(object? sender, EventArgs e) => _post(() =>
    {
        var busy = _service.IsBusy;
        if (_disposed || busy == _busy)
            return;
        _busy = busy;
        Raise(nameof(ShowsInIsland));
    });

    private void OnShortcut() => _post(() => _ = TriggerAsync());

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Translate.Enabled != e.Current.Translate.Enabled || e.Previous.Translate.Shortcut != e.Current.Translate.Shortcut)
            _post(() => _ = ApplyShortcutAsync());
    }

    private async Task ApplyShortcutAsync()
    {
        if (_disposed)
            return;
        var settings = _settings.Current.Translate;
        if (!settings.Enabled || ShortcutRules.Parse(settings.Shortcut) is not { } shortcut)
        {
            _shortcut.Unregister();
            _settingsCard.ShowShortcutState(null);
            return;
        }

        var registered = await _shortcut.RegisterAsync(shortcut, CancellationToken.None).ConfigureAwait(true);
        _settingsCard.ShowShortcutState(registered);
        if (registered.IsFailure)
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "translate.shortcut.unavailable", "Translate", "shortcut", registered.Error!.Code));
    }

    internal void OpenSettings() => _openSettings();

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

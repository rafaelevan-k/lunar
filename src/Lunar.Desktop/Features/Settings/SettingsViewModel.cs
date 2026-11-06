using System.ComponentModel;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Settings;

/// <summary>One choice in a group (a theme, a display): its words, whether it is the saved one, and what choosing it does.</summary>
public sealed record SettingsChoice(string Label, bool IsSelected, ICommand Command);

/// <summary>
/// The Settings window's state (FE-23, ADR-0016). It shows what is saved, never what was clicked: every control asks through a command, the
/// <see cref="SettingsService"/> saves, and the window changes only when the saved value does (so a failed save leaves everything as it was
/// and says so). It follows changes made elsewhere (the Usage switch, a reset). It touches no file, registry or window API.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "SettingsWindow";
    internal const int OffsetStep = 4;

    private readonly SettingsService _settings;
    private readonly IWindowEnvironment _environment;
    private readonly IFolderPicker _folders;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly bool _diagnosticsInUse;
    private readonly CancellationTokenSource _lifetime = new();

    private IReadOnlyList<SettingsChoice> _themes = [];
    private IReadOnlyList<SettingsChoice> _languages = [];
    private readonly string _languageInUse = Strings.Culture.TwoLetterISOLanguageName;
    private IReadOnlyList<SettingsChoice> _monitors = [];
    private string? _problem;
    private bool _noticeDismissed;
    private bool _confirmingReset;
    private bool _disposed;

    /// <param name="diagnosticsInUse">Whether verbose diagnostics are on in this run; the saved choice applies from the next start.</param>
    public SettingsViewModel(
        SettingsService settings,
        IWindowEnvironment environment,
        IFolderPicker folders,
        Action<Action> post,
        bool diagnosticsInUse,
        IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(post);
        _settings = settings;
        _environment = environment;
        _folders = folders;
        _post = post;
        _diagnosticsInUse = diagnosticsInUse;
        _log = log;

        var lifetime = _lifetime.Token;
        ToggleReduceMotionCommand = Save(s => s with { Appearance = s.Appearance with { ReduceMotion = !s.Appearance.ReduceMotion } }, lifetime);
        ToggleEyesCommand = Save(s => s with { Appearance = s.Appearance with { EyeAnimationEnabled = !s.Appearance.EyeAnimationEnabled } }, lifetime);
        ToggleDiagnosticsCommand = Save(s => s with { Privacy = s.Privacy with { DiagnosticsEnabled = !s.Privacy.DiagnosticsEnabled } }, lifetime);
        OffsetUpCommand = Save(
            s => s with { Island = s.Island with { TopOffsetDip = Math.Min(SettingsRules.MaxTopOffsetDip, s.Island.TopOffsetDip + OffsetStep) } },
            lifetime,
            () => TopOffset < SettingsRules.MaxTopOffsetDip);
        OffsetDownCommand = Save(
            s => s with { Island = s.Island with { TopOffsetDip = Math.Max(SettingsRules.MinTopOffsetDip, s.Island.TopOffsetDip - OffsetStep) } },
            lifetime,
            () => TopOffset > SettingsRules.MinTopOffsetDip);
        ChooseFolderCommand = new AsyncRelayCommand(ChooseFolderAsync, () => !IsReadOnly, OnFault, lifetime);
        ClearFolderCommand = Save(s => s with { Conversion = s.Conversion with { OutputDirectory = null } }, lifetime, () => HasOutputFolder);
        AskResetCommand = new RelayCommand(() =>
        {
            _confirmingReset = true;
            Changed();
        }, () => !IsReadOnly);
        CancelResetCommand = new RelayCommand(() =>
        {
            _confirmingReset = false;
            Changed();
        });
        ConfirmResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsReadOnly, OnFault, lifetime);
        DismissNoticeCommand = new RelayCommand(() =>
        {
            _noticeDismissed = true;
            Changed();
        });

        _settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the window shows --------------------------------------------------------------------------------------

    /// <summary>True when the settings file comes from a newer Lunar: everything is shown and nothing can be changed.</summary>
    public bool IsReadOnly => _settings.IsReadOnly;

    public bool IsEditable => !IsReadOnly;

    /// <summary>A repair made at start-up, dismissible; null when nothing was repaired or the user closed it.</summary>
    public string? Notice => _noticeDismissed ? null : SettingsText.Recovery(_settings.Recovery);

    public bool HasNotice => Notice is not null;

    /// <summary>Why the last change did not happen, or null.</summary>
    public string? Problem => _problem;

    public bool HasProblem => _problem is not null;

    public bool ReduceMotion => _settings.Current.Appearance.ReduceMotion;

    public bool EyeAnimationEnabled => _settings.Current.Appearance.EyeAnimationEnabled;

    public bool DiagnosticsEnabled => _settings.Current.Privacy.DiagnosticsEnabled;

    /// <summary>True when the saved diagnostics choice is not what this run is using yet.</summary>
    public bool DiagnosticsNeedsRestart => DiagnosticsEnabled != _diagnosticsInUse;

    public IReadOnlyList<SettingsChoice> Themes => _themes;

    /// <summary>The language choices (ADR-0017): follow Windows, English, Bahasa Indonesia.</summary>
    public IReadOnlyList<SettingsChoice> Languages => _languages;

    /// <summary>True when the saved language is not the one this run is showing (it applies at the next start).</summary>
    public bool LanguageNeedsRestart =>
        Strings.Resolve(_settings.Current.Locale ?? Strings.FromWindows(System.Globalization.CultureInfo.CurrentUICulture)) != _languageInUse;

    public IReadOnlyList<SettingsChoice> Monitors => _monitors;

    public int TopOffset => _settings.Current.Island.TopOffsetDip;

    public string TopOffsetText => SettingsText.Offset(TopOffset);

    public bool HasOutputFolder => _settings.Current.Conversion.OutputDirectory is not null;

    public string OutputFolderText => _settings.Current.Conversion.OutputDirectory ?? SettingsText.FolderDefault;

    public bool IsConfirmingReset => _confirmingReset && !IsReadOnly;

    public bool IsNotConfirmingReset => !IsConfirmingReset;

    public ICommand ToggleReduceMotionCommand { get; }

    public ICommand ToggleEyesCommand { get; }

    public ICommand ToggleDiagnosticsCommand { get; }

    public ICommand OffsetUpCommand { get; }

    public ICommand OffsetDownCommand { get; }

    public ICommand ChooseFolderCommand { get; }

    public ICommand ClearFolderCommand { get; }

    public ICommand AskResetCommand { get; }

    public ICommand CancelResetCommand { get; }

    public ICommand ConfirmResetCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    /// <summary>The displays may have changed (one plugged in, one removed); the list is read again. UI thread only.</summary>
    public void RefreshDisplays()
    {
        if (_disposed)
            return;
        Rebuild();
        Changed();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    private AsyncRelayCommand Save(Func<LunarSettings, LunarSettings> change, CancellationToken lifetime, Func<bool>? canExecute = null) =>
        new(ct => SaveAsync(change, ct), () => !IsReadOnly && (canExecute?.Invoke() ?? true), OnFault, lifetime);

    private async Task SaveAsync(Func<LunarSettings, LunarSettings> change, CancellationToken ct)
    {
        var saved = await _settings.UpdateAsync(change, ct);
        if (saved.IsFailure && !saved.IsCancelled)
            Fail(saved.Error!.Code);
        else
            _problem = null;

        // Rebuilt even after a failure, so a choice the user clicked but that was not saved goes back to the saved one.
        _post(() =>
        {
            if (_disposed)
                return;
            Rebuild();
            Changed();
        });
    }

    private async Task ChooseFolderAsync(CancellationToken ct)
    {
        var path = _folders.PickFolder(); // modal, on the UI thread, writes nothing
        if (path is null)
            return;
        if (!IsLocalAbsolute(path))
        {
            _problem = SettingsText.FolderRejected;
            Changed();
            return;
        }

        await SaveAsync(s => s with { Conversion = s.Conversion with { OutputDirectory = path } }, ct);
    }

    private async Task ResetAsync(CancellationToken ct)
    {
        _confirmingReset = false;
        await SaveAsync(_ => LunarSettings.Defaults, ct);
    }

    private static bool IsLocalAbsolute(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        try
        {
            return System.IO.Path.IsPathFullyQualified(path) && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0 && path.Length <= SettingsRules.MaxPathLength;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void Fail(ErrorCode code)
    {
        Log(DiagnosticLevel.Warning, "settings.save.failed", "Save", code);
        _problem = _settings.IsReadOnly ? SettingsText.ReadOnly : SettingsText.SaveFailed;
    }

    // ---- following the saved settings -------------------------------------------------------------------------------

    // Raised on whatever thread finished the save.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _post(() =>
    {
        if (_disposed)
            return;
        Rebuild();
        Changed();
    });

    private void Rebuild()
    {
        var current = _settings.Current;
        _themes =
        [
            Theme(Strings.Get("Settings.ThemeMatch"), ThemePreference.System, current),
            Theme(Strings.Get("Settings.ThemeDark"), ThemePreference.Dark, current),
            Theme(Strings.Get("Settings.ThemeLight"), ThemePreference.Light, current)
        ];
        _languages =
        [
            Language(Strings.Get("Settings.LanguageMatch"), null, current),
            Language(Strings.Get("Settings.LanguageEn"), "en", current),
            Language(Strings.Get("Settings.LanguageId"), "id", current)
        ];
        _monitors = BuildMonitors(current.Island.PreferredMonitorId);
    }

    private SettingsChoice Language(string label, string? tag, LunarSettings current) =>
        new(label, tag is null ? current.Locale is null : current.Locale is not null && Strings.Resolve(current.Locale) == tag,
            Save(s => s with { Locale = tag }, _lifetime.Token));

    private SettingsChoice Theme(string label, ThemePreference value, LunarSettings current) =>
        new(label, current.Appearance.Theme == value, Save(s => s with { Appearance = s.Appearance with { Theme = value } }, _lifetime.Token));

    private IReadOnlyList<SettingsChoice> BuildMonitors(string? savedId)
    {
        var list = new List<SettingsChoice>();
        var displays = _environment.GetDisplays();
        var known = displays.IsSuccess ? displays.Value : [];
        var savedIsPresent = savedId is not null && known.Any(d => string.Equals(d.Id, savedId, StringComparison.OrdinalIgnoreCase));

        list.Add(Monitor(SettingsText.MainDisplay, null, selected: savedId is null));
        for (var i = 0; i < known.Count; i++)
        {
            var display = known[i];
            list.Add(Monitor(SettingsText.Monitor(i + 1, display), display.Id, selected: savedIsPresent && string.Equals(display.Id, savedId, StringComparison.OrdinalIgnoreCase)));
        }

        // A saved display that is unplugged: the island is on the main one meanwhile, and the choice stays visible and kept.
        if (savedId is not null && !savedIsPresent)
            list.Add(new SettingsChoice(SettingsText.MissingDisplay, true, new RelayCommand(() => { }, () => false)));
        return list;
    }

    private SettingsChoice Monitor(string label, string? id, bool selected) =>
        new(label, selected, Save(s => s with { Island = s.Island with { PreferredMonitorId = id } }, _lifetime.Token));

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every property: the window is small
        foreach (var command in new[] { OffsetUpCommand, OffsetDownCommand, ClearFolderCommand, ConfirmResetCommand, ChooseFolderCommand, ToggleReduceMotionCommand, ToggleEyesCommand, ToggleDiagnosticsCommand })
            ((AsyncRelayCommand)command).NotifyCanExecuteChanged();

        ((RelayCommand)AskResetCommand).NotifyCanExecuteChanged();
    }

    private void OnFault(Exception ex)
    {
        Log(DiagnosticLevel.Error, "settingswindow.fault", "Command", ErrorCode.Unexpected, ex.GetType());
        _problem = SettingsText.SaveFailed;
        _post(Changed);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, ExceptionType: exception));
}

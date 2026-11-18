using System.ComponentModel;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Translate;
using Lunar.Application.Settings;
using Lunar.Application.Translate;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Translate;

/// <summary>A choice in the Settings Translation card.</summary>
public sealed record TranslateChoice(string Label, bool IsSelected, ICommand Command);

/// <summary>
/// The Settings "Translation" card (ADR-0028 §8, FE-41): the switch, the target language, the user's own Azure key (masked, never shown again; Test
/// and Remove), its region, and the shortcut with a message when another app holds it. Use on the UI thread.
/// </summary>
/// <summary>A command that runs a task with the command parameter, one run at a time; a fault is reported, never thrown into WPF.</summary>
public sealed class TaskCommand(Func<object?, Task> execute, Func<bool>? canExecute = null, Action<Exception>? onFault = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        _running = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onFault?.Invoke(ex);
        }
        finally
        {
            _running = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class TranslationSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Where the user creates a free Translator resource and finds its key.</summary>
    public const string SignUpAddress = "https://portal.azure.com/#create/Microsoft.CognitiveServicesTextTranslation";

    /// <summary>Microsoft's privacy statement, which covers the text the service receives.</summary>
    public const string PrivacyAddress = "https://privacy.microsoft.com/privacystatement";

    private readonly SettingsService _settings;
    private readonly ITranslationKeyStore _keys;
    private readonly ITextTranslator _translator;
    private readonly IGlobalShortcut _shortcut;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private string _shortcutText;
    private string _regionText;
    private string? _keyMessage;
    private string? _shortcutMessage;
    private bool _shortcutProblem;
    private bool _disposed;

    public TranslationSettingsViewModel(SettingsService settings, ITranslationKeyStore keys, ITextTranslator translator, IGlobalShortcut shortcut, Action<Action> post, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(shortcut);
        ArgumentNullException.ThrowIfNull(post);
        _settings = settings;
        _keys = keys;
        _translator = translator;
        _shortcut = shortcut;
        _post = post;
        _log = log;
        _shortcutText = settings.Current.Translate.Shortcut;
        _regionText = settings.Current.Translate.Region ?? string.Empty;
        ToggleCommand = new TaskCommand(_ => Save(s => s with { Translate = s.Translate with { Enabled = !s.Translate.Enabled } }), onFault: Fault);
        SaveKeyCommand = new TaskCommand(SaveKeyAsync, onFault: Fault);
        RemoveKeyCommand = new TaskCommand(RemoveKeyAsync, () => HasKey, Fault);
        TestKeyCommand = new TaskCommand(TestKeyAsync, () => HasKey, Fault);
        ApplyShortcutCommand = new TaskCommand(_ => ApplyShortcutAsync(), onFault: Fault);
        ApplyRegionCommand = new TaskCommand(_ => ApplyRegionAsync(), onFault: Fault);
        _settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled => _settings.Current.Translate.Enabled;

    public bool HasKey => _keys.HasKey;

    public string KeyStatus => HasKey ? Strings.Get("Translate.KeySaved") : Strings.Get("Translate.KeyMissing");

    /// <summary>The result of the last key action (saved, removed, tested), in words.</summary>
    public string? KeyMessage
    {
        get => _keyMessage;
        private set => Set(ref _keyMessage, value, nameof(KeyMessage));
    }

    public IReadOnlyList<TranslateChoice> Targets { get; private set; } = [];

    public string ShortcutText
    {
        get => _shortcutText;
        set => Set(ref _shortcutText, value ?? string.Empty, nameof(ShortcutText));
    }

    public string RegionText
    {
        get => _regionText;
        set => Set(ref _regionText, value ?? string.Empty, nameof(RegionText));
    }

    /// <summary>Whether the shortcut works, or why not.</summary>
    public string? ShortcutMessage
    {
        get => _shortcutMessage;
        private set => Set(ref _shortcutMessage, value, nameof(ShortcutMessage));
    }

    public bool ShortcutProblem
    {
        get => _shortcutProblem;
        private set => Set(ref _shortcutProblem, value, nameof(ShortcutProblem));
    }

    public string SignUp => SignUpAddress;

    public string Privacy => PrivacyAddress;

    public ICommand ToggleCommand { get; }

    /// <summary>Takes the key from the masked box (the parameter), saves it encrypted and forgets the text.</summary>
    public ICommand SaveKeyCommand { get; }

    public ICommand RemoveKeyCommand { get; }

    public ICommand TestKeyCommand { get; }

    public ICommand ApplyShortcutCommand { get; }

    public ICommand ApplyRegionCommand { get; }

    /// <summary>The shortcut's registration result, set by the coordinator that owns it.</summary>
    internal void ShowShortcutState(Lunar.Domain.Shared.Result? registered)
    {
        if (registered is null)
        {
            ShortcutMessage = null;
            ShortcutProblem = false;
            return;
        }

        ShortcutProblem = registered.IsFailure;
        ShortcutMessage = registered.IsSuccess
            ? Strings.Format("Translate.ShortcutWorks", _settings.Current.Translate.Shortcut)
            : registered.Error!.Code == Lunar.Domain.Shared.ErrorCode.AccessDenied ? Strings.Get("Translate.ShortcutTaken") : Strings.Get("Translate.ShortcutRefused");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
    }

    private async Task SaveKeyAsync(object? parameter)
    {
        var text = parameter switch
        {
            System.Windows.Controls.PasswordBox box => box.Password,
            string s => s,
            _ => string.Empty
        };
        var saved = await _keys.SaveAsync(text, CancellationToken.None).ConfigureAwait(true);
        if (parameter is System.Windows.Controls.PasswordBox clear)
            clear.Clear();
        KeyMessage = saved.IsSuccess ? Strings.Get("Translate.KeySavedNow") : Strings.Get("Translate.KeyInvalid");
        Refresh();
    }

    private async Task RemoveKeyAsync(object? _)
    {
        var removed = await _keys.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
        KeyMessage = removed.IsSuccess ? Strings.Get("Translate.KeyRemoved") : Strings.Get("Translate.KeyRemoveFailed");
        Refresh();
    }

    private async Task TestKeyAsync(object? _)
    {
        KeyMessage = Strings.Get("Translate.KeyTesting");
        var key = await _keys.ReadAsync(CancellationToken.None).ConfigureAwait(true);
        if (key.IsFailure)
        {
            KeyMessage = Strings.Get("Translate.ProblemNoKey");
            return;
        }

        // A one-word test: the user pressed Test, which is consent for this word.
        var reply = await _translator.TranslateAsync(["Hello"], TranslationLanguage.Indonesian,
            new TranslatorCredential(key.Value, _settings.Current.Translate.Region), CancellationToken.None).ConfigureAwait(true);
        KeyMessage = reply.IsSuccess && reply.Value.IsSuccess ? Strings.Get("Translate.KeyWorks") : TranslateText.Problem(reply.IsSuccess ? reply.Value.Problem : TranslationProblem.ProviderError);
    }

    private async Task ApplyShortcutAsync()
    {
        var parsed = ShortcutRules.Parse(ShortcutText);
        if (parsed is null)
        {
            ShortcutProblem = true;
            ShortcutMessage = Strings.Get("Translate.ShortcutInvalid");
            return;
        }

        ShortcutText = parsed.ToString();
        await Save(s => s with { Translate = s.Translate with { Shortcut = parsed.ToString() } }).ConfigureAwait(true);
    }

    private async Task ApplyRegionAsync()
    {
        var region = TranslationKeyRules.ValidateRegion(RegionText);
        if (region.IsFailure)
        {
            KeyMessage = Strings.Get("Translate.RegionInvalid");
            return;
        }

        RegionText = region.Value ?? string.Empty;
        await Save(s => s with { Translate = s.Translate with { Region = region.Value } }).ConfigureAwait(true);
        KeyMessage = Strings.Get("Translate.RegionSaved");
    }

    private Task Save(Func<Lunar.Application.Settings.Models.LunarSettings, Lunar.Application.Settings.Models.LunarSettings> change) =>
        _settings.UpdateAsync(change, CancellationToken.None);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _post(() =>
    {
        if (!_disposed)
            Refresh();
    });

    private void Refresh()
    {
        Rebuild();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var command in new[] { RemoveKeyCommand, TestKeyCommand })
            ((TaskCommand)command).NotifyCanExecuteChanged();
    }

    private void Rebuild()
    {
        var target = _settings.Current.Translate.Target;
        Targets =
        [
            Choice(Strings.Get("Translate.TargetAutomatic"), null, target),
            Choice(Strings.Get("Translate.English"), TranslationLanguage.English, target),
            Choice(Strings.Get("Translate.Indonesian"), TranslationLanguage.Indonesian, target),
            Choice(Strings.Get("Translate.Japanese"), TranslationLanguage.Japanese, target)
        ];
    }

    private TranslateChoice Choice(string label, TranslationLanguage? value, TranslationLanguage? current) =>
        new(label, value == current, new TaskCommand(_ => Save(s => s with { Translate = s.Translate with { Target = value } }), onFault: Fault));

    private void Fault(Exception ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "translate.settings.fault", "Translate", "settings", Lunar.Domain.Shared.ErrorCode.Unexpected, ExceptionType: ex.GetType()));

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

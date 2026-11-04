using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Conversion;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// What the Converter page shows and can do for choosing files and a format (FE-20): the list of files, why a file is refused,
/// and the output formats that fit the list. It reads the Application layer only (<see cref="IConversionPlanner"/>,
/// <see cref="IRendererAvailability"/>) and asks an <see cref="IFilePicker"/> for the file dialog; it opens no window, reads no file
/// itself and calls no Windows API.
///
/// How it behaves:
/// - Files come from the picker or from a drop (<see cref="AddFiles"/>). A file already listed (same path, any letter case) is not
///   added twice and the page says so; past the batch limit the rest are not added and the page says so.
/// - Every change to the list or the format is checked by the planner, which only reads (it signs, sizes and shapes each file,
///   checks the route and decides the result name). Its answer for each file is shown on that file in plain words, so a refused file
///   says why and what to do. The check runs off the UI thread and a newer change replaces an older, unfinished check.
/// - The formats offered are the five formats of the matrix, each marked available only when every recognised file in the list can
///   become it (and, for Word files, only when a layout engine is installed). An unavailable format says why. The format is picked
///   automatically when there is only one sensible choice (the first available format that is not a plain copy) and stays the user's
///   choice while it remains available.
/// - Nothing here writes a log line with a path or a name; events carry no file information. The full path appears only in a tooltip.
///
/// Whether the island may close meanwhile is reported through <see cref="IsDialogOpen"/> and <see cref="IsDragActive"/>.
/// Use on the UI thread; the check's result is marshalled through <c>post</c>.
/// </summary>
public sealed class ConverterPageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "ConverterPage";

    private readonly IConversionPlanner _planner;
    private readonly IRendererAvailability _renderer;
    private readonly IFilePicker _picker;
    private readonly IFolderPicker _folders;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<ConverterItem> _items = [];
    private readonly ObservableCollection<TargetOption> _targets = [];
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _check;
    private Task _checking = Task.CompletedTask;
    private FileFormat? _userTarget;
    private FileFormat? _selected;
    private ConversionPlan? _plan;
    private bool _disposed;
    private bool _dialogOpen;
    private bool _dragActive;
    private bool _isChecking;
    private bool _confirmingOverwrite;
    private string? _notice;
    private string? _errorMessage;

    public ConverterPageViewModel(IConversionPlanner planner, IRendererAvailability renderer, IFilePicker picker, IFolderPicker folders, ConverterQueueViewModel queue, Action<Action> post, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(post);
        _planner = planner;
        _renderer = renderer;
        _picker = picker;
        _folders = folders;
        Options = new ConverterOptionsViewModel(OnOptionsChanged);
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !_disposed && !_dialogOpen);
        Queue = queue;
        StartCommand = new AsyncRelayCommand(ct => StartAsync(confirmed: false, ct), () => CanStart && !_confirmingOverwrite, OnStartFault, _lifetime.Token);
        ConfirmOverwriteCommand = new AsyncRelayCommand(ct => StartAsync(confirmed: true, ct), () => _confirmingOverwrite && CanStart, OnStartFault, _lifetime.Token);
        CancelOverwriteCommand = new RelayCommand(() => IsConfirmingOverwrite = false, () => _confirmingOverwrite);
        _post = post;
        _log = log;
        Items = new ReadOnlyObservableCollection<ConverterItem>(_items);
        Targets = new ReadOnlyObservableCollection<TargetOption>(_targets);
        AddFilesCommand = new RelayCommand(PickFiles, () => !_disposed && !_dialogOpen);
        ClearCommand = new RelayCommand(Clear, () => !_disposed && _items.Count > 0);
        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        RebuildTargets([], rendererAvailable: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what the page shows -----------------------------------------------------------------------------------------

    public ReadOnlyObservableCollection<ConverterItem> Items { get; }

    public ReadOnlyObservableCollection<TargetOption> Targets { get; }

    public bool IsEmpty => _items.Count == 0;

    public bool HasItems => _items.Count > 0;

    /// <summary>The planner's latest answer for the whole list (what will run, be skipped or be refused); null before the first check or while the list is empty.</summary>
    public ConversionPlan? Plan => _plan;

    /// <summary>The format that will be used, or null when no listed file can become any format.</summary>
    public FileFormat? SelectedFormat => _selected;

    /// <summary>The folder the results go to for now: the folder of the first listed file. Choosing another is part of the options (FE-21).</summary>
    public string OutputFolder { get; private set; } = string.Empty;

    public string SummaryText { get; private set; } = string.Empty;

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
                Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
                ((RelayCommand)DismissErrorCommand).NotifyCanExecuteChanged();
        }
    }

    /// <summary>True while the file dialog is open; the island must not close then.</summary>
    public bool IsDialogOpen
    {
        get => _dialogOpen;
        private set
        {
            if (Set(ref _dialogOpen, value))
            {
                ((RelayCommand)AddFilesCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ChooseFolderCommand).NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True while files are being dragged over the page; the island must not close then.</summary>
    public bool IsDragActive
    {
        get => _dragActive;
        private set => Set(ref _dragActive, value);
    }

    /// <summary>The choices that shape the conversion and the notices about the chosen route (FE-21).</summary>
    public ConverterOptionsViewModel Options { get; }

    /// <summary>True from a change of the list, format or results folder until the planner's answer for it has been applied.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (Set(ref _isChecking, value))
                RaiseStart();
        }
    }

    /// <summary>
    /// True only when there is something to convert and nothing blocks it: a format is chosen that fits the files, at least one file passed
    /// the check, the check is finished, and every shown choice is valid. A format that is not available can never be chosen, so a route
    /// that is disabled cannot start. The Convert button of FE-22 binds to this.
    /// </summary>
    public bool CanStart => !_disposed && !_isChecking && _selected is not null && _plan is { IsEmpty: false } && Options.IsValid
        && _items.Any(i => i.Status == ConverterItemStatus.Ready);

    /// <summary>Why <see cref="CanStart"/> is false, in words for the Convert button; empty when it is true.</summary>
    public string StartHint
    {
        get
        {
            if (CanStart)
                return string.Empty;
            if (_items.Count == 0)
                return "Add files to convert.";
            if (_isChecking)
                return "Checking the files...";
            if (_selected is null)
                return "No format fits every file in the list.";
            if (!Options.IsValid)
                return "Fix the list of pages first.";
            return "None of the files can be converted yet.";
        }
    }

    /// <summary>What FE-22 hands to the conversion: the files that passed the check, the chosen format and folder, the name policy. Null when <see cref="CanStart"/> is false.</summary>
    public ConversionRequest? CurrentRequest => CanStart && _selected is { } format
        ? new ConversionRequest([.. _items.Where(i => i.Status == ConverterItemStatus.Ready).Select(i => i.FullPath)], format, OutputFolder, Options.Collision)
        : null;

    /// <summary>The conversion choices as set now, to go with <see cref="CurrentRequest"/>.</summary>
    public ConversionChoices CurrentChoices => Options.BuildChoices();

    /// <summary>The conversions that were started: their state, progress, cancel, retry and show in folder (FE-22).</summary>
    public ConverterQueueViewModel Queue { get; }

    /// <summary>Starts the conversion of the files that passed the check. Replacing existing results asks first (<see cref="IsConfirmingOverwrite"/>).</summary>
    public ICommand StartCommand { get; }

    public ICommand ConfirmOverwriteCommand { get; }

    public ICommand CancelOverwriteCommand { get; }

    /// <summary>True while the page waits for the user to confirm that existing results may be replaced; the island must not close then.</summary>
    public bool IsConfirmingOverwrite
    {
        get => _confirmingOverwrite;
        private set
        {
            if (Set(ref _confirmingOverwrite, value))
            {
                ((AsyncRelayCommand)StartCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)ConfirmOverwriteCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelOverwriteCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public ICommand ChooseFolderCommand { get; }

    public ICommand AddFilesCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand DismissErrorCommand { get; }

    // ---- the view tells the page what happened -----------------------------------------------------------------------

    /// <summary>Files were dragged over the page (true) or left it or were dropped (false).</summary>
    public void SetDragActive(bool active) => IsDragActive = active && !_disposed;

    /// <summary>
    /// Adds files by full path, from a drop or the picker. Returns how many were added. Blank entries are ignored; a path already in
    /// the list is not added twice; past the batch limit the rest are not added. The page then checks the list.
    /// </summary>
    public int AddFiles(IEnumerable<string?>? paths)
    {
        if (_disposed || paths is null)
            return 0;

        ErrorMessage = null;
        var added = 0;
        var duplicates = 0;
        var overflow = 0;
        var limit = new ConversionLimits().MaxFilesPerRequest;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!_keys.Add(KeyOf(path)))
            {
                duplicates++;
                continue;
            }

            if (_items.Count >= limit)
            {
                _keys.Remove(KeyOf(path));
                overflow++;
                continue;
            }

            _items.Add(MakeItem(path, ConverterItemStatus.Checking, "Checking..."));
            added++;
        }

        Notice = NoticeFor(duplicates, overflow, limit);
        if (added > 0 || duplicates > 0 || overflow > 0)
            Changed();
        return added;
    }

    /// <summary>Completes when the check running now (if any) has applied its answer. For tests and shutdown.</summary>
    public Task WhenCheckedAsync() => _checking;

    // ---- commands ----------------------------------------------------------------------------------------------------

    private void PickFiles()
    {
        IReadOnlyList<string>? chosen = null;
        IsDialogOpen = true;
        try
        {
            chosen = _picker.PickFiles();
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "converter.picker.failed", Component, "PickFiles", ExceptionType: ex.GetType()));
            ErrorMessage = "Lunar couldn't open the file chooser. You can also drag files onto this page.";
        }
        finally
        {
            IsDialogOpen = false;
        }

        if (chosen is { Count: > 0 })
            AddFiles(chosen);
    }

    private void Clear()
    {
        if (_disposed)
            return;

        ResetList();
        Notice = null;
        ErrorMessage = null;
    }

    // Empties the list of files to convert (not the conversions already started) and forgets the PDF password.
    private void ResetList()
    {
        _items.Clear();
        _keys.Clear();
        Options.ResetPdfPassword();
        Changed();
    }

    private async Task StartAsync(bool confirmed, CancellationToken ct)
    {
        var request = CurrentRequest;
        if (request is null)
            return;

        // Replacing existing results is only done after the user says so, here, at the moment of starting.
        if (request.Collision == CollisionPolicy.Overwrite && !confirmed)
        {
            IsConfirmingOverwrite = true;
            return;
        }

        IsConfirmingOverwrite = false;
        ErrorMessage = null;
        var started = await Queue.StartAsync(request, CurrentChoices, ct);
        if (_disposed || started.IsCancelled)
            return;
        if (started.IsFailure)
        {
            ErrorMessage = started.Error!.SafeMessage;
            return;
        }

        if (started.Value.JobIds.Count == 0)
        {
            Notice = "Nothing was left to convert. Check the list and try again.";
            return;
        }

        // The jobs now live in the queue below; the list is emptied so the same files cannot be started twice.
        var count = started.Value.JobIds.Count;
        ResetList();
        Notice = count == 1 ? "1 conversion started." : $"{count} conversions started.";
    }

    private void OnStartFault(Exception ex)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "converter.start.failed", Component, "Start", ExceptionType: ex.GetType()));
        ErrorMessage = "Lunar couldn't start the conversion. Try again.";
    }

    private void Remove(string fullPath)
    {
        if (_disposed)
            return;

        var index = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        _items.RemoveAt(index);
        _keys.Remove(KeyOf(fullPath));
        Notice = null;
        Changed();
    }

    private void Select(FileFormat format)
    {
        if (_disposed || _targets.All(t => t.Format != format || !t.IsAvailable))
            return;

        _userTarget = format;
        Changed();
    }

    // ---- checking ----------------------------------------------------------------------------------------------------

    private void Changed()
    {
        IsConfirmingOverwrite = false; // what was asked about is no longer what is on the page
        OutputFolder = Options.ChosenFolder ?? (_items.Count == 0 ? string.Empty : FolderOf(_items[0].FullPath));
        Raise(nameof(OutputFolder));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasItems));
        ((RelayCommand)ClearCommand).NotifyCanExecuteChanged();
        RefreshSummary();
        StartCheck();
    }

    // A newer check replaces an older one; the older one is cancelled and its answer is never applied.
    private void StartCheck()
    {
        _check?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _check = source;
        var paths = _items.Select(i => i.FullPath).ToArray();
        var folder = OutputFolder;
        var wanted = _userTarget;
        var collision = Options.Collision;
        IsChecking = true;
        _checking = CheckAsync(paths, folder, collision, wanted, source);
    }

    private async Task CheckAsync(string[] paths, string folder, CollisionPolicy collision, FileFormat? wanted, CancellationTokenSource source)
    {
        var ct = source.Token;
        try
        {
            var rendererAvailable = await IsRendererAvailableAsync(ct).ConfigureAwait(false);
            var formats = KnownFormats(paths);
            var offered = Offer(formats, rendererAvailable);
            var chosen = Choose(offered, wanted);

            Result<ConversionPlan>? planned = null;
            if (paths.Length > 0)
            {
                // The planner reads each file's start, so it runs on the pool; the UI thread only gets the answer.
                var request = new ConversionRequest(paths, chosen ?? FileFormat.Png, folder, collision);
                planned = await Task.Run(() => _planner.PlanAsync(request, ct), CancellationToken.None).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _post(() => Apply(source, paths, formats, rendererAvailable, chosen, planned));
        }
        catch (OperationCanceledException)
        {
            // A newer check or shutdown replaced this one.
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "converter.check.failed", Component, "Check", ExceptionType: ex.GetType()));
            _post(() => ApplyFailure(source, "Lunar couldn't check these files. Try adding them again."));
        }
        finally
        {
            if (ReferenceEquals(_check, source))
                _check = null;
            source.Dispose();
        }
    }

    private async Task<bool> IsRendererAvailableAsync(CancellationToken ct)
    {
        try
        {
            return await _renderer.IsAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not knowing means not offering the Word routes; the rest of the page still works.
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "converter.renderer.check_failed", Component, "Check", ExceptionType: ex.GetType()));
            return false;
        }
    }

    private void Apply(
        CancellationTokenSource source,
        string[] paths,
        IReadOnlyList<FileFormat> formats,
        bool rendererAvailable,
        FileFormat? chosen,
        Result<ConversionPlan>? planned)
    {
        if (_disposed || source.IsCancellationRequested || !SameList(paths))
            return;

        _selected = chosen;
        RebuildTargets(formats, rendererAvailable);
        Raise(nameof(SelectedFormat));

        if (planned is null)
        {
            _plan = null;
            Finish();
            return;
        }

        if (planned.IsFailure)
        {
            _plan = null;
            if (!planned.IsCancelled)
                ErrorMessage = planned.Error!.SafeMessage;
            MarkAll(ConverterItemStatus.NotChecked, "Not checked.");
            Finish();
            return;
        }

        ErrorMessage = null;
        _plan = planned.Value;
        var plan = planned.Value;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var job = plan.Jobs.FirstOrDefault(j => j.Index == i);
            var refused = plan.Rejected.FirstOrDefault(r => r.Index == i);
            var skipped = plan.Skipped.FirstOrDefault(s => s.Index == i);
            _items[i] = (job, refused, skipped) switch
            {
                ({ } j, _, _) => item with
                {
                    Status = ConverterItemStatus.Ready,
                    Message = j.WasRenamed ? $"Ready. The name is taken, so the result will be {j.OutputBaseName + j.OutputExtension}." : "Ready."
                },
                (_, { } r, _) => item with { Status = ConverterItemStatus.Refused, Message = r.Error.SafeMessage },
                (_, _, { }) => item with { Status = ConverterItemStatus.Skipped, Message = "Skipped: a file with the result's name already exists." },
                _ => item with { Status = ConverterItemStatus.NotChecked, Message = "Not checked." }
            };
        }

        Finish();
    }

    private void ApplyFailure(CancellationTokenSource source, string message)
    {
        if (_disposed || source.IsCancellationRequested)
            return;

        _plan = null;
        ErrorMessage = message;
        MarkAll(ConverterItemStatus.NotChecked, "Not checked.");
        Finish();
    }

    // The answer of the newest check is in: the summary, the choices that fit and whether starting is possible all follow it.
    private void Finish()
    {
        RefreshSummary();
        var routes = new List<ConversionRoute>();
        if (_selected is { } target)
        {
            foreach (var item in _items.Where(i => i.Status == ConverterItemStatus.Ready))
            {
                if (FileFormats.TryFromExtension(ExtensionOf(item.FullPath), out var source))
                {
                    var route = ConversionPolicy.Resolve(source, target);
                    if (!routes.Contains(route))
                        routes.Add(route);
                }
            }
        }

        Options.Update(routes);
        IsChecking = false;
        RaiseStart();
    }

    private void OnOptionsChanged(bool recheck)
    {
        if (_disposed)
            return;
        if (recheck)
            Changed();
        RaiseStart();
    }

    private void ChooseFolder()
    {
        string? chosen = null;
        IsDialogOpen = true;
        try
        {
            chosen = _folders.PickFolder();
        }
        catch (Exception ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "converter.folder_picker.failed", Component, "ChooseFolder", ExceptionType: ex.GetType()));
            ErrorMessage = "Lunar couldn't open the folder chooser.";
        }
        finally
        {
            IsDialogOpen = false;
        }

        if (!string.IsNullOrWhiteSpace(chosen))
            Options.SetChosenFolder(chosen);
    }

    private void RaiseStart()
    {
        Raise(nameof(CanStart));
        Raise(nameof(StartHint));
        ((AsyncRelayCommand)StartCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ConfirmOverwriteCommand).NotifyCanExecuteChanged();
    }

    private void MarkAll(ConverterItemStatus status, string message)
    {
        for (var i = 0; i < _items.Count; i++)
            _items[i] = _items[i] with { Status = status, Message = message };
    }

    private bool SameList(string[] paths)
    {
        if (paths.Length != _items.Count)
            return false;
        for (var i = 0; i < paths.Length; i++)
        {
            if (!string.Equals(paths[i], _items[i].FullPath, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    // ---- the formats on offer ----------------------------------------------------------------------------------------

    private static IReadOnlyList<FileFormat> KnownFormats(IEnumerable<string> paths)
    {
        var formats = new List<FileFormat>();
        foreach (var path in paths)
        {
            if (FileFormats.TryFromExtension(ExtensionOf(path), out var format) && !formats.Contains(format))
                formats.Add(format);
        }

        return formats;
    }

    private sealed record Offered(FileFormat Format, bool Available, string Note, bool PlainCopy);

    private static List<Offered> Offer(IReadOnlyList<FileFormat> sources, bool rendererAvailable)
    {
        var offered = new List<Offered>();
        foreach (var target in FileFormats.All)
        {
            if (sources.Count == 0)
            {
                offered.Add(new Offered(target, false, "Add files first.", false));
                continue;
            }

            var routes = sources.Select(s => ConversionPolicy.Resolve(s, target)).ToList();
            var unsupported = routes.FirstOrDefault(r => !r.IsSupported);
            if (unsupported is not null)
            {
                offered.Add(new Offered(target, false, $"{Label(unsupported.Source)} files can't become {Label(target)} in this version.", false));
            }
            else if (routes.Any(r => r.RequiresExternalRenderer) && !rendererAvailable)
            {
                offered.Add(new Offered(target, false, "Not available yet: it needs LibreOffice, which you install yourself (Lunar never installs it for you).", false));
            }
            else
            {
                var note = string.Join("; ", routes.Select(r => ModeNote(r.Mode)).Distinct());
                offered.Add(new Offered(target, true, note, routes.All(r => r.Mode == ConversionMode.SameFormat)));
            }
        }

        return offered;
    }

    // The user's choice while it still fits; otherwise the first format that does something (not a plain copy), then any.
    private static FileFormat? Choose(List<Offered> offered, FileFormat? wanted)
    {
        if (wanted is { } w && offered.Any(o => o.Format == w && o.Available))
            return w;
        return (offered.FirstOrDefault(o => o.Available && !o.PlainCopy) ?? offered.FirstOrDefault(o => o.Available))?.Format;
    }

    private void RebuildTargets(IReadOnlyList<FileFormat> sources, bool rendererAvailable)
    {
        var offered = Offer(sources, rendererAvailable);
        _targets.Clear();
        foreach (var option in offered)
        {
            var format = option.Format;
            _targets.Add(new TargetOption(
                format,
                Label(format),
                option.Available,
                option.Available && _selected == format,
                option.Note,
                new RelayCommand(() => Select(format), () => option.Available)));
        }
    }

    private static string ModeNote(ConversionMode mode) => mode switch
    {
        ConversionMode.SameFormat => "a copy under the new name",
        ConversionMode.Native => "the picture is re-encoded",
        ConversionMode.Embed => "the picture is placed inside the document",
        ConversionMode.Rasterize => "each page becomes a picture",
        ConversionMode.ExternalRenderer => "laid out by LibreOffice, so the layout may differ",
        _ => string.Empty
    };

    private static string Label(FileFormat format) => FileFormats.Extension(format)[1..].ToUpperInvariant();

    // ---- the list ----------------------------------------------------------------------------------------------------

    private ConverterItem MakeItem(string path, ConverterItemStatus status, string message)
    {
        var name = NameOf(path);
        var kind = FileFormats.TryFromExtension(ExtensionOf(path), out var format) ? KindText(format) : "Unknown type";
        return new ConverterItem(path, name, kind, status, message, new RelayCommand(() => Remove(path)));
    }

    private static string KindText(FileFormat format) => format switch
    {
        FileFormat.Png => "PNG picture",
        FileFormat.Jpg or FileFormat.Jpeg => "JPEG picture",
        FileFormat.Docx => "Word document",
        _ => "PDF document"
    };

    private void RefreshSummary()
    {
        var ready = _items.Count(i => i.Status == ConverterItemStatus.Ready);
        var refused = _items.Count(i => i.Status == ConverterItemStatus.Refused);
        var checking = _items.Count(i => i.Status == ConverterItemStatus.Checking);
        var parts = new List<string>();
        if (_items.Count > 0)
        {
            if (ready > 0)
                parts.Add(ready == 1 ? "1 file ready" : $"{ready} files ready");
            if (refused > 0)
                parts.Add(refused == 1 ? "1 can't be converted" : $"{refused} can't be converted");
            if (checking > 0)
                parts.Add("checking...");
        }

        SummaryText = string.Join(", ", parts);
        Raise(nameof(SummaryText));
    }

    private static string? NoticeFor(int duplicates, int overflow, int limit)
    {
        var parts = new List<string>();
        if (duplicates > 0)
            parts.Add(duplicates == 1 ? "1 file was already in the list." : $"{duplicates} files were already in the list.");
        if (overflow > 0)
            parts.Add($"Lunar converts at most {limit} files at a time, so {overflow} {(overflow == 1 ? "file was" : "files were")} not added.");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    // ---- names, pure text work (no file access) ----------------------------------------------------------------------

    private static string KeyOf(string path) => path.Trim();

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? "(unnamed)" : name;
    }

    private static string ExtensionOf(string path)
    {
        var name = NameOf(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    private static string FolderOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? string.Empty : path[..(cut == 2 && path[1] == ':' ? 3 : cut)];
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _check = null;
        _lifetime.Dispose();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

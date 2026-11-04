using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Conversion.Models;
using Lunar.Application.Conversion.UseCases;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Converter;

/// <summary>One conversion in the list on the page (FE-22): what it is, where it stands, how far it has come and what can be done with it.</summary>
/// <param name="JobId">The queue's id for the job.</param>
/// <param name="Title">"photo.png to JPG".</param>
/// <param name="State">The queue's state.</param>
/// <param name="StatusText">The state in words ("Waiting", "Converting", "Done", the reason it failed ...).</param>
/// <param name="Fraction">How far, 0 to 1, only when the converter really knows the page count; null otherwise (never made up).</param>
/// <param name="ProgressText">"3 of 10 pages" when <paramref name="Fraction"/> is known; empty otherwise.</param>
/// <param name="ShowProgressBar">True while the conversion runs or stops: a bar with a value when known, an indeterminate bar when not.</param>
/// <param name="ResultText">What was made ("photo.jpg", "12 pictures"); empty until it succeeds.</param>
/// <param name="FirstOutputPath">Where the first result is; shown only as a tooltip.</param>
/// <param name="CancelCommand">Stops a waiting or running conversion.</param>
/// <param name="RetryCommand">Queues a failed or cancelled conversion again.</param>
/// <param name="RevealCommand">Shows the result in the file manager.</param>
public sealed record JobRow(
    int JobId,
    string Title,
    ConversionJobState State,
    string StatusText,
    double? Fraction,
    string ProgressText,
    bool ShowProgressBar,
    string ResultText,
    string? FirstOutputPath,
    ICommand CancelCommand,
    ICommand RetryCommand,
    ICommand RevealCommand)
{
    public bool IsFinal => State is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Cancelled;

    public bool IsIndeterminate => ShowProgressBar && Fraction is null;

    public bool CanCancel => State is ConversionJobState.Queued or ConversionJobState.Running;

    public bool CanRetry => State is ConversionJobState.Failed or ConversionJobState.Cancelled;

    public bool CanReveal => State == ConversionJobState.Succeeded && FirstOutputPath is not null;

    public bool IsFailed => State == ConversionJobState.Failed;

    public bool IsNotFailed => State != ConversionJobState.Failed;

    /// <summary>What a screen reader says for the row.</summary>
    public string AutomationName => string.Join(", ", new[] { Title, StatusText, ProgressText, ResultText }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// The list of conversions on the Converter page (FE-22): starts them from the page's request, shows each one's state and honest
/// progress, and offers cancel, retry, show in folder, cancel all and clear finished. It reads the Application ports only
/// (<see cref="StartConversion"/>, <see cref="IConversionQueue"/>, <see cref="IOutputRevealer"/>) and calls no Windows API.
///
/// - Progress is exactly what the converter reported: a bar with a value and "n of m pages" only when the page count is known, an
///   indeterminate bar while running otherwise, nothing while waiting. No percentage is ever worked out here.
/// - The queue lives in the application, not in the island: collapsing the island, switching pages or closing the page's controls never
///   cancels, pauses or loses a conversion. Only the user's Cancel, or the application exiting, stops one.
/// - A failed or cancelled conversion says in words that nothing was saved (the queue guarantees no partial file) and can be tried again.
/// - Queue changes arrive on other threads and are marshalled through <c>post</c> before the list changes. Use on the UI thread.
/// </summary>
public sealed class ConverterQueueViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "ConverterQueue";

    private readonly StartConversion _start;
    private readonly IConversionQueue _queue;
    private readonly IOutputRevealer _revealer;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<JobRow> _rows = [];
    private readonly Dictionary<int, ConversionJobSnapshot> _latest = [];
    private IDisposable? _subscription;
    private bool _disposed;
    private string? _errorMessage;

    public ConverterQueueViewModel(StartConversion start, IConversionQueue queue, IOutputRevealer revealer, Action<Action> post, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(revealer);
        ArgumentNullException.ThrowIfNull(post);
        _start = start;
        _queue = queue;
        _revealer = revealer;
        _post = post;
        _log = log;
        Rows = new ReadOnlyObservableCollection<JobRow>(_rows);
        CancelAllCommand = new RelayCommand(() => _queue.CancelAll(), () => IsBusy);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => _rows.Any(r => r.IsFinal));
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);

        // Read the queue first (the changes have no replay), then follow it.
        foreach (var job in _queue.Jobs)
            _latest[job.JobId] = job;
        Rebuild();
        _subscription = _queue.Changes.Subscribe(new Watcher(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread each time a conversion newly reaches Done (the island celebrates).</summary>
    public event EventHandler? JobSucceeded;

    public ReadOnlyObservableCollection<JobRow> Rows { get; }

    public bool HasRows => _rows.Count > 0;

    /// <summary>True while any conversion is waiting, running or stopping.</summary>
    public bool IsBusy => _rows.Any(r => !r.IsFinal);

    public string SummaryText { get; private set; } = string.Empty;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value)
                return;
            _errorMessage = value;
            Raise();
            ((RelayCommand)DismissErrorCommand).NotifyCanExecuteChanged();
        }
    }

    public ICommand CancelAllCommand { get; }

    public ICommand ClearFinishedCommand { get; }

    public ICommand DismissErrorCommand { get; }

    /// <summary>
    /// Plans and queues the request. A refusal of the whole request (the queue is full, a choice is invalid) comes back as a failed result
    /// and is also shown here; files that were refused one by one are in the returned plan.
    /// </summary>
    public async Task<Result<ConversionStart>> StartAsync(ConversionRequest request, ConversionChoices choices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(choices);
        ErrorMessage = null;
        // Planning reads each file's start, so it runs on the pool and the UI thread is never held.
        var result = await Task.Run(() => _start.ExecuteAsync(request, choices, ct), CancellationToken.None);
        if (result.IsFailure && !result.IsCancelled)
            ErrorMessage = result.Error!.SafeMessage;
        return result;
    }

    // ---- following the queue -----------------------------------------------------------------------------------------

    private void OnChanged(ConversionJobSnapshot snapshot)
    {
        _post(() =>
        {
            if (_disposed)
                return;
            var wasDone = _latest.TryGetValue(snapshot.JobId, out var before) && before.State == ConversionJobState.Succeeded;
            _latest[snapshot.JobId] = snapshot;
            Upsert(snapshot);
            Refresh();
            if (snapshot.State == ConversionJobState.Succeeded && !wasDone)
                JobSucceeded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Upsert(ConversionJobSnapshot snapshot)
    {
        var row = RowFor(snapshot);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].JobId == snapshot.JobId)
            {
                _rows[i] = row;
                return;
            }
        }

        _rows.Add(row);
    }

    private void Rebuild()
    {
        _rows.Clear();
        foreach (var snapshot in _latest.Values.OrderBy(s => s.JobId))
            _rows.Add(RowFor(snapshot));
        Refresh();
    }

    private void Refresh()
    {
        var done = _rows.Count(r => r.State == ConversionJobState.Succeeded);
        var failed = _rows.Count(r => r.IsFailed);
        var active = _rows.Count(r => !r.IsFinal);
        var parts = new List<string>();
        if (active > 0)
            parts.Add($"{active} in progress");
        if (done > 0)
            parts.Add($"{done} done");
        if (failed > 0)
            parts.Add($"{failed} failed");
        SummaryText = string.Join(", ", parts);
        Raise(nameof(SummaryText));
        Raise(nameof(HasRows));
        Raise(nameof(IsBusy));
        ((RelayCommand)CancelAllCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ClearFinishedCommand).NotifyCanExecuteChanged();
    }

    private JobRow RowFor(ConversionJobSnapshot s)
    {
        var id = s.JobId;
        var running = s.State is ConversionJobState.Running or ConversionJobState.Cancelling;
        double? fraction = running && s.Progress.IsDeterminate ? s.Progress.Fraction : null;
        var progressText = fraction is not null ? $"{s.Progress.PagesCompleted} of {s.Progress.PagesTotal} pages" : string.Empty;
        var first = s.State == ConversionJobState.Succeeded && s.OutputPaths.Count > 0 ? s.OutputPaths[0] : null;
        return new JobRow(
            id,
            TitleOf(s.Job),
            s.State,
            StatusOf(s),
            fraction,
            progressText,
            running,
            ResultOf(s),
            first,
            new RelayCommand(() => Cancel(id)),
            new RelayCommand(() => Retry(id)),
            new AsyncRelayCommand(ct => RevealAsync(id, ct), null, OnFault, _lifetime.Token));
    }

    private static string TitleOf(PlannedConversion job)
    {
        var name = job.SourcePath;
        var cut = name.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
            name = name[(cut + 1)..];
        return $"{name} to {TargetLabel(job)}";
    }

    private static string TargetLabel(PlannedConversion job) => job.OutputExtension.TrimStart('.').ToUpperInvariant();

    private static string StatusOf(ConversionJobSnapshot s) => s.State switch
    {
        ConversionJobState.Queued => "Waiting",
        ConversionJobState.Running => "Converting",
        ConversionJobState.Cancelling => "Stopping and cleaning up...",
        ConversionJobState.Succeeded => "Done",
        ConversionJobState.Failed => (s.Error?.SafeMessage ?? "It could not be converted.") + " Nothing was saved.",
        _ => "Cancelled. Nothing was saved."
    };

    private static string ResultOf(ConversionJobSnapshot s)
    {
        if (s.State != ConversionJobState.Succeeded || s.OutputPaths.Count == 0)
            return string.Empty;
        if (s.OutputPaths.Count > 1)
            return $"{s.OutputPaths.Count} pictures saved";
        var path = s.OutputPaths[0];
        var cut = path.LastIndexOfAny(['\\', '/']);
        return "Saved as " + (cut >= 0 ? path[(cut + 1)..] : path);
    }

    // ---- row commands ------------------------------------------------------------------------------------------------

    private void Cancel(int id)
    {
        var result = _queue.Cancel(id);
        if (result.IsFailure && !result.IsCancelled)
            ErrorMessage = result.Error!.SafeMessage;
    }

    private void Retry(int id)
    {
        var result = _queue.Retry(id);
        if (result.IsFailure && !result.IsCancelled)
            ErrorMessage = result.Error!.SafeMessage;
    }

    private async Task RevealAsync(int id, CancellationToken ct)
    {
        if (!_latest.TryGetValue(id, out var snapshot) || snapshot.OutputPaths.Count == 0)
            return;

        var result = await _revealer.RevealAsync(snapshot.OutputPaths[0], ct);
        if (result.IsFailure && !result.IsCancelled)
            ErrorMessage = result.Error!.SafeMessage;
    }

    private void ClearFinished()
    {
        _queue.ClearFinished();
        foreach (var id in _latest.Where(p => p.Value.IsFinal).Select(p => p.Key).ToList())
            _latest.Remove(id);
        Rebuild();
    }

    private void OnFault(Exception ex)
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "converter.queue.command_failed", Component, "Command", ExceptionType: ex.GetType()));
        ErrorMessage = "That did not work. Try again.";
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class Watcher(ConverterQueueViewModel owner) : IObserver<ConversionJobSnapshot>
    {
        public void OnNext(ConversionJobSnapshot value) => owner.OnChanged(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}

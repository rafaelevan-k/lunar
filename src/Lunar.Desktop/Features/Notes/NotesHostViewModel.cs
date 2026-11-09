using System.ComponentModel;
using Lunar.Desktop.Features.Recorder;

namespace Lunar.Desktop.Features.Notes;

/// <summary>
/// Which of the two views of the Notes page is shown (FE-34). Both stay alive, so switching never loses a note being edited and never touches a recording.
/// It starts on Notes and lasts while Lunar runs. Use on the UI thread.
/// </summary>
public sealed class NotesHostViewModel : INotifyPropertyChanged
{
    private readonly CancellationToken _lifetime;
    private bool _isRecordsShown;

    public NotesHostViewModel(NotesPageViewModel notes, RecordsViewModel records, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(records);
        Notes = notes;
        Records = records;
        _lifetime = lifetime;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NotesPageViewModel Notes { get; }

    public RecordsViewModel Records { get; }

    public bool IsNotesShown
    {
        get => !_isRecordsShown;
        set => Show(records: !value);
    }

    public bool IsRecordsShown
    {
        get => _isRecordsShown;
        set => Show(records: value);
    }

    private void Show(bool records)
    {
        if (_isRecordsShown == records)
            return;
        _isRecordsShown = records;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotesShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecordsShown)));
        if (records)
            Refresh();
    }

    // A failed read only leaves the list as it was: Record still tells the truth when it is pressed.
    private async void Refresh()
    {
        try
        {
            await Records.RefreshInputsAsync(_lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Lunar is closing.
        }
    }
}

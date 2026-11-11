using System.ComponentModel;
using System.Windows.Input;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Navigation;

/// <summary>
/// The selected page of the expanded island (FE-11): an index that always stays within 0 to <see cref="PageCount"/> - 1,
/// with commands for the arrow buttons, the page dots and the keyboard. Out-of-range requests are refused (they return
/// false and change nothing). It makes no OS calls and owns no timers. Must be used on the UI thread.
/// </summary>
public sealed class PageNavigationViewModel : INotifyPropertyChanged
{
    public const int PageCount = 6;

    private readonly string[] _titles;
    private int _selectedIndex;

    /// <param name="titles">Exactly six page titles (System, Media, Pomodoro, Converter, Notes, Usage), used for accessible names.</param>
    public PageNavigationViewModel(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        if (titles.Count != PageCount || titles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Exactly {PageCount} non-empty page titles are required.", nameof(titles));

        _titles = [.. titles];
        NextCommand = new RelayCommand(() => Next(), () => CanGoNext);
        PreviousCommand = new RelayCommand(() => Previous(), () => CanGoPrevious);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<string> Titles => _titles;

    public int SelectedIndex => _selectedIndex;

    public string SelectedTitle => _titles[_selectedIndex];

    public bool CanGoNext => _selectedIndex < PageCount - 1;

    public bool CanGoPrevious => _selectedIndex > 0;

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    /// <summary>Selects a page. False (and no change) when the index is out of range or already selected.</summary>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= PageCount || index == _selectedIndex)
            return false;

        _selectedIndex = index;
        Raise(nameof(SelectedIndex));
        Raise(nameof(SelectedTitle));
        Raise(nameof(CanGoNext));
        Raise(nameof(CanGoPrevious));
        ((RelayCommand)NextCommand).NotifyCanExecuteChanged();
        ((RelayCommand)PreviousCommand).NotifyCanExecuteChanged();
        return true;
    }

    public bool Next() => GoTo(_selectedIndex + 1);

    public bool Previous() => GoTo(_selectedIndex - 1);

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

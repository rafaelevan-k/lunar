using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Notes;

/// <summary>
/// The Notes page (FE-31). Presentation only: the island never takes the keyboard by itself (ADR-0007). While the pointer is over this page a
/// click may activate the island, so clicking New note, a note or the text gives the island the keyboard; the note then gets focus.
/// Escape in the note closes it (saving it) instead of collapsing the island. No logic and no Windows API here.
/// </summary>
public partial class NotesPage : UserControl
{
    private NotesPageViewModel? _model;

    public NotesPage()
    {
        InitializeComponent();
        Editor.PreviewMouseLeftButtonDown += OnEditorMouseDown;
        MouseEnter += (_, _) => Island?.BeginTypingHover();
        MouseLeave += (_, _) => Island?.EndTypingHover();
        Editor.PreviewKeyDown += OnEditorKeyDown;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(DataContext as NotesPageViewModel);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Watch(e.NewValue as NotesPageViewModel);

    private void Watch(NotesPageViewModel? model)
    {
        if (ReferenceEquals(model, _model))
            return;
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
    }

    private bool _wasEditing;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var editing = _model?.IsEditing == true;
        if (editing == _wasEditing)
            return;
        _wasEditing = editing;
        // After the layout shows the editor (or the list again), move the keyboard to where the user is.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (editing)
            {
                RequestKeyboard();
                Editor.Focus();
                Keyboard.Focus(Editor);
                Editor.CaretIndex = Editor.Text.Length;
            }
            else if (IsKeyboardFocusWithin && NewNoteButton.IsVisible)
            {
                Keyboard.Focus(NewNoteButton);
            }
        });
    }

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e) => RequestKeyboard();

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _model?.CloseEditorCommand is not { } close)
            return;
        e.Handled = true; // closes the note, not the island
        if (close.CanExecute(null))
            close.Execute(null);
    }

    private IslandWindow? Island => Window.GetWindow(this) as IslandWindow;

    /// <summary>
    /// Typing needs the keyboard. Over this page a click already activates the island (see <see cref="IslandWindow.BeginTypingHover"/>); this
    /// covers a note opened from the keyboard. The island lets go of the keyboard again as soon as the user clicks elsewhere.
    /// </summary>
    private void RequestKeyboard()
    {
        if (Island is { IsActive: false } island)
            island.OpenForKeyboard();
    }
}

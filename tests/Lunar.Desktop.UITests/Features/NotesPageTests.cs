using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Notes;
using Lunar.Desktop.Features.Notes;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Notes;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-31: the real Notes page on an STA thread at the island's page size, over the real NotesService and an in-memory notes file. Whether the
// island really takes the keyboard on a click, typing with a real keyboard, Narrator, and survival of a restart are manual checks
// (FE-31 evidence).
public sealed class NotesPageTests
{
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF notes page check failed.", failure);
    }

    private static void Pump(Func<bool> done, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The page did not reach the expected state in time.");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static Note Stored(int i) =>
        Note.Create(Guid.NewGuid(), $"Note number {i}\nbody", new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero).AddMinutes(-i)).Value;

    private sealed class Rig : IDisposable
    {
        public Rig(int notes = 0, FrameworkElement? host = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Repository.Stored = Enumerable.Range(0, notes).Select(Stored).ToList();
            Service = new NotesService(Repository, new NotesPageViewModelTests.FixedTime());
            var dispatcher = Dispatcher.CurrentDispatcher;
            Model = new NotesPageViewModel(Service, work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); },
                new ManualTimer(), new ManualTimer(), new NotesPageViewModelTests.FixedTime());
            Page = new NotesPage { DataContext = Model };
            Window = new Window
            {
                Width = PageWidth, Height = PageHeight, Content = host ?? Page, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual
            };
            foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
                Window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.Show();
            var started = Model.InitializeAsync(CancellationToken.None);
            Pump(() => started.IsCompleted);
            Window.UpdateLayout();
        }

        public FakeNotesRepository Repository { get; } = new();

        public NotesService Service { get; }

        public NotesPageViewModel Model { get; }

        public NotesPage Page { get; }

        public Window Window { get; }

        public T Named<T>(string name)
            where T : class =>
            (Page.FindName(name) as T) ?? throw new InvalidOperationException(name + " is missing.");

        public void Run(ICommand command)
        {
            command.Execute(null);
            Pump(() => !((Lunar.Desktop.Shell.AsyncRelayCommand)command).IsRunning);
            Pump();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
            Service.Dispose();
        }
    }

    [Fact]
    public void AThousandNotesScrollInsideThePageWithoutBuildingEveryRow() => RunOnSta(() =>
    {
        using var rig = new Rig(notes: 1000);
        var list = rig.Named<ItemsControl>("NotesList");
        var scroll = Descendants<ScrollViewer>(list).First();

        Assert.Equal(1000, list.Items.Count);
        Assert.True(list.ActualHeight <= PageHeight, "The list is taller than the page.");
        Assert.True(scroll.ExtentHeight > scroll.ViewportHeight, "The list does not scroll.");
        var realized = Descendants<ContentPresenter>(list).Where(c => c.Content is NoteListItem).Count();
        Assert.InRange(realized, 1, 60); // virtualized: only the rows on screen exist

        var clock = Stopwatch.StartNew();
        scroll.ScrollToEnd();
        Pump();
        list.UpdateLayout();
        clock.Stop();

        Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
        Assert.Contains(Descendants<TextBlock>(list), t => t.Text == "Note number 999");
        Assert.True(clock.ElapsedMilliseconds < 2000, $"Scrolling to the end took {clock.ElapsedMilliseconds} ms.");
        Assert.InRange(Descendants<ContentPresenter>(list).Where(c => c.Content is NoteListItem).Count(), 1, 60);
    });

    [Fact]
    public void TheListTheEmptyStateAndAnOpenNoteHaveNoAccessibilityProblems() => RunOnSta(() =>
    {
        using (var empty = new Rig())
        {
            Assert.True(empty.Named<TextBlock>("EmptyText").IsVisible);
            Assert.Empty(AutomationAudit.Problems(empty.Page));
        }

        using var rig = new Rig(notes: 3);
        Assert.Empty(AutomationAudit.Problems(rig.Page));
        var rows = Descendants<Button>(rig.Named<ItemsControl>("NotesList")).Where(b => b.Command is not null).ToList();
        Assert.Contains(rows, b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Note number 0, changed ", StringComparison.Ordinal));
        Assert.Contains(rows, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Delete Note number 0");

        rig.Run(rig.Model.Items[0].OpenCommand);
        var editor = rig.Named<TextBox>("Editor");
        Assert.True(editor.IsVisible);
        Assert.False(rig.Named<ItemsControl>("NotesList").IsVisible);
        Assert.Equal("Note text", System.Windows.Automation.AutomationProperties.GetName(editor));
        Assert.Empty(AutomationAudit.Problems(rig.Page));
    });

    [Fact]
    public void TheWholeRoundTripWorksFromTheKeyboard() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var newNote = rig.Named<Button>("NewNoteButton");
        Assert.True(newNote.IsVisible);

        rig.Run(newNote.Command);
        var editor = rig.Named<TextBox>("Editor");
        Pump(() => editor.IsVisible);
        editor.Text = "Written with keys";
        Pump();

        // Escape closes the note (saving it); it never reaches the island, which would collapse.
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(escape);
        Pump(() => !rig.Model.IsEditing);
        Assert.True(escape.Handled);
        Assert.Equal("Written with keys", Assert.Single(rig.Repository.Stored).Text);

        // The Delete key on a row deletes it, and Undo brings it back.
        rig.Window.UpdateLayout();
        var row = Descendants<Button>(rig.Named<ItemsControl>("NotesList")).First(b => b.InputBindings.Count > 0);
        var binding = (KeyBinding)row.InputBindings[0];
        Assert.Equal(Key.Delete, binding.Key);
        rig.Run(binding.Command);
        Assert.Empty(rig.Repository.Stored);
        Assert.True(rig.Named<Button>("UndoButton").IsVisible);
        rig.Run(rig.Named<Button>("UndoButton").Command);
        Assert.Single(rig.Repository.Stored);
    });

    [Fact]
    public void TypingInANoteNeverSwipesThePagesEvenAtTheEndOfTheText() => RunOnSta(() =>
    {
        var navigation = new PageNavigationViewModel(["System", "Media", "File converter", "Notes", "Usage"]);
        var swiper = new SwiperView();
        var notesHost = new ContentControl();
        var pages = new FrameworkElement[] { new TextBlock { Text = "one" }, new TextBlock { Text = "two" }, new TextBlock { Text = "three" }, notesHost, new TextBlock { Text = "five" } };
        swiper.Initialize(navigation, pages, reduceMotion: true);
        using var rig = new Rig(host: swiper);
        notesHost.Content = rig.Page;
        navigation.GoTo(3);
        Pump();
        rig.Run(rig.Model.NewNoteCommand);
        var editor = rig.Named<TextBox>("Editor");
        Pump(() => editor.IsVisible);
        editor.Text = "end";
        editor.CaretIndex = editor.Text.Length;

        foreach (var key in new[] { Key.Right, Key.Left })
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
            editor.RaiseEvent(args); // a key the box did not use bubbles up to the swiper
            Assert.Equal(3, navigation.SelectedIndex);
        }

        Assert.True(SwiperView.GetBlocksSwipe(editor));
    });

    // The fix for keys going to the other app: over the Notes page the island may be activated by the click itself (which Windows always
    // allows), and it goes back to non-activating when the pointer leaves without a click.
    [Fact]
    public void OverTheNotesPageAClickMayActivateTheIslandAndLeavingRestoresNonActivating() => RunOnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var service = new NotesService(new FakeNotesRepository());
        using var model = new NotesPageViewModel(service, work => work(), new ManualTimer(), new ManualTimer());
        var page = new NotesPage { DataContext = model };
        var island = new Lunar.Desktop.Shell.IslandWindow { ViewContent = page, Left = -20000, Top = -20000 };
        foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
            island.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
        island.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        island.Show();
        Pump();
        var handle = new System.Windows.Interop.WindowInteropHelper(island).Handle;
        Assert.True(NonActivating(handle));

        page.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert.False(NonActivating(handle));

        page.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert.True(NonActivating(handle));
        island.Close();
    });

    private static bool NonActivating(IntPtr handle) => (GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000L) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [Fact]
    public void ASaveFailureAndAnUnreadableFileAreShownInWords() => RunOnSta(() =>
    {
        using var rig = new Rig(notes: 1);
        rig.Run(rig.Model.Items[0].OpenCommand);
        rig.Repository.FailSaveWith = Lunar.Domain.Shared.ErrorCode.IoFailure;
        rig.Named<TextBox>("Editor").Text = "not saved";
        rig.Run(rig.Model.CloseEditorCommand);

        Assert.True(rig.Named<TextBlock>("ProblemText").IsVisible);
        Assert.Equal("Your notes file couldn't be written. The note stays open so nothing is lost.", rig.Named<TextBlock>("ProblemText").Text);
        Assert.Equal("Not saved", rig.Named<TextBlock>("SaveStatusText").Text);
        Assert.True(rig.Named<TextBox>("Editor").IsVisible);
    });

    [Fact]
    public void ARestoredFileIsToldAboveTheList() => RunOnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var repository = new FakeNotesRepository { Stored = [Stored(1)], Recovery = Lunar.Application.Abstractions.Notes.NotesRecovery.RestoredFromBackup };
        using var service = new NotesService(repository);
        using var model = new NotesPageViewModel(service, work => work(), new ManualTimer(), new ManualTimer());
        var page = new NotesPage { DataContext = model };
        var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
            window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        window.Show();
        var started = model.InitializeAsync();
        Pump(() => started.IsCompleted);
        window.UpdateLayout();

        var notice = (TextBlock)page.FindName("NoticeText");
        Assert.True(notice.IsVisible);
        Assert.Contains("notes.corrupt.json", notice.Text, StringComparison.Ordinal);
        Assert.True(((ItemsControl)page.FindName("NotesList")).IsVisible);
        window.Close();
    });
}

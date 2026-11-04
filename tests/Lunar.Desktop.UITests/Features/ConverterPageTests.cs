using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Conversion.Planning;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Conversion;
using SkiaSharp;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-20: the real Converter page, real theme and control styles, on an STA thread, bound to the real view model over real synthetic
// files. A physical drag from Explorer and the real Windows file dialog are manual checks (NOT_RUN here).
public sealed class ConverterPageTests
{
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private sealed class NoPicker : IFilePicker
    {
        public IReadOnlyList<string>? PickFiles() => null;
    }

    private sealed class NoRenderer : IRendererAvailability
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(task.IsCompleted, "The check did not finish in time.");
        task.GetAwaiter().GetResult();
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
            throw new InvalidOperationException("WPF converter page check failed.", failure);
    }

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

    private static string MakePng(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(new SKColor(10, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void WithPage(string colors, Action<ConverterPage, ConverterPageViewModel, Window, string> check)
    {
        RunOnSta(() =>
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunar-fe20ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var model = new ConverterPageViewModel(new ConversionPlanner(new NoRenderer()), new NoRenderer(), new NoPicker(),
                work => { if (dispatcher.CheckAccess()) work(); else dispatcher.BeginInvoke(work); });
            var page = new ConverterPage { DataContext = model };
            var window = new Window { Width = PageWidth, Height = PageHeight, Content = page, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
            window.Resources.MergedDictionaries.Add(Load("Themes", colors));
            window.Resources.MergedDictionaries.Add(Load("Themes", "Typography.xaml"));
            window.Resources.MergedDictionaries.Add(Load("Themes", "Spacing.xaml"));
            window.Resources.MergedDictionaries.Add(Load("Themes", "Components.xaml"));
            window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            window.Show();
            window.UpdateLayout();
            try
            {
                check(page, model, window, folder);
            }
            finally
            {
                model.Dispose();
                window.Close();
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    private static void Settle(Window window)
    {
        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        window.UpdateLayout();
    }

    [Theory]
    [InlineData("Colors.Dark.xaml")]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.HighContrast.xaml")]
    public void TheEmptyPageInvitesADropAndFitsTheIsland(string colors) => WithPage(colors, (page, model, window, _) =>
    {
        Assert.True(page.AllowDrop);
        var empty = (FrameworkElement)page.FindName("EmptyFace");
        var list = (FrameworkElement)page.FindName("ListScroll");
        Assert.Equal(Visibility.Visible, empty.Visibility);
        Assert.Equal(Visibility.Collapsed, list.Visibility);
        Assert.Equal(5, Descendants<Button>(page).Count(b => b.Command is not null && b.DataContext is TargetOption));
        Assert.True(page.ActualWidth <= PageWidth + 0.5 && page.ActualHeight <= PageHeight + 0.5, "the page stays inside the island");
        Assert.Equal("File converter", AutomationProperties.GetName(page));
    });

    [Fact]
    public void AListShowsEachFileItsReasonAndOnlyTheFormatsThatFit() => WithPage("Colors.Dark.xaml", (page, model, window, folder) =>
    {
        var junk = System.IO.Path.Combine(folder, "notes.txt");
        File.WriteAllText(junk, "hello");
        model.AddFiles([MakePng(folder, "photo.png"), junk]);
        Pump(model.WhenCheckedAsync());
        Settle(window);

        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)page.FindName("EmptyFace")).Visibility);
        var list = (ItemsControl)page.FindName("FileList");
        Assert.Equal(2, list.Items.Count);
        var texts = Descendants<TextBlock>(list).Select(t => t.Text).ToList();
        Assert.Contains("photo.png", texts);
        Assert.Contains(texts, t => t.Contains("isn't supported", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("PNG picture - Ready.", StringComparison.Ordinal));
        Assert.Equal("1 file ready, 1 can't be converted", ((TextBlock)page.FindName("SummaryText")).Text);

        var names = list.Items.Cast<ConverterItem>().Select(i => i.AutomationName).ToList();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    });

    [Fact]
    public void AnUnavailableFormatIsDisabledAndAnAvailableOneIsPressable() => WithPage("Colors.Dark.xaml", (page, model, window, folder) =>
    {
        var pdf = System.IO.Path.Combine(folder, "a.pdf");
        using (var stream = new FileStream(pdf, FileMode.Create))
        using (var document = SKDocument.CreatePdf(stream))
        {
            document.BeginPage(100, 100);
            document.EndPage();
            document.Close();
        }

        model.AddFiles([pdf]);
        Pump(model.WhenCheckedAsync());
        Settle(window);

        var buttons = Descendants<Button>((FrameworkElement)page.FindName("FormatList")).ToList();
        Assert.Equal(5, buttons.Count);
        var word = buttons.Single(b => b.Content as string == "DOCX");
        var png = buttons.Single(b => b.Content as string == "PNG");
        Assert.False(word.IsEnabled);
        Assert.True(png.IsEnabled);
        Assert.Contains("not available", AutomationProperties.GetName(word), StringComparison.Ordinal);
        Assert.Contains("selected", AutomationProperties.GetName(png), StringComparison.Ordinal);
    });

    [Fact]
    public void ARefusalOfTheWholeListShowsInTheErrorBar() => WithPage("Colors.Dark.xaml", (page, model, window, folder) =>
    {
        model.AddFiles([System.IO.Path.Combine(folder, "missing-folder", "a.png")]);
        Pump(model.WhenCheckedAsync());
        Settle(window);

        var bar = (Lunar.Desktop.Controls.InlineError)page.FindName("ErrorBar");
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Contains("folder", bar.Message, StringComparison.OrdinalIgnoreCase);
    });
}

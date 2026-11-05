using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The Usage page (FE-27). Presentation only: it attaches the Claude palette (brand colors, or Windows colors in High Contrast) and hands
/// the pasted token to <see cref="UsagePageViewModel.SaveTokenAsync"/>. The password box cannot be bound, so the click reads it, clears it at
/// once whatever the result, and passes the text on without keeping it. No logic and no Windows API here.
/// </summary>
public partial class UsagePage : UserControl
{
    public UsagePage()
    {
        InitializeComponent();
        var palette = SystemParameters.HighContrast ? "Colors.Usage.HighContrast.xaml" : "Colors.Usage.xaml";
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Lunar.Desktop;component/Themes/" + palette, UriKind.Relative) });
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnTokenKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SaveAsync();
    }

    // The view model catches its own failures, so nothing escapes this async void.
    private async Task SaveAsync()
    {
        if (DataContext is not UsagePageViewModel viewModel)
            return;
        var pasted = TokenBox.Password;
        TokenBox.Clear();
        await viewModel.SaveTokenAsync(pasted);
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Lunar.Desktop.Features.Converter;

/// <summary>
/// The Converter page (FE-20). Presentation only: the XAML binds to <see cref="ConverterPageViewModel"/>. The one thing done here
/// is turning a drop into a call: the dragged names go to <see cref="ConverterPageViewModel.AddFiles"/> and nothing else happens
/// to them (no file is opened, moved or deleted; the drop is always a copy-style drop that only reads names).
/// </summary>
public partial class ConverterPage : UserControl
{
    /// <summary>True while the page shows its two-column Notch layout.</summary>
    internal bool IsCompact { get; private set; }

    protected override void OnPropertyChanged(System.Windows.DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Lunar.Desktop.Navigation.CompactLayout.IsCompactProperty)
            ApplyLayout((bool)e.NewValue);
    }

    /// <summary>
    /// The open Notch is wide and short (ADR-0027, amended 2026-11-18). There a step fits without scrolling: the step uses the panel's full width, its
    /// options stand two side by side, the summary line above them is left out (the choices show it), the drop area and the file list are a little
    /// shorter, and Back with Next or Convert sit in one row at the bottom right. The Pill keeps one column with the actions under the step, clear of
    /// its side arrows.
    /// </summary>
    private void ApplyLayout(bool compact)
    {
        IsCompact = compact;
        // The same side and bottom room as the other pages, in both styles (2026-11-18, the owner).
        Layout.Margin = new System.Windows.Thickness(40, 4, 40, 6);
        PlaceActions(compact);
        EmptyIcon.Visibility = compact ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        Actions.Margin = compact ? new System.Windows.Thickness(0, 4, 0, 0) : new System.Windows.Thickness(0, 6, 0, 0);
        DropArea.MinHeight = compact ? 64 : 84;
        ListScroll.MaxHeight = compact ? 96 : 130;
        TweaksSummaryText.Visibility = compact ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        UpdateOptionWidth();
    }

    // Pill: [Back][primary, wide] with the hint under them. Notch: [hint][Back][primary, 170 DIP] in one row.
    private void PlaceActions(bool compact)
    {
        if (compact && StartHintText.Parent == Actions)
        {
            Actions.Children.Remove(StartHintText);
            ActionRow.Children.Add(StartHintText);
        }
        else if (!compact && StartHintText.Parent == ActionRow)
        {
            ActionRow.Children.Remove(StartHintText);
            Actions.Children.Add(StartHintText);
        }

        var star = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        ActionRow.ColumnDefinitions[0].Width = compact ? star : System.Windows.GridLength.Auto;
        ActionRow.ColumnDefinitions[1].Width = compact ? System.Windows.GridLength.Auto : star;
        ActionRow.ColumnDefinitions[2].Width = compact ? new System.Windows.GridLength(170) : new System.Windows.GridLength(0);
        Grid.SetColumn(StartHintText, 0);
        Grid.SetColumn(BackButton, compact ? 1 : 0);
        foreach (var primary in new System.Windows.FrameworkElement[] { NextButton, ConvertButton, ConvertMoreButton })
            Grid.SetColumn(primary, compact ? 2 : 1);
        StartHintText.TextAlignment = compact ? System.Windows.TextAlignment.Right : System.Windows.TextAlignment.Center;
        StartHintText.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        StartHintText.Margin = compact ? new System.Windows.Thickness(0, 0, 10, 0) : new System.Windows.Thickness(0, 4, 0, 0);
    }

    // Each option block is a WrapPanel item: the whole width in one column, or half of it (less a gap) in two.
    private void UpdateOptionWidth()
    {
        var width = TweaksFold.ActualWidth;
        if (width <= 0)
            return;
        OptionsPanel.ItemWidth = IsCompact ? Math.Floor(width / 2) : width;
        foreach (System.Windows.FrameworkElement block in OptionsPanel.Children)
            block.Margin = IsCompact ? new System.Windows.Thickness(0, 0, 12, 0) : new System.Windows.Thickness(0);
        TweaksFold.Margin = IsCompact ? new System.Windows.Thickness(0) : new System.Windows.Thickness(0, 4, 0, 0);
    }

    public ConverterPage()
    {
        InitializeComponent();
        ApplyLayout(Lunar.Desktop.Navigation.CompactLayout.GetIsCompact(this));
        TweaksFold.SizeChanged += (_, _) => UpdateOptionWidth();
        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, _) => Model?.SetDragActive(false);
        Drop += OnDrop;
        // A PasswordBox cannot be bound. The typed password goes straight to the options (memory only) and is never read back here.
        PdfPasswordBox.PasswordChanged += (_, _) => Model?.Options.SetPdfPassword(PdfPasswordBox.Password);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ConverterPageViewModel old)
                old.Options.PdfPasswordReset -= OnPasswordReset;
            if (e.NewValue is ConverterPageViewModel now)
                now.Options.PdfPasswordReset += OnPasswordReset;
        };
    }

    // The options forgot the password (the list was emptied or handed to the queue): empty the box on screen as well.
    private void OnPasswordReset(object? sender, EventArgs e) => PdfPasswordBox.Clear();

    private ConverterPageViewModel? Model => DataContext as ConverterPageViewModel;

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDrag(object sender, DragEventArgs e)
    {
        var accepts = HasFiles(e);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        Model?.SetDragActive(accepts);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        Model?.SetDragActive(false);
        if (HasFiles(e) && e.Data.GetData(DataFormats.FileDrop) is string[] names)
            Model?.AddFiles(names);
        e.Handled = true;
    }
}

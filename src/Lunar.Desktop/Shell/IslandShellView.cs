using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Lunar.Desktop.Behaviors;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Navigation;

namespace Lunar.Desktop.Shell;

/// <summary>
/// What the island window shows (FE-11): the eyes in a fixed strip at the top, which is the whole idle pill and stays
/// clickable when expanded, and the page swiper below it. The swiper is invisible and does not take input unless the
/// island is fully expanded, so while the pill grows or shrinks (FE-10) only the eyes are seen and the pages never reflow
/// at the in-between sizes; it fades in once the expansion has finished and out as soon as a collapse starts.
/// Must be used on the UI thread.
/// </summary>
public sealed class IslandShellView : Grid, IDisposable
{
    internal static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(80);

    private readonly IslandViewModel _viewModel;
    private readonly bool _reduceMotion;
    private bool _disposed;

    public IslandShellView(EyesView eyes, SwiperView swiper, IslandViewModel viewModel, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(eyes);
        ArgumentNullException.ThrowIfNull(swiper);
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _reduceMotion = reduceMotion;
        Swiper = swiper;

        // The eyes strip is exactly the idle pill: its height minus the pill's one-pixel border on each side.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(IslandLayout.IdleHeight - 2) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(eyes, 0);
        SetRow(swiper, 1);
        Children.Add(eyes);
        Children.Add(swiper);

        // The island's recoverable error, in place under the pages; it takes no room while there is no error.
        Error = new InlineError();
        Error.SetBinding(InlineError.MessageProperty, new System.Windows.Data.Binding(nameof(IslandViewModel.ErrorMessage)) { Source = viewModel });
        Error.SetBinding(InlineError.DismissCommandProperty, new System.Windows.Data.Binding(nameof(IslandViewModel.DismissErrorCommand)) { Source = viewModel });
        SetRow(Error, 2);
        Children.Add(Error);

        // Every reusable control below reads reduced motion from here.
        Motion.SetReduceMotion(this, reduceMotion);

        Apply(viewModel.IsExpanded, animate: false);
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    public SwiperView Swiper { get; }

    public InlineError Error { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;
    }

    /// <summary>Three pages that only show their title: stand-ins until the System, Media and Converter pages exist.</summary>
    public static IReadOnlyList<FrameworkElement> CreatePlaceholderPages(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var pages = new List<FrameworkElement>();
        foreach (var title in titles)
        {
            var text = new TextBlock
            {
                Text = title,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(StyleProperty, "Lunar.Text.Title");
            pages.Add(new Border { Child = text });
        }

        return pages;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpanded) && !_disposed)
            Apply(_viewModel.IsExpanded, animate: true);
    }

    private void Apply(bool expanded, bool animate)
    {
        Swiper.IsHitTestVisible = expanded;
        var target = expanded ? 1.0 : 0.0;
        Swiper.BeginAnimation(OpacityProperty, null);
        if (!animate || _reduceMotion)
        {
            Swiper.Opacity = target;
            return;
        }

        var from = Swiper.Opacity;
        Swiper.Opacity = target;
        Swiper.BeginAnimation(OpacityProperty,
            new DoubleAnimation(from, target, expanded ? FadeIn : FadeOut) { FillBehavior = FillBehavior.Stop });
    }
}

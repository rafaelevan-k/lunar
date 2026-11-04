using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Lunar.Application.Conversion.Models;
using Lunar.Desktop.Shell;
using Lunar.Domain.Conversion;

namespace Lunar.Desktop.Features.Converter;

/// <summary>One button of a small group of exclusive choices (resolution, page size ...).</summary>
/// <param name="Group">What the group is called, for a screen reader ("Resolution").</param>
/// <param name="Label">The button text.</param>
/// <param name="IsSelected">True for the current choice.</param>
/// <param name="SelectCommand">Chooses this one.</param>
public sealed record ChoiceOption(string Group, string Label, bool IsSelected, ICommand SelectCommand)
{
    public string AutomationName => $"{Group}: {Label}{(IsSelected ? ", selected" : string.Empty)}";
}

/// <summary>A group of exclusive choices with one current value. The buttons are rebuilt on every change so the selection shows.</summary>
public sealed class ChoiceGroup<T>
    where T : notnull
{
    private readonly string _title;
    private readonly (string Label, T Value)[] _choices;
    private readonly Action _changed;
    private readonly ObservableCollection<ChoiceOption> _options = [];

    public ChoiceGroup(string title, IEnumerable<(string Label, T Value)> choices, T initial, Action changed)
    {
        _title = title;
        _choices = [.. choices];
        _changed = changed;
        Value = initial;
        Options = new ReadOnlyObservableCollection<ChoiceOption>(_options);
        Rebuild();
    }

    public T Value { get; private set; }

    public ReadOnlyObservableCollection<ChoiceOption> Options { get; }

    public void Select(T value)
    {
        if (EqualityComparer<T>.Default.Equals(Value, value))
            return;
        Value = value;
        Rebuild();
        _changed();
    }

    private void Rebuild()
    {
        _options.Clear();
        foreach (var (label, value) in _choices)
        {
            var chosen = value;
            _options.Add(new ChoiceOption(_title, label, EqualityComparer<T>.Default.Equals(value, Value), new RelayCommand(() => Select(chosen))));
        }
    }
}

/// <summary>
/// The choices that shape a conversion (FE-21, PRD section 5) and the notices about what the chosen route does to the files. Which
/// choices are on screen depends on the routes of the files that can be converted to the chosen format, so a choice that would do
/// nothing is not shown, and one that is shown is valid for every route in use:
/// - JPEG quality: to a JPEG, from a PNG or from PDF pages.
/// - Background colour: a PNG to a JPEG (transparent areas have to become a colour).
/// - Resolution and pages: PDF pages to pictures. The pages are written as "1-3, 5, 8-"; a text that cannot be read blocks starting.
/// - Page size, margin and fit: a picture put in a PDF or Word file.
/// - Password: a PDF (kept in memory only, never shown again, never written anywhere).
/// - Where the results go and what to do when a name is taken (keep both, replace, skip); replacing is explained.
/// Notices state, from the route's fidelity, what is lost or changed (lossy JPEG, pixels kept, picture inside the file, pages as
/// pictures, copy, layout of a Word file may differ and needs LibreOffice). Use on the UI thread.
/// </summary>
public sealed class ConverterOptionsViewModel : INotifyPropertyChanged
{
    private readonly Action<bool> _changed;
    private bool _showQuality;
    private bool _showBackground;
    private bool _showPdf;
    private bool _showLayout;
    private IReadOnlyList<string> _notices = [];
    private double _jpegQuality = ImageEncodingOptions.DefaultJpegQuality;
    private string _pagesText = string.Empty;
    private PageSelection _pages = PageSelection.All;
    private string? _pagesProblem;
    private string? _password;
    private string? _chosenFolder;

    /// <param name="changed">Called after any change; the argument is true when the files must be checked again (the results folder or the name policy changed).</param>
    public ConverterOptionsViewModel(Action<bool> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        _changed = changed;
        void Same() => Changed(recheck: false);
        Background = new ChoiceGroup<RgbColor>("Background for transparent areas",
            [("White", RgbColor.White), ("Light gray", new RgbColor(230, 230, 230)), ("Black", new RgbColor(0, 0, 0))], RgbColor.White, Same);
        Dpi = new ChoiceGroup<int>("Resolution",
            [("72", PdfRasterizationOptions.Dpi72), ("150", PdfRasterizationOptions.Dpi150), ("300", PdfRasterizationOptions.Dpi300)], PdfRasterizationOptions.Dpi150, Same);
        PageSizes = new ChoiceGroup<PageSize>("Page size", [("Fit the picture", PageSize.Auto), ("A4", PageSize.A4), ("Letter", PageSize.Letter)], PageSize.Auto, Same);
        Margins = new ChoiceGroup<double>("Margin", [("None", 0), ("Narrow", 18), ("Normal", 36)], 0, Same);
        Fits = new ChoiceGroup<ImageFit>("Fit", [("Whole picture", ImageFit.Fit), ("Fill the page", ImageFit.Cover)], ImageFit.Fit, Same);
        Collisions = new ChoiceGroup<CollisionPolicy>("If a result's name is taken",
            [("Keep both", CollisionPolicy.Rename), ("Replace", CollisionPolicy.Overwrite), ("Skip", CollisionPolicy.Skip)], CollisionPolicy.Rename,
            () => { Raise(nameof(ShowOverwriteWarning)); Changed(recheck: true); });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- what is on screen -------------------------------------------------------------------------------------------

    public bool ShowQuality => _showQuality;

    public bool ShowBackground => _showBackground;

    /// <summary>Resolution, pages and password: PDF pages become pictures.</summary>
    public bool ShowPdf => _showPdf;

    /// <summary>Page size, margin and fit: a picture goes into a PDF or Word file.</summary>
    public bool ShowLayout => _showLayout;

    public bool ShowAny => _showQuality || _showBackground || _showPdf || _showLayout;

    public IReadOnlyList<string> Notices => _notices;

    public bool HasNotices => _notices.Count > 0;

    // ---- the choices -------------------------------------------------------------------------------------------------

    public ChoiceGroup<RgbColor> Background { get; }

    public ChoiceGroup<int> Dpi { get; }

    public ChoiceGroup<PageSize> PageSizes { get; }

    public ChoiceGroup<double> Margins { get; }

    public ChoiceGroup<ImageFit> Fits { get; }

    public ChoiceGroup<CollisionPolicy> Collisions { get; }

    /// <summary>JPEG quality, 1 to 100 (whole numbers; anything else is rounded and held in range).</summary>
    public double JpegQuality
    {
        get => _jpegQuality;
        set
        {
            var clamped = double.IsNaN(value) ? ImageEncodingOptions.DefaultJpegQuality : Math.Clamp(Math.Round(value), ImageEncodingOptions.MinJpegQuality, ImageEncodingOptions.MaxJpegQuality);
            if (clamped == _jpegQuality)
                return;
            _jpegQuality = clamped;
            Raise();
            Raise(nameof(QualityText));
            Changed(recheck: false);
        }
    }

    public string QualityText => $"JPEG quality: {(int)_jpegQuality}";

    /// <summary>The pages to convert, as the user typed them ("1-3, 5, 8-"); empty means every page.</summary>
    public string PagesText
    {
        get => _pagesText;
        set
        {
            value ??= string.Empty;
            if (value == _pagesText)
                return;
            _pagesText = value;
            _pagesProblem = PageSelection.TryParse(value, out var parsed, out var problem) ? null : problem;
            _pages = _pagesProblem is null ? parsed : PageSelection.All;
            Raise();
            Raise(nameof(PagesProblem));
            Raise(nameof(HasPagesProblem));
            Changed(recheck: false);
        }
    }

    /// <summary>What to write instead when the pages cannot be read; null when they can. It never repeats the text.</summary>
    public string? PagesProblem => _showPdf ? _pagesProblem : null;

    public bool HasPagesProblem => PagesProblem is not null;

    public bool HasPassword => !string.IsNullOrEmpty(_password);

    /// <summary>Where the user chose to save results; null means the folder of the first file.</summary>
    public string? ChosenFolder => _chosenFolder;

    public CollisionPolicy Collision => Collisions.Value;

    public bool ShowOverwriteWarning => Collision == CollisionPolicy.Overwrite;

    /// <summary>False while a shown choice cannot be used (unreadable pages); starting must wait.</summary>
    public bool IsValid => !HasPagesProblem;

    // ---- the view and the page tell the options what happened --------------------------------------------------------

    /// <summary>The password typed for a protected PDF. Held in memory only; there is no way to read it back out of this class except into the choices.</summary>
    public void SetPdfPassword(string? password)
    {
        if (_password == password)
            return;
        _password = string.IsNullOrEmpty(password) ? null : password;
        Raise(nameof(HasPassword));
        Changed(recheck: false);
    }

    /// <summary>Raised when the password is forgotten, so the box on screen can be emptied too.</summary>
    public event EventHandler? PdfPasswordReset;

    /// <summary>Forgets the password (the list was emptied or the files were handed to the queue, which keeps its own copy in memory for a retry).</summary>
    public void ResetPdfPassword()
    {
        SetPdfPassword(null);
        PdfPasswordReset?.Invoke(this, EventArgs.Empty);
    }

    public void SetChosenFolder(string? folder)
    {
        var next = string.IsNullOrWhiteSpace(folder) ? null : folder;
        if (next == _chosenFolder)
            return;
        _chosenFolder = next;
        Raise(nameof(ChosenFolder));
        Changed(recheck: true);
    }

    /// <summary>
    /// Shows the choices and notices that fit the routes of the files that can be converted to the chosen format, and hides the rest.
    /// An empty list of routes (nothing to convert yet) shows nothing.
    /// </summary>
    public void Update(IReadOnlyList<ConversionRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var supported = routes.Where(r => r.IsSupported).ToList();
        _showQuality = supported.Any(r => r.Target is FileFormat.Jpg or FileFormat.Jpeg && r.Mode is ConversionMode.Native or ConversionMode.Rasterize);
        _showBackground = supported.Any(r => r.FlattensTransparency);
        _showPdf = supported.Any(r => r.Mode == ConversionMode.Rasterize);
        _showLayout = supported.Any(r => r.Mode == ConversionMode.Embed);
        _notices = NoticesFor(supported);
        Raise(nameof(ShowQuality));
        Raise(nameof(ShowBackground));
        Raise(nameof(ShowPdf));
        Raise(nameof(ShowLayout));
        Raise(nameof(ShowAny));
        Raise(nameof(Notices));
        Raise(nameof(HasNotices));
        Raise(nameof(PagesProblem));
        Raise(nameof(HasPagesProblem));
        Changed(recheck: false);
    }

    /// <summary>The conversion choices as set now. A choice that is not shown keeps its default, and unreadable pages mean every page (but <see cref="IsValid"/> is false then).</summary>
    public ConversionChoices BuildChoices() => new()
    {
        Image = new ImageEncodingOptions { JpegQuality = (int)_jpegQuality, Background = Background.Value },
        Layout = new PageLayoutOptions { Size = PageSizes.Value, MarginPoints = Margins.Value, Fit = Fits.Value },
        Pdf = new PdfRasterizationOptions { Dpi = Dpi.Value, Pages = _pages },
        PdfPassword = _showPdf ? _password : null
    };

    // ---- notices -----------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> NoticesFor(List<ConversionRoute> routes)
    {
        var notices = new List<string>();
        void Add(string text)
        {
            if (!notices.Contains(text))
                notices.Add(text);
        }

        foreach (var route in routes)
        {
            switch (route.Fidelity)
            {
                case ConversionFidelity.Identical:
                    Add("A copy under the new name; nothing is re-encoded.");
                    break;
                case ConversionFidelity.Lossy:
                    Add("JPEG is lossy: some detail is lost, and transparent areas are filled with the background colour. Camera details and other metadata are not copied.");
                    break;
                case ConversionFidelity.PixelPreserving:
                    Add("The picture keeps its pixels exactly, but a PNG is usually larger. Camera details and other metadata are not copied.");
                    break;
                case ConversionFidelity.Embedded:
                    Add(route.Target == FileFormat.Docx
                        ? "The picture is placed inside a Word file. No text is recognised, so there is no text to edit or search."
                        : "The picture is placed inside the PDF. No text is recognised, so there is no text to select or search.");
                    break;
                case ConversionFidelity.Rasterized:
                    Add("Each page becomes a picture, so its text can no longer be selected, and a result is written for every page.");
                    break;
                case ConversionFidelity.LayoutDependent:
                    Add("Needs LibreOffice, which you installed yourself. It lays the pages out its own way, so fonts and layout may differ from Word.");
                    break;
            }
        }

        return notices;
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------------

    private void Changed(bool recheck) => _changed(recheck);

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

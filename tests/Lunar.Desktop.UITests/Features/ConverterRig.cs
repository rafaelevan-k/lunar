using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Providers;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Converter;
using Lunar.Domain.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Lunar.Desktop.UITests.Features;

/// <summary>A renderer-availability stub: available, not available, or failing to answer.</summary>
internal sealed class StubRenderer(bool available, bool throws = false) : IRendererAvailability
{
    public Task<bool> IsAvailableAsync(CancellationToken ct) => throws ? throw new InvalidOperationException("boom") : Task.FromResult(available);
}

/// <summary>No LibreOffice on this PC (whatever the real PC has), so tests never depend on it.</summary>
internal sealed class NoLibreOffice : ILibreOfficeLocator
{
    public LibreOfficeInfo? Locate() => null;
}

/// <summary>A file manager that only records what it was asked to show.</summary>
internal sealed class RecordingRevealer : IOutputRevealer
{
    public List<string> Shown { get; } = [];

    public OperationError? Failure { get; set; }

    public Task<Result> RevealAsync(string path, CancellationToken ct)
    {
        Shown.Add(path);
        return Task.FromResult(Failure is null ? Result.Success() : Result.Failure(Failure));
    }
}

/// <summary>
/// The Converter page wired the way the application wires it: the container's real planner, queue, providers (and Windows PDF renderer)
/// and output step, with the working folder moved into a scratch folder, and fakes only for the dialogs, the file manager and the log.
/// </summary>
internal sealed class ConverterRig : IDisposable
{
    private ConverterRig(ServiceProvider services, ConverterPageViewModel page, ConverterQueueViewModel queue, IConversionQueue rawQueue, RecordingRevealer revealer, string work)
    {
        Services = services;
        Page = page;
        Queue = queue;
        RawQueue = rawQueue;
        Revealer = revealer;
        Work = work;
    }

    public ServiceProvider Services { get; }

    public ConverterPageViewModel Page { get; }

    public ConverterQueueViewModel Queue { get; }

    public IConversionQueue RawQueue { get; }

    public RecordingRevealer Revealer { get; }

    public string Work { get; }

    public static ConverterRig Create(
        string root,
        IFilePicker picker,
        IFolderPicker folders,
        IDiagnosticLog log,
        IRendererAvailability? renderer = null,
        Action<Action>? post = null)
    {
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        var revealer = new RecordingRevealer();
        var services = ServiceRegistration.BuildServiceProvider(s =>
        {
            s.AddSingleton(log);
            s.AddSingleton(renderer ?? new StubRenderer(false));
            s.AddSingleton<ConversionOrchestrator>(sp => new ConversionOrchestrator(
                work,
                new ImageConversionProvider(),
                new PdfCreationProvider(),
                new DocxEmbeddingProvider(),
                new PdfRasterizationProvider(sp.GetRequiredService<IPdfPageRasterizer>()),
                new LibreOfficeRendererProvider(new NoLibreOffice()),
                new OutputCommitter()));
        });
        var toUi = post ?? (a => a());
        var rawQueue = services.GetRequiredService<IConversionQueue>();
        var queue = new ConverterQueueViewModel(services.GetRequiredService<Lunar.Application.Conversion.UseCases.StartConversion>(), rawQueue, revealer, toUi, log);
        var page = new ConverterPageViewModel(
            services.GetRequiredService<IConversionPlanner>(), services.GetRequiredService<IRendererAvailability>(), picker, folders, queue, toUi, log);
        return new ConverterRig(services, page, queue, rawQueue, revealer, work);
    }

    public void Dispose()
    {
        Page.Dispose();
        Queue.Dispose();
        Services.Dispose();
    }
}

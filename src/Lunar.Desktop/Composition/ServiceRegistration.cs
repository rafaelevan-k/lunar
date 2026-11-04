using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Conversion.UseCases;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Shell.UseCases;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Planning;
using Lunar.Conversion.Providers;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Shell;
using Lunar.Infrastructure.Logging;
using Lunar.Infrastructure.Settings;
using Lunar.Infrastructure.Startup;
using Lunar.Windows.Audio;
using Lunar.Windows.Bluetooth;
using Lunar.Windows.Environment;
using Lunar.Windows.Media;
using Lunar.Windows.Network;
using Lunar.Windows.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace Lunar.Desktop.Composition;

/// <summary>
/// The single composition root. Concrete adapters are registered only here, by the tasks that create them.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Fixed identifier of the production single-instance scope (ADR-0008).</summary>
    internal const string InstanceName = "Lunar.Desktop.v1";

    /// <summary>Where conversions make their private working folders: under the user's own profile, whose permissions keep them private.</summary>
    internal static string DefaultConversionWorkRoot { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunar", "Temp");

    /// <summary>
    /// Builds the container with scope and dependency validation so lifetime mistakes fail at startup.
    /// OS services are singletons disposed with the returned provider; use cases are transient.
    /// Registrations passed in <paramref name="configure"/> run last and override the defaults.
    /// </summary>
    public static ServiceProvider BuildServiceProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        // Logging files are created lazily by the first resolve, not when the container is built.
        services.AddSingleton<SerilogDiagnosticLog>(_ => new SerilogDiagnosticLog(
            LoggingConfiguration.DefaultDirectory, LoggingConfiguration.IsDiagnosticsRequested()));
        services.AddSingleton<IDiagnosticLog>(sp => sp.GetRequiredService<SerilogDiagnosticLog>());
        services.AddSingleton<IInstanceCoordinator>(sp =>
            new SingleInstanceCoordinator(InstanceName, sp.GetRequiredService<IDiagnosticLog>()));

        // Presentation state is per window, so each window resolves its own state machine and view model.
        // One Windows clock serves both ports: plain local time (IClock) and time zone plus regional format (ILocaleClock).
        services.AddSingleton<WindowsClock>(sp => new WindowsClock(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IClock>(sp => sp.GetRequiredService<WindowsClock>());
        services.AddSingleton<ILocaleClock>(sp => sp.GetRequiredService<WindowsClock>());
        services.AddTransient<TransitionIsland>(sp => new TransitionIsland(sp.GetRequiredService<IClock>()));
        services.AddTransient<IslandViewModel>(sp => new IslandViewModel(sp.GetRequiredService<TransitionIsland>()));

        services.AddSingleton<ISettingsRepository>(sp => new JsonSettingsRepository(null, sp.GetRequiredService<IDiagnosticLog>()));

        services.AddSingleton<IVolumeService>(sp => new CoreAudioVolumeService(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IWifiService>(sp => new NativeWifiService(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IBluetoothService>(sp => new WindowsBluetoothService(sp.GetRequiredService<IDiagnosticLog>()));
        // Media (BE-15..BE-18): one session service, and one of each use case so their ordering gates and the user's session pick are
        // shared by everything that shows or controls media. The service starts its Windows thread only when first resolved.
        services.AddSingleton<IMediaService>(sp => new GsmtcMediaService(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<MediaSessionTracker>(sp => new MediaSessionTracker(sp.GetRequiredService<IMediaService>()));
        services.AddSingleton<MediaTransport>(sp => new MediaTransport(sp.GetRequiredService<IMediaService>()));
        services.AddSingleton<SeekMedia>(sp => new SeekMedia(sp.GetRequiredService<IMediaService>()));
        // PDF pages to pictures (BE-24): the operating system's renderer behind the Application port; the conversion layer never sees it.
        services.AddSingleton<IPdfPageRasterizer>(sp => new WindowsPdfPageRasterizer(sp.GetRequiredService<IDiagnosticLog>()));
        // Conversion (BE-27): the planner, the providers, the output step and the orchestrator that joins them, behind the queue and the use
        // case the screens call. No document layout engine is wired yet (BE-25), so Word routes are refused with "needs LibreOffice".
        services.AddSingleton<IRendererAvailability, RendererNotInstalled>();
        services.AddSingleton<IConversionPlanner>(sp => new ConversionPlanner(sp.GetRequiredService<IRendererAvailability>()));
        services.AddSingleton<ConversionOrchestrator>(sp => new ConversionOrchestrator(
            DefaultConversionWorkRoot,
            new ImageConversionProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new PdfCreationProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new DocxEmbeddingProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new PdfRasterizationProvider(sp.GetRequiredService<IPdfPageRasterizer>(), sp.GetRequiredService<IDiagnosticLog>()),
            new OutputCommitter(sp.GetRequiredService<IDiagnosticLog>()),
            sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IConversionQueue>(sp => new ConversionJobQueue(sp.GetRequiredService<ConversionOrchestrator>()));
        services.AddTransient<StartConversion>(sp => new StartConversion(sp.GetRequiredService<IConversionPlanner>(), sp.GetRequiredService<IConversionQueue>()));
        services.AddSingleton<IOutputRevealer>(sp => new WindowsOutputRevealer(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IFilePicker, WindowsFilePicker>();
        services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
        services.AddSingleton<IWindowEnvironment>(sp => new MonitorInfoProvider(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<ISystemSettingsLauncher>(sp => new WindowsSettingsLauncher(sp.GetRequiredService<IDiagnosticLog>()));

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}

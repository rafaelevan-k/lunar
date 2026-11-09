using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Conversion.UseCases;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Notes;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Shell.UseCases;
using Lunar.Application.Usage;
using Lunar.Conversion.Output;
using Lunar.Conversion.Pipeline;
using Lunar.Conversion.Planning;
using Lunar.Conversion.Providers;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Shell;
using Lunar.Infrastructure.Logging;
using Lunar.Infrastructure.Notes;
using Lunar.Infrastructure.Recording;
using Lunar.Infrastructure.Settings;
using Lunar.Infrastructure.Startup;
using Lunar.Infrastructure.Usage;
using Lunar.Windows.Audio;
using Lunar.Windows.Bluetooth;
using Lunar.Windows.Display;
using Lunar.Windows.Environment;
using Lunar.Windows.Media;
using Lunar.Windows.Network;
using Lunar.Windows.Pdf;
using Lunar.Windows.Security;
using Lunar.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Lunar.Desktop.Composition;

/// <summary>
/// The single composition root. Concrete adapters are registered only here, by the tasks that create them.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Fixed identifier of the production single-instance scope (ADR-0008).</summary>
    internal const string InstanceName = "Lunar.Desktop.v1";

    /// <summary>
    /// The hold of ADR-0005: Word documents are converted with the user's own LibreOffice, but Lunar does not promise those routes until their fidelity
    /// has been checked on a real LibreOffice. While this is false the planner refuses every Word route, whether or not LibreOffice is installed.
    /// Switching it on is this one value, after that check and with the user's approval.
    /// </summary>
    internal const bool DocxRoutesEnabled = false;

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

        // Diagnostics are chosen when the logger is built: by the environment variable or by the saved setting (ADR-0016). It is read here once
        // and shared, so the Settings window can say whether the saved choice is already in use.
        var diagnostics = LoggingConfiguration.IsDiagnosticsRequested() || DiagnosticsPreference.IsEnabled();
        services.AddSingleton(new DiagnosticsRun(diagnostics));
        // Logging files are created lazily by the first resolve, not when the container is built.
        services.AddSingleton<SerilogDiagnosticLog>(_ => new SerilogDiagnosticLog(
            LoggingConfiguration.DefaultDirectory, diagnostics));
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
        // The one writer of the settings and the announcer of changes (ADR-0016); the theme source tells when Windows changes light or dark.
        services.AddSingleton<SettingsService>(sp => new SettingsService(sp.GetRequiredService<ISettingsRepository>()));
        // Recorder (ADR-0022, BE-39/BE-40). Nothing opens a microphone until the user presses Record.
        services.AddSingleton<WindowsAudioInputCatalog>();
        services.AddSingleton<IAudioInputCatalog>(sp => sp.GetRequiredService<WindowsAudioInputCatalog>());
        services.AddSingleton<IVoiceCaptureEngine, WinRtVoiceCaptureEngine>();
        services.AddSingleton<IRecordingFiles>(_ => new RecordingFileStore());
        services.AddSingleton<RecordingService>(sp => new RecordingService(
            sp.GetRequiredService<IVoiceCaptureEngine>(),
            sp.GetRequiredService<IAudioInputCatalog>(),
            sp.GetRequiredService<IRecordingFiles>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IDiagnosticLog>()));

        // Notes (ADR-0020, BE-36): notes.json beside the settings, and the one writer of it. Nothing is read until the Notes page asks.
        services.AddSingleton<INotesRepository>(sp => new JsonNotesRepository(null, sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<NotesService>(sp => new NotesService(sp.GetRequiredService<INotesRepository>(), TimeProvider.System, sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<ISystemThemeSource>(sp => new WindowsThemeSource(sp.GetRequiredService<IDiagnosticLog>()));

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
        // case the screens call. Word routes need the user's own LibreOffice and are held off by DocxRoutesEnabled (ADR-0005).
        services.AddSingleton<ILibreOfficeLocator, LibreOfficeLocator>();
        services.AddSingleton<IRendererAvailability>(sp => new LibreOfficeAvailability(sp.GetRequiredService<ILibreOfficeLocator>(), DocxRoutesEnabled));
        services.AddSingleton<IConversionPlanner>(sp => new ConversionPlanner(sp.GetRequiredService<IRendererAvailability>()));
        services.AddSingleton<ConversionOrchestrator>(sp => new ConversionOrchestrator(
            DefaultConversionWorkRoot,
            new ImageConversionProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new PdfCreationProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new DocxEmbeddingProvider(sp.GetRequiredService<IDiagnosticLog>()),
            new PdfRasterizationProvider(sp.GetRequiredService<IPdfPageRasterizer>(), sp.GetRequiredService<IDiagnosticLog>()),
            new LibreOfficeRendererProvider(sp.GetRequiredService<ILibreOfficeLocator>(), log: sp.GetRequiredService<IDiagnosticLog>()),
            new OutputCommitter(sp.GetRequiredService<IDiagnosticLog>()),
            sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IConversionQueue>(sp => new ConversionJobQueue(sp.GetRequiredService<ConversionOrchestrator>()));
        services.AddTransient<StartConversion>(sp => new StartConversion(sp.GetRequiredService<IConversionPlanner>(), sp.GetRequiredService<IConversionQueue>()));
        // Usage monitoring (ADR-0014, BE-31, BE-33): the token the user pasted (encrypted for this Windows user) and the source that reads the
        // account limits with it. Nothing here runs or touches the network until a screen asks; it is created on first use.
        services.AddSingleton<DpapiUsageTokenStore>(sp => new DpapiUsageTokenStore(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IUsageTokenStore>(sp => sp.GetRequiredService<DpapiUsageTokenStore>());
        services.AddSingleton<IUsageCredentialSource>(sp => new ClaudeCodeCredentialSource(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IUsageSource>(sp => new AnthropicUsageSource(sp.GetRequiredService<IUsageCredentialSource>(), sp.GetRequiredService<IDiagnosticLog>()));
        // The monitor that polls the source and announces resets (BE-32). Created on first use and idle until a screen calls Start.
        services.AddSingleton<UsageMonitor>(sp => new UsageMonitor(sp.GetRequiredService<IUsageSource>()));
        services.AddSingleton<IOutputRevealer>(sp => new WindowsOutputRevealer(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<IFilePicker, WindowsFilePicker>();
        services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
        services.AddSingleton<IWindowEnvironment>(sp => new MonitorInfoProvider(sp.GetRequiredService<IDiagnosticLog>()));
        services.AddSingleton<ISystemSettingsLauncher>(sp => new WindowsSettingsLauncher(sp.GetRequiredService<IDiagnosticLog>()));
        // Every Lunar window is hidden from screen sharing, screenshots and recordings, always (ADR-0019, BE-35, FE-30).
        services.AddSingleton<ICaptureExclusion>(sp => new WindowsCaptureExclusion(sp.GetRequiredService<IDiagnosticLog>()));
        // Display brightness (ADR-0021, BE-37): built-in panels through WMI, external monitors through DDC/CI. Idle until a screen asks.
        services.AddSingleton<IBrightnessService>(sp => new WindowsBrightnessService(sp.GetRequiredService<IDiagnosticLog>()));
        // Start with Windows (BE-29): the current user's Run entry for the running executable; nothing is registered until the user turns it on.
        services.AddSingleton<IStartupRegistration>(sp => new StartupRegistrationService(
            Environment.ProcessPath ?? string.Empty, sp.GetRequiredService<IDiagnosticLog>()));

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}

/// <summary>Whether verbose diagnostics are on in this run (decided when the logger was built).</summary>
internal sealed record DiagnosticsRun(bool Enabled);

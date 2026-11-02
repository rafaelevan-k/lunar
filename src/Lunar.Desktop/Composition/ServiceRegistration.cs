using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Desktop.Shell;
using Lunar.Infrastructure.Logging;
using Lunar.Infrastructure.Settings;
using Lunar.Infrastructure.Startup;
using Lunar.Windows.Environment;
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

        // Presentation state is per window, so each window resolves its own view model.
        services.AddTransient<IslandViewModel>();

        services.AddSingleton<ISettingsRepository>(sp => new JsonSettingsRepository(null, sp.GetRequiredService<IDiagnosticLog>()));

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

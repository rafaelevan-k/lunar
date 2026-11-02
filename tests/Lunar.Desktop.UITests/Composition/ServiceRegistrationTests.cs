using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Tests.Fakes;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Shell;
using Lunar.Infrastructure.Logging;
using Lunar.Infrastructure.Settings;
using Lunar.Infrastructure.Startup;
using Lunar.Windows.Environment;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lunar.Desktop.UITests.Composition;

// Container behavior only (no WPF/UI is created); name follows the canonical UI test project.
public sealed class ServiceRegistrationTests
{
    [Fact]
    public void EmptyCompositionBuilds()
    {
        using var provider = ServiceRegistration.BuildServiceProvider();

        Assert.NotNull(provider);
    }

    [Fact]
    public void InstanceCoordinatorIsRegisteredAsOneSingletonAndOverridable()
    {
        // The log is replaced so this test never creates files in the real %LocalAppData%\Lunar\Logs.
        using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IDiagnosticLog>(new NullLog()));

        var first = provider.GetRequiredService<IInstanceCoordinator>();

        Assert.Same(first, provider.GetRequiredService<IInstanceCoordinator>());
        Assert.IsType<SingleInstanceCoordinator>(first);

        using var overridden = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IInstanceCoordinator>(_ => new SingleInstanceCoordinator("Lunar.Tests.Override")));
        Assert.NotSame(first, overridden.GetRequiredService<IInstanceCoordinator>());
    }

    [Fact]
    public void IslandViewModelIsTransientSoEachWindowGetsItsOwn()
    {
        using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IDiagnosticLog>(new NullLog()));

        var first = provider.GetRequiredService<IslandViewModel>();
        var second = provider.GetRequiredService<IslandViewModel>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void SettingsRepositoryIsRegisteredAsSingletonWithoutTouchingDisk()
    {
        using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IDiagnosticLog>(new NullLog()));

        var repository = provider.GetRequiredService<ISettingsRepository>();

        Assert.Same(repository, provider.GetRequiredService<ISettingsRepository>());
        Assert.IsType<JsonSettingsRepository>(repository); // constructing reads and writes nothing
    }

    [Fact]
    public void DisplayEnvironmentAndSettingsLauncherAreRegisteredAsSingletons()
    {
        using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IDiagnosticLog>(new NullLog()));

        var environment = provider.GetRequiredService<IWindowEnvironment>();
        var launcher = provider.GetRequiredService<ISystemSettingsLauncher>();

        Assert.Same(environment, provider.GetRequiredService<IWindowEnvironment>());
        Assert.Same(launcher, provider.GetRequiredService<ISystemSettingsLauncher>());
        Assert.IsType<MonitorInfoProvider>(environment);
        Assert.IsType<WindowsSettingsLauncher>(launcher);
        Assert.True(environment.GetDisplays().IsSuccess); // resolving does not start the listener; reading works
    }

    [Fact]
    public void DiagnosticLogIsOneSharedSingletonBackedBySerilog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lunar.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = ServiceRegistration.BuildServiceProvider(services =>
                services.AddSingleton<SerilogDiagnosticLog>(_ => new SerilogDiagnosticLog(directory, diagnosticsEnabled: false)));

            var log = provider.GetRequiredService<IDiagnosticLog>();

            Assert.Same(log, provider.GetRequiredService<IDiagnosticLog>());
            Assert.Same(log, provider.GetRequiredService<SerilogDiagnosticLog>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(DiagnosticEvent diagnosticEvent)
        {
        }
    }

    [Fact]
    public async Task PortResolvesToOneSingletonAndIsDisposedWithTheProvider()
    {
        var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<IVolumeService>(_ => new FakeVolumeService()));

        var first = provider.GetRequiredService<IVolumeService>();
        var second = provider.GetRequiredService<IVolumeService>();
        Assert.Same(first, second);

        await provider.DisposeAsync();

        Assert.True(((FakeVolumeService)first).IsDisposed);
    }

    [Fact]
    public async Task ScopedServiceIsDisposedWithItsScopeOnly()
    {
        await using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddScoped<IVolumeService>(_ => new FakeVolumeService()));

        IVolumeService inScope;
        await using (var scope = provider.CreateAsyncScope())
        {
            inScope = scope.ServiceProvider.GetRequiredService<IVolumeService>();
            Assert.Same(inScope, scope.ServiceProvider.GetRequiredService<IVolumeService>());
            Assert.False(((FakeVolumeService)inScope).IsDisposed);
        }

        Assert.True(((FakeVolumeService)inScope).IsDisposed);
        await using var other = provider.CreateAsyncScope();
        Assert.NotSame(inScope, other.ServiceProvider.GetRequiredService<IVolumeService>());
    }

    [Fact]
    public void ScopedServiceCannotBeResolvedFromTheRoot()
    {
        using var provider = ServiceRegistration.BuildServiceProvider(
            services => services.AddScoped<IVolumeService>(_ => new FakeVolumeService()));

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IVolumeService>());
    }

    [Fact]
    public void SingletonDependingOnScopedServiceFailsAtBuild()
    {
        Assert.ThrowsAny<Exception>(() => ServiceRegistration.BuildServiceProvider(services =>
        {
            services.AddScoped<IVolumeService>(_ => new FakeVolumeService());
            services.AddSingleton<VolumeConsumer>();
        }));
    }

    [Fact]
    public void MissingRegistrationFailsAtBuildNotAtFirstUse()
    {
        Assert.ThrowsAny<Exception>(() => ServiceRegistration.BuildServiceProvider(
            services => services.AddSingleton<VolumeConsumer>()));
    }

    private sealed class VolumeConsumer(IVolumeService volume)
    {
        public IVolumeService Volume { get; } = volume;
    }
}

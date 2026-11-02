using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace Lunar.Architecture.Tests;

// Linked into each canonical test project so every layer has a real fitness check.
public sealed class LayerBoundaryTests
{
    private static readonly string Layer = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "LayerUnderTest").Value!;

    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lunar.sln")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Run architecture tests from a Lunar checkout.");
        }
    }

    [Fact]
    public void DeclaredProjectReferencesRespectLayerOwnership()
    {
        var projectPath = Path.Combine(RepositoryRoot, "src", Layer, Layer + ".csproj");
        var project = XDocument.Load(projectPath);
        foreach (var reference in project.Descendants("ProjectReference"))
        {
            var include = Assert.IsType<string>(reference.Attribute("Include")?.Value);
            var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, include.Replace('\\', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(targetPath), $"Missing project reference: {include}");
            var target = Path.GetFileNameWithoutExtension(targetPath);
            Assert.True(LayerRules.AllowsProjectReference(Layer, target), $"{Layer} -> {target} is forbidden.");
        }

        if (Layer is "Lunar.Domain" or "Lunar.Application")
        {
            Assert.Empty(project.Descendants("PackageReference"));
            Assert.Empty(project.Descendants("FrameworkReference"));
        }
    }

    [Fact]
    public void CompiledAssemblyReferencesRespectLayerOwnership()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, Layer + ".dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (name.StartsWith("Lunar.", StringComparison.Ordinal))
                Assert.True(LayerRules.AllowsProjectReference(Layer, name), $"{Layer} -> {name} is forbidden.");
            if (Layer is "Lunar.Domain" or "Lunar.Application")
                Assert.True(name is "System.Runtime" or "netstandard" or "mscorlib"
                    || name.StartsWith("System.", StringComparison.Ordinal)
                    || (Layer == "Lunar.Application" && name == "Lunar.Domain"),
                    $"Non-BCL dependency {name} in {Layer}.");
        }
    }

    [Fact]
    public void CompiledTypesDoNotImportForbiddenNamespaces()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, Layer + ".dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            Assert.True(LayerRules.AllowsType(Layer, typeName), $"Forbidden type in {Layer}: {typeName}");
        }
    }

    [Fact]
    public void ProductionAssemblyContainsNoDiscoveryTypes()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, Layer + ".dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            Assert.DoesNotContain("Spike", name, StringComparison.Ordinal);
        }
    }
}

internal static class LayerRules
{
    internal static bool AllowsProjectReference(string source, string target) => source switch
    {
        "Lunar.Domain" => false,
        "Lunar.Application" => target == "Lunar.Domain",
        "Lunar.Windows" or "Lunar.Conversion" or "Lunar.Infrastructure" =>
            target is "Lunar.Application" or "Lunar.Domain",
        "Lunar.Desktop" => target is "Lunar.Application" or "Lunar.Domain"
            or "Lunar.Windows" or "Lunar.Conversion" or "Lunar.Infrastructure",
        _ => false
    };

    internal static bool AllowsType(string layer, string typeName)
    {
        if (layer == "Lunar.Desktop")
            return true; // View concerns may use WPF; adapter usage is reviewed at composition.

        // ADR-0003 permits bounded BCL streams across the PDF port, never file I/O.
        if (layer == "Lunar.Application" && typeName == "System.IO.Stream")
            return true;

        string[] forbidden = layer switch
        {
            "Lunar.Domain" or "Lunar.Application" =>
            ["System.IO", "System.Net", "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo", "System.Runtime.InteropServices",
             "System.Windows", "Windows", "Microsoft.Win32"],
            "Lunar.Conversion" or "Lunar.Infrastructure" => ["System.Windows", "Windows"],
            "Lunar.Windows" => ["System.Windows"],
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
        return !forbidden.Any(prefix => typeName == prefix
            || typeName.StartsWith(prefix + ".", StringComparison.Ordinal));
    }
}

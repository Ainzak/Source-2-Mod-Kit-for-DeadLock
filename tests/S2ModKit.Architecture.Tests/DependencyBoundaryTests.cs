using System.Reflection;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using S2ModKit.Infrastructure;

namespace S2ModKit.Architecture.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void DomainHasNoProjectOrValveDependencies()
    {
        var references = typeof(ContentHash).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.DoesNotContain("S2ModKit.Application", references);
        Assert.DoesNotContain("S2ModKit.Infrastructure", references);
        Assert.DoesNotContain("S2ModKit.Adapters.Source2", references);
        Assert.DoesNotContain("S2ModKit.Adapters.Vpk", references);
        Assert.DoesNotContain("ValveResourceFormat", references);
    }

    [Fact]
    public void ApplicationDependsOnlyOnDomainAndGeometryAmongProductionProjects()
    {
        var references = typeof(S2ModKitApplication).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).Where(name => name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true).ToArray();

        Assert.Equal(["S2ModKit.Domain", "S2ModKit.Geometry"], references);
    }

    [Fact]
    public void DomainAndApplicationPublicApisDoNotExposeValveTypes()
    {
        foreach (var assembly in new[] { typeof(ContentHash).Assembly, typeof(S2ModKitApplication).Assembly })
        {
            var leaked = assembly.ExportedTypes.SelectMany(GetPublicContractTypes)
                .FirstOrDefault(type => type.Namespace?.StartsWith("Valve", StringComparison.Ordinal) == true);
            Assert.Null(leaked);
        }
    }

    [Fact]
    public void VpkAdapterDependsOnlyOnApplicationAndDomainAmongProductionProjects()
    {
        var references = typeof(CompactVpkArchiveIO).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["S2ModKit.Application", "S2ModKit.Domain"], references);
    }

    [Fact]
    public void Source2AdapterDependsOnlyOnApplicationDomainAndGeometryAmongProductionProjects()
    {
        var references = typeof(Source2CompiledModelAdapter).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["S2ModKit.Application", "S2ModKit.Domain", "S2ModKit.Geometry"], references);
    }

    [Fact]
    public void ProjectWorkspacePersistencePortDoesNotDiscoverResources()
    {
        var publicationMethod = typeof(IProjectWorkspace).GetMethod(nameof(IProjectWorkspace.PublishProjectAsync));

        Assert.NotNull(publicationMethod);
        Assert.Contains(publicationMethod.GetParameters(), parameter => parameter.ParameterType == typeof(ProjectPublicationRequest));
        Assert.DoesNotContain(
            typeof(IProjectWorkspace).GetMethods().SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(IResourceDependencyReader)
                || parameter.ParameterType == typeof(IResourceCatalog)
                || parameter.ParameterType == typeof(ProjectCreationRequest));
    }

    [Fact]
    public void DirectoryResourceCatalogImplementsApplicationPort()
    {
        Assert.Contains(typeof(IResourceCatalog), typeof(DirectoryResourceCatalog).GetInterfaces());
        Assert.Contains(typeof(IProjectResourceSourceFactory), typeof(DirectoryProjectResourceSourceFactory).GetInterfaces());
    }

    [Fact]
    public void ValvePakIsConfinedToTheVpkAdapter()
    {
        var vpkReferences = typeof(CompactVpkArchiveIO).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        var infrastructureReferences = typeof(FileSystemProjectWorkspace).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Contains("ValvePak", vpkReferences);
        Assert.DoesNotContain("ValvePak", infrastructureReferences);
    }

    [Fact]
    public void GeometryHasNoProjectOrValveDependencies()
    {
        var references = typeof(Point3).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.DoesNotContain(references, name => name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("ValveResourceFormat", references);
        Assert.DoesNotContain("ValvePak", references);
    }

    [Fact]
    public void GeometryPublicApiDoesNotExposeFilesystemProcessOrInteropTypes()
    {
        var leaked = typeof(Point3).Assembly.ExportedTypes
            .SelectMany(GetPublicContractTypes)
            .FirstOrDefault(type => type.Namespace is string ns
                && (ns.StartsWith("System.IO", StringComparison.Ordinal)
                    || ns.StartsWith("System.Diagnostics", StringComparison.Ordinal)
                    || ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal)));

        Assert.Null(leaked);
    }

    [Fact]
    public void StructuralProfileAnalyzerDoesNotReceiveHeroCatalogueIdentity()
    {
        var analyzeMethod = typeof(IStructuralProfileAnalyzer).GetMethod(nameof(IStructuralProfileAnalyzer.AnalyzeAsync));

        Assert.NotNull(analyzeMethod);
        Assert.Equal(typeof(StructuralProfileAnalysisRequest), analyzeMethod.GetParameters()[0].ParameterType);
        Assert.Equal(
            [typeof(ArtifactContent), typeof(ModelSnapshot)],
            typeof(StructuralProfileAnalysisRequest)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray());
    }

    [Fact]
    public void ProductionCoreContainsNoFixedHeroResourcePath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var productionDirectories = new[]
        {
            "S2ModKit.Domain",
            "S2ModKit.Geometry",
            "S2ModKit.Application",
            "S2ModKit.Adapters.Source2",
        };

        var offendingFile = productionDirectories
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "src", directory),
                "*.cs",
                SearchOption.AllDirectories))
            .FirstOrDefault(path => File.ReadAllText(path).Contains("models/heroes", StringComparison.OrdinalIgnoreCase));

        Assert.Null(offendingFile);
    }

    [Theory]
    [InlineData("holliday")]
    [InlineData("astro_hat")]
    public void ProductionCoreContainsNoCoupledTransformQualificationDispatch(string qualificationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var offendingFile = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .FirstOrDefault(path => File.ReadAllText(path).Contains(qualificationToken, StringComparison.OrdinalIgnoreCase));

        Assert.Null(offendingFile);
    }

    private static IEnumerable<Type> GetPublicContractTypes(Type type)
    {
        yield return type;
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var contractType in ExpandContractType(parameter.ParameterType))
                {
                    yield return contractType;
                }
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            foreach (var contractType in ExpandContractType(property.PropertyType))
            {
                yield return contractType;
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var contractType in ExpandContractType(method.ReturnType))
            {
                yield return contractType;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var contractType in ExpandContractType(parameter.ParameterType))
                {
                    yield return contractType;
                }
            }
        }
    }

    private static IEnumerable<Type> ExpandContractType(Type type)
    {
        while (type.HasElementType)
        {
            type = type.GetElementType()!;
        }

        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var contractType in ExpandContractType(argument))
            {
                yield return contractType;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}

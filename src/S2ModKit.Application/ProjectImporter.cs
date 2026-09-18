using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed class ProjectImporter
{
    private const int MaximumDependencyArtifactCount = 4096;
    private const int MaximumDependencyDepth = 64;
    private const long MaximumDependencyBytes = 8L * 1024 * 1024 * 1024;

    private readonly IProjectWorkspace workspace;
    private readonly IResourceDependencyReader dependencyReader;
    private readonly IProjectResourceSourceFactory sourceFactory;
    private readonly IVpkProjectResourceSourceFactory? vpkSourceFactory;

    public ProjectImporter(
        IProjectWorkspace workspace,
        IResourceDependencyReader dependencyReader,
        IProjectResourceSourceFactory sourceFactory,
        IVpkProjectResourceSourceFactory? vpkSourceFactory = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.dependencyReader = dependencyReader ?? throw new ArgumentNullException(nameof(dependencyReader));
        this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        this.vpkSourceFactory = vpkSourceFactory;
    }

    public async Task<ProjectManifest> CreateProjectAsync(
        ProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var source = sourceFactory.Create(request);
        return await ImportAsync(request.ProjectRoot, request.CreatedUtc, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectManifest> CreateVpkProjectAsync(
        VpkProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (vpkSourceFactory is null)
        {
            throw Errors.Unsupported("VPK_RESOURCE_CATALOG_UNAVAILABLE", "No VPK project resource source factory is configured.", "Use the default CLI composition or configure a reviewed VPK catalog adapter.");
        }

        using var source = vpkSourceFactory.Create(request);
        return await ImportAsync(request.ProjectRoot, request.CreatedUtc, source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectManifest> ImportAsync(
        string projectRoot,
        DateTimeOffset createdUtc,
        ProjectResourceSource source,
        CancellationToken cancellationToken)
    {
        var input = await source.InputCatalog.TryOpenAsync(source.InputLogicalPath, cancellationToken).ConfigureAwait(false);
        if (input is null)
        {
            throw Errors.Input("INPUT_NOT_FOUND", $"Input resource '{source.InputLogicalPath}' was not found in '{source.InputCatalog.Descriptor.SourceIdentity}'.", "Provide an existing immutable compiled resource.");
        }

        ValidateCatalogArtifact(source.InputCatalog, input, source.InputLogicalPath);
        if (!string.Equals(source.InputCatalog.Descriptor.ProvenanceKind, "owned", StringComparison.Ordinal))
        {
            throw Errors.Input("INPUT_PROVENANCE_INVALID", "The project input must come from the owned resource catalog.", "Choose an input below the owned resource root.");
        }

        var preparedInput = PrepareArtifact(source.InputCatalog, input);
        var dependencies = new Dictionary<string, ProjectSourceArtifact>(StringComparer.Ordinal);
        var edges = new List<ProjectDependencyEdge>();
        var queuedPaths = new HashSet<string>(StringComparer.Ordinal) { preparedInput.Content.LogicalPath };
        var pending = new Queue<(ArtifactContent Artifact, int Depth)>();
        pending.Enqueue((preparedInput.Content, 0));
        long dependencyBytes = 0;

        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDependencyDepth)
            {
                throw Errors.Unsupported("RESOURCE_DEPENDENCY_DEPTH_UNSUPPORTED", $"The resource graph exceeds the supported depth of {MaximumDependencyDepth}.", "Use a bounded resource graph or add a reviewed deeper traversal profile.");
            }

            if (!dependencyReader.CanReadDependencies(current.Artifact))
            {
                throw Errors.Unsupported("DEPENDENCY_READER_CAPABILITY_UNAVAILABLE", $"No dependency reader accepts '{current.Artifact.LogicalPath}'.", "Configure a dependency reader for every owned compiled resource in the graph.");
            }

            var references = await dependencyReader.ReadDependenciesAsync(current.Artifact, cancellationToken).ConfigureAwait(false);
            if (references.Count > MaximumDependencyArtifactCount)
            {
                throw Errors.Unsupported("RESOURCE_DEPENDENCY_COUNT_UNSUPPORTED", $"Resource '{current.Artifact.LogicalPath}' declares {references.Count} direct dependencies; the supported per-resource limit is {MaximumDependencyArtifactCount}.", "Use a bounded resource graph or add a reviewed higher-capacity profile.");
            }

            foreach (var reference in references
                .OrderBy(item => item.LogicalPath, StringComparer.Ordinal)
                .ThenBy(item => item.ReferenceId, StringComparer.Ordinal))
            {
                var logicalPath = ValidateDependency(reference);
                edges.Add(new ProjectDependencyEdge
                {
                    FromLogicalPath = current.Artifact.LogicalPath,
                    ToLogicalPath = logicalPath,
                    ReferenceId = reference.ReferenceId,
                });
                if (!queuedPaths.Add(logicalPath))
                {
                    continue;
                }

                if (queuedPaths.Count - 1 > MaximumDependencyArtifactCount)
                {
                    throw Errors.Unsupported("RESOURCE_DEPENDENCY_GRAPH_SIZE_UNSUPPORTED", $"The resource graph exceeds the supported {MaximumDependencyArtifactCount} dependency artifacts.", "Use a bounded resource graph or add a reviewed higher-capacity profile.");
                }

                var dependency = await ResolveDependencyAsync(source.DependencyCatalogs, logicalPath, cancellationToken).ConfigureAwait(false);
                if (dependency is null)
                {
                    throw Errors.Input(
                        "RESOURCE_DEPENDENCY_UNRESOLVED",
                        $"Resource '{current.Artifact.LogicalPath}' references '{logicalPath}', which was not found in any configured resource catalog.",
                        "Provide the compiled dependency in the owned catalog or an explicitly classified runtime catalog.");
                }

                dependencyBytes = checked(dependencyBytes + dependency.Content.Bytes.Length);
                if (dependencyBytes > MaximumDependencyBytes)
                {
                    throw Errors.Input("RESOURCE_DEPENDENCY_BYTES_EXCEEDED", $"The dependency graph exceeds the supported total of {MaximumDependencyBytes} bytes.", "Provide a narrower resource graph or add a reviewed higher-capacity profile.");
                }

                dependencies.Add(logicalPath, dependency);
                if (string.Equals(dependency.ProvenanceKind, "owned", StringComparison.Ordinal))
                {
                    pending.Enqueue((dependency.Content, current.Depth + 1));
                }
            }
        }

        var publication = new ProjectPublicationRequest(
            projectRoot,
            createdUtc,
            preparedInput,
            dependencies.Values.OrderBy(item => item.Content.LogicalPath, StringComparer.Ordinal).ToArray(),
            edges
                .DistinctBy(edge => (edge.FromLogicalPath, edge.ToLogicalPath, edge.ReferenceId))
                .OrderBy(edge => edge.FromLogicalPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToLogicalPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.ReferenceId, StringComparer.Ordinal)
                .ToArray(),
            source.ResourceRoots,
            source.RuntimeResourceRoots);
        return await workspace.PublishProjectAsync(publication, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectSourceArtifact?> ResolveDependencyAsync(
        IReadOnlyList<IResourceCatalog> catalogs,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        foreach (var priorityGroup in catalogs
            .OrderBy(catalog => catalog.Descriptor.Priority)
            .GroupBy(catalog => catalog.Descriptor.Priority))
        {
            var matches = new List<IResourceCatalog>();
            foreach (var catalog in priorityGroup)
            {
                var match = await catalog.FindEntryAsync(logicalPath, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    ValidateCatalogEntry(catalog, match, logicalPath);
                    matches.Add(catalog);
                }
            }

            if (matches.Count == 0)
            {
                continue;
            }

            if (matches.Count > 1)
            {
                var runtimeOnly = matches.All(match => string.Equals(match.Descriptor.ProvenanceKind, "runtime_provided", StringComparison.Ordinal));
                throw Errors.Input(
                    runtimeOnly ? "RUNTIME_RESOURCE_AMBIGUOUS" : "RESOURCE_CATALOG_AMBIGUOUS",
                    $"Dependency '{logicalPath}' exists in more than one catalog at priority {priorityGroup.Key}.",
                    "Configure one catalog at that priority or resolve the duplicate by hash before import.");
            }

            var selected = matches[0];
            var artifact = await selected.TryOpenAsync(logicalPath, cancellationToken).ConfigureAwait(false)
                ?? throw Errors.Verification("RESOURCE_CATALOG_ENTRY_DISAPPEARED", $"Dependency '{logicalPath}' disappeared from catalog '{selected.Descriptor.SourceIdentity}' between lookup and read.", "Retry with an immutable resource catalog.");
            ValidateCatalogArtifact(selected, artifact, logicalPath);
            return PrepareArtifact(selected, artifact);
        }

        return null;
    }

    private static ProjectSourceArtifact PrepareArtifact(IResourceCatalog catalog, ResourceCatalogArtifact artifact) =>
        new(
            artifact.Content,
            artifact.SourceIdentity,
            catalog.Descriptor.ProvenanceKind,
            catalog.Descriptor.SourceKind,
            catalog.Descriptor.SourceIdentity,
            catalog.Descriptor.VerificationMode);

    private static void ValidateCatalogEntry(
        IResourceCatalog catalog,
        ResourceCatalogEntry entry,
        string requestedLogicalPath)
    {
        var normalizedRequestedPath = StableIdentity.NormalizePath(requestedLogicalPath);
        if (!string.Equals(entry.LogicalPath, normalizedRequestedPath, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.SourceIdentity)
            || entry.Size < 0)
        {
            throw Errors.Verification(
                "RESOURCE_CATALOG_CONTRACT_INVALID",
                $"Catalog '{catalog.Descriptor.SourceIdentity}' returned inconsistent metadata for '{normalizedRequestedPath}'.",
                "Reject this catalog implementation and inspect its logical-path and metadata behavior.");
        }
    }

    private static void ValidateCatalogArtifact(
        IResourceCatalog catalog,
        ResourceCatalogArtifact artifact,
        string requestedLogicalPath)
    {
        var normalizedRequestedPath = StableIdentity.NormalizePath(requestedLogicalPath);
        if (!string.Equals(artifact.Content.LogicalPath, normalizedRequestedPath, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(artifact.SourceIdentity)
            || artifact.Content.ContentHash != ContentHash.Compute(artifact.Content.Bytes.Span))
        {
            throw Errors.Verification(
                "RESOURCE_CATALOG_CONTRACT_INVALID",
                $"Catalog '{catalog.Descriptor.SourceIdentity}' returned inconsistent content for '{normalizedRequestedPath}'.",
                "Reject this catalog implementation and inspect its logical-path and hashing behavior.");
        }
    }

    private static string ValidateDependency(ResourceDependency dependency)
    {
        if (dependency.ReferenceId.Length != 16
            || !dependency.ReferenceId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw Errors.Unsupported("RESOURCE_REFERENCE_ID_INVALID", "A resource dependency does not have a lower-case 16-digit hexadecimal reference id.", "Use an intact RERL table with an id for every external reference.");
        }

        var logicalPath = StableIdentity.NormalizePath(dependency.LogicalPath);
        var pathSegments = logicalPath.Split('/');
        if (pathSegments.Length == 0
            || pathSegments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
        {
            throw Errors.Unsupported("RESOURCE_DEPENDENCY_PATH_INVALID", $"Dependency '{dependency.LogicalPath}' is not a safe logical resource path.", "Use an intact RERL path without drive or parent-directory segments.");
        }

        if (!logicalPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Unsupported("RESOURCE_DEPENDENCY_PATH_UNCOMPILED", $"Dependency '{logicalPath}' is not a compiled resource path.", "Resolve the reference to its compiled _c resource before project import.");
        }

        return logicalPath;
    }
}

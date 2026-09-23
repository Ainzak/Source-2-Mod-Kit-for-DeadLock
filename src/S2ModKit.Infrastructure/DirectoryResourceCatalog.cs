using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class DirectoryResourceCatalog : IResourceCatalog
{
    private const long MaxInMemoryArtifactBytes = 512L * 1024 * 1024;
    private readonly string root;

    public DirectoryResourceCatalog(string root, string provenanceKind, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceKind);
        this.root = Path.GetFullPath(root);
        Descriptor = new ResourceCatalogDescriptor("directory", this.root, provenanceKind, priority, "selected_entry_sha256");
    }

    public ResourceCatalogDescriptor Descriptor { get; }

    public Task<ResourceCatalogEntry?> FindEntryAsync(
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        var path = ResolveInside(root, normalizedPath);
        if (!File.Exists(path))
        {
            return Task.FromResult<ResourceCatalogEntry?>(null);
        }

        var size = new FileInfo(path).Length;
        if (size > MaxInMemoryArtifactBytes)
        {
            throw Errors.Input("OBJECT_SIZE_LIMIT_EXCEEDED", $"Resource '{path}' exceeds the supported limit of {MaxInMemoryArtifactBytes} bytes.", "Use a compiled resource below 512 MiB or add a reviewed streaming profile.");
        }

        return Task.FromResult<ResourceCatalogEntry?>(new ResourceCatalogEntry(normalizedPath, path, size));
    }

    public async Task<ResourceCatalogArtifact?> TryOpenAsync(
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        var path = ResolveInside(root, normalizedPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var size = new FileInfo(path).Length;
        if (size > MaxInMemoryArtifactBytes)
        {
            throw Errors.Input("OBJECT_SIZE_LIMIT_EXCEEDED", $"Resource '{path}' exceeds the supported limit of {MaxInMemoryArtifactBytes} bytes.", "Use a compiled resource below 512 MiB or add a reviewed streaming profile.");
        }

        byte[] bytes;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > MaxInMemoryArtifactBytes)
            {
                throw Errors.Input("OBJECT_SIZE_LIMIT_EXCEEDED", $"Resource '{path}' exceeds the supported limit of {MaxInMemoryArtifactBytes} bytes.", "Use a compiled resource below 512 MiB or add a reviewed streaming profile.");
            }

            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        var content = new ArtifactContent(normalizedPath, ContentHash.Compute(bytes), bytes);
        return new ResourceCatalogArtifact(content, path);
    }

    private static string NormalizeLogicalPath(string logicalPath)
    {
        var normalized = StableIdentity.NormalizePath(logicalPath);
        var segments = normalized.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
        {
            throw Errors.Input("RESOURCE_LOGICAL_PATH_INVALID", $"Resource path '{logicalPath}' is not a safe logical path.", "Use a relative Source 2 resource path without drive or parent-directory segments.");
        }

        return normalized;
    }

    private static string ResolveInside(string root, string logicalPath)
    {
        var fullRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, logicalPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("RESOURCE_LOGICAL_PATH_INVALID", $"Resource path '{logicalPath}' escapes catalog '{root}'.", "Use a relative Source 2 resource path without parent-directory segments.");
        }

        return candidate;
    }
}

public sealed class DirectoryProjectResourceSourceFactory : IProjectResourceSourceFactory
{
    private const int OwnedPriority = 0;
    private const int RuntimePriority = 100;

    public ProjectResourceSource Create(ProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputPath = Path.GetFullPath(request.InputPath);
        var resourceRoot = Path.GetFullPath(request.ResourceRoot);
        var runtimeRoots = (request.RuntimeResourceRoots ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!File.Exists(inputPath))
        {
            throw Errors.Input("INPUT_NOT_FOUND", $"Input file '{inputPath}' does not exist.", "Provide an existing immutable compiled resource.");
        }

        if (!Directory.Exists(resourceRoot))
        {
            throw Errors.Input("RESOURCE_ROOT_NOT_FOUND", $"Resource root '{resourceRoot}' does not exist.", "Provide a directory containing the input's resolvable dependencies.");
        }

        if (runtimeRoots.Any(root => string.Equals(root, resourceRoot, StringComparison.OrdinalIgnoreCase)))
        {
            throw Errors.Input("RUNTIME_RESOURCE_ROOT_DUPLICATE", "A runtime resource root duplicates the owned resource root.", "Pass each root once and classify it explicitly.");
        }

        var missingRuntimeRoot = runtimeRoots.FirstOrDefault(root => !Directory.Exists(root));
        if (missingRuntimeRoot is not null)
        {
            throw Errors.Input("RUNTIME_RESOURCE_ROOT_NOT_FOUND", $"Runtime resource root '{missingRuntimeRoot}' does not exist.", "Provide an extracted read-only base-game resource root.");
        }

        var relativeInputPath = Path.GetRelativePath(resourceRoot, inputPath);
        if (Path.IsPathRooted(relativeInputPath)
            || string.Equals(relativeInputPath, "..", StringComparison.Ordinal)
            || relativeInputPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeInputPath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw Errors.Input(
                "INPUT_OUTSIDE_RESOURCE_ROOT",
                $"Input '{inputPath}' is outside resource root '{resourceRoot}', so its Source 2 logical path cannot be derived safely.",
                "Choose a resource root that contains the input at its intended compiled-resource path.");
        }

        var ownedCatalog = new DirectoryResourceCatalog(resourceRoot, "owned", OwnedPriority);
        var runtimeCatalogs = runtimeRoots
            .Select(root => (IResourceCatalog)new DirectoryResourceCatalog(root, "runtime_provided", RuntimePriority))
            .ToArray();
        return new ProjectResourceSource(
            StableIdentity.NormalizePath(relativeInputPath),
            ownedCatalog,
            [ownedCatalog, .. runtimeCatalogs],
            [resourceRoot, .. runtimeRoots],
            runtimeRoots);
    }
}

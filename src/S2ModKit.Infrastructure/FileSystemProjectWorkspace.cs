using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace : IProjectWorkspace, IVpkPackageWorkspace
{
    private const long MaxInMemoryArtifactBytes = 512L * 1024 * 1024;
    private const long MaximumEvidenceBytes = 16L * 1024 * 1024;

    public async Task<ProjectManifest> PublishProjectAsync(
        ProjectPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectRoot = Path.GetFullPath(request.ProjectRoot);
        var manifestPath = ResolveInside(projectRoot, "project.s2mod.json");
        if (File.Exists(manifestPath))
        {
            throw Errors.Input("PROJECT_ALREADY_EXISTS", $"A project already exists at '{projectRoot}'.", "Choose a new project root or use the existing project.");
        }

        ValidatePublication(request);
        Directory.CreateDirectory(projectRoot);

        foreach (var relative in new[] { "objects/sha256", "plans", "builds", "packages", "reports", "temp" })
        {
            Directory.CreateDirectory(ResolveInside(projectRoot, relative));
        }

        var importedInput = await ImportObjectAsync(projectRoot, request.Input, cancellationToken).ConfigureAwait(false);
        var dependencies = new List<ProjectArtifactManifest>(request.Dependencies.Count);
        foreach (var dependency in request.Dependencies.OrderBy(item => item.Content.LogicalPath, StringComparer.Ordinal))
        {
            var importedDependency = await ImportObjectAsync(projectRoot, dependency, cancellationToken).ConfigureAwait(false);
            dependencies.Add(new ProjectArtifactManifest
            {
                LogicalPath = dependency.Content.LogicalPath,
                ContentHash = importedDependency.Hash,
                Size = importedDependency.Size,
                ObjectRelativePath = NormalizeRelative(Path.GetRelativePath(projectRoot, importedDependency.ObjectPath)),
                SourcePath = dependency.SourceIdentity,
                ProvenanceKind = dependency.ProvenanceKind,
                SourceKind = dependency.SourceKind,
                CatalogIdentity = dependency.CatalogIdentity,
                VerificationMode = dependency.VerificationMode,
            });
        }

        var project = new ProjectManifest
        {
            ProjectId = CreateProjectId(new DirectoryInfo(projectRoot).Name),
            CreatedUtc = request.CreatedUtc.ToUniversalTime(),
            Input = new ProjectArtifactManifest
            {
                LogicalPath = request.Input.Content.LogicalPath,
                ContentHash = importedInput.Hash,
                Size = importedInput.Size,
                ObjectRelativePath = NormalizeRelative(Path.GetRelativePath(projectRoot, importedInput.ObjectPath)),
                SourcePath = request.Input.SourceIdentity,
                ProvenanceKind = request.Input.ProvenanceKind,
                SourceKind = request.Input.SourceKind,
                CatalogIdentity = request.Input.CatalogIdentity,
                VerificationMode = request.Input.VerificationMode,
            },
            DependencyGraphComplete = true,
            Dependencies = dependencies,
            DependencyEdges = request.DependencyEdges
                .DistinctBy(edge => (edge.FromLogicalPath, edge.ToLogicalPath, edge.ReferenceId))
                .OrderBy(edge => edge.FromLogicalPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToLogicalPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.ReferenceId, StringComparer.Ordinal)
                .ToArray(),
            ResourceRoots = request.ResourceRoots,
            RuntimeResourceRoots = request.RuntimeResourceRoots,
        };

        await WriteNewAtomicAsync(manifestPath, JsonDefaults.SerializeToUtf8(project), cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<ProjectManifest> LoadProjectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var manifestPath = ResolveInside(root, "project.s2mod.json");
        if (!File.Exists(manifestPath))
        {
            throw Errors.Input("PROJECT_NOT_FOUND", $"No project manifest exists at '{root}'.", "Create a project first or provide the correct project root.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var project = JsonDefaults.Deserialize<ProjectManifest>(bytes, "Project manifest");
        var graphPaths = project.Dependencies.Select(dependency => dependency.LogicalPath)
            .Append(project.Input.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        if (project.SchemaVersion is not (1 or 2)
            || !project.DependencyGraphComplete
            || project.ResourceRoots.Count == 0
            || project.RuntimeResourceRoots.Any(root => !project.ResourceRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(project.Input.ObjectRelativePath)
            || project.Input.ProvenanceKind != "owned"
            || project.Dependencies.Any(dependency => dependency.ProvenanceKind is not ("owned" or "runtime_provided"))
            || project.Dependencies.Any(dependency => string.IsNullOrWhiteSpace(dependency.ObjectRelativePath))
            || project.Dependencies.Select(dependency => dependency.LogicalPath).Append(project.Input.LogicalPath).Distinct(StringComparer.Ordinal).Count() != project.Dependencies.Count + 1
            || project.DependencyEdges.Any(edge => !IsReferenceId(edge.ReferenceId)
                || !graphPaths.Contains(edge.FromLogicalPath)
                || !graphPaths.Contains(edge.ToLogicalPath))
            || (project.SchemaVersion == 2 && !HasSourceEvidence(project.Input))
            || (project.SchemaVersion == 2 && project.Dependencies.Any(dependency => !HasSourceEvidence(dependency))))
        {
            throw Errors.Input("PROJECT_MANIFEST_INVALID", "The project manifest does not satisfy Stage 1 invariants.", "Validate it against schemas/project.schema.json or recreate the project.");
        }

        return project;
    }

    private static bool HasSourceEvidence(ProjectArtifactManifest artifact) =>
        artifact.SourceKind is "directory" or "vpk"
        && !string.IsNullOrWhiteSpace(artifact.CatalogIdentity)
        && !string.IsNullOrWhiteSpace(artifact.VerificationMode);

    public async Task<ArtifactContent> LoadInputAsync(string projectRoot, ProjectManifest project, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var objectPath = ResolveInside(root, project.Input.ObjectRelativePath);
        return await LoadVerifiedArtifactAsync(objectPath, project.Input.LogicalPath, project.Input.ContentHash, project.Input.Size, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactContent>> LoadDependenciesAsync(
        string projectRoot,
        ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        var dependencies = new List<ArtifactContent>(project.Dependencies.Count);
        foreach (var dependency in project.Dependencies.OrderBy(item => item.LogicalPath, StringComparer.Ordinal))
        {
            var objectPath = ResolveInside(root, dependency.ObjectRelativePath);
            dependencies.Add(await LoadVerifiedArtifactAsync(
                objectPath,
                dependency.LogicalPath,
                dependency.ContentHash,
                dependency.Size,
                cancellationToken).ConfigureAwait(false));
        }

        return dependencies;
    }

    private static async Task<(ContentHash Hash, long Size, string ObjectPath)> ImportObjectAsync(
        string projectRoot,
        ProjectSourceArtifact source,
        CancellationToken cancellationToken)
    {
        var tempPath = ResolveInside(projectRoot, $"temp/import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var size = source.Content.Bytes.Length;
        try
        {
            if (size > MaxInMemoryArtifactBytes)
            {
                throw Errors.Input("OBJECT_SIZE_LIMIT_EXCEEDED", $"Resource '{source.SourceIdentity}' exceeds the Stage 1 limit of {MaxInMemoryArtifactBytes} bytes.", "Use a compiled resource below 512 MiB or add a reviewed streaming profile.");
            }

            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await destination.WriteAsync(source.Content.Bytes, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            var hash = await ComputeFileHashAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (hash != source.Content.ContentHash)
            {
                throw Errors.Verification("SOURCE_CONTENT_HASH_MISMATCH", $"Prepared resource '{source.Content.LogicalPath}' does not match its declared hash.", "Reject the source catalog output and retry import from stable bytes.");
            }

            var objectPath = ResolveInside(projectRoot, $"objects/sha256/{hash}/content");
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            if (File.Exists(objectPath))
            {
                var existingHash = await ComputeFileHashAsync(objectPath, cancellationToken).ConfigureAwait(false);
                if (existingHash != hash || new FileInfo(objectPath).Length != size)
                {
                    throw Errors.Verification("OBJECT_STORE_COLLISION", $"Existing object path for hash {hash} contains different bytes.", "Stop using this workspace and inspect storage corruption.");
                }
            }
            else
            {
                File.Move(tempPath, objectPath);
            }

            return (hash, size, objectPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ValidatePublication(ProjectPublicationRequest request)
    {
        if (request.ResourceRoots.Count == 0
            || request.Input.ProvenanceKind != "owned"
            || request.Dependencies.Any(dependency => dependency.ProvenanceKind is not ("owned" or "runtime_provided"))
            || request.RuntimeResourceRoots.Any(root => !request.ResourceRoots.Contains(root, StringComparer.OrdinalIgnoreCase)))
        {
            throw Errors.Input("PROJECT_PUBLICATION_INVALID", "The prepared project graph has invalid provenance or resource-root metadata.", "Build the graph through a configured project importer.");
        }

        var artifacts = request.Dependencies.Prepend(request.Input).ToArray();
        if (artifacts.Any(artifact => string.IsNullOrWhiteSpace(artifact.SourceIdentity)
                || artifact.Content.Bytes.Length > MaxInMemoryArtifactBytes
                || artifact.Content.ContentHash != ContentHash.Compute(artifact.Content.Bytes.Span)
                || !string.Equals(artifact.Content.LogicalPath, StableIdentity.NormalizePath(artifact.Content.LogicalPath), StringComparison.Ordinal))
            || artifacts.Select(artifact => artifact.Content.LogicalPath).Distinct(StringComparer.Ordinal).Count() != artifacts.Length)
        {
            throw Errors.Input("PROJECT_PUBLICATION_INVALID", "The prepared project graph contains invalid, duplicate, or hash-inconsistent artifacts.", "Build the graph through a configured project importer.");
        }

        var graphPaths = artifacts.Select(artifact => artifact.Content.LogicalPath).ToHashSet(StringComparer.Ordinal);
        if (request.DependencyEdges.Any(edge => !IsReferenceId(edge.ReferenceId)
            || !graphPaths.Contains(edge.FromLogicalPath)
            || !graphPaths.Contains(edge.ToLogicalPath)))
        {
            throw Errors.Input("PROJECT_PUBLICATION_INVALID", "The prepared project graph contains an invalid dependency edge.", "Build the graph through a configured project importer.");
        }
    }

    private static async Task<ArtifactContent> LoadVerifiedArtifactAsync(string path, string logicalPath, ContentHash expectedHash, long expectedSize, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw Errors.Input("OBJECT_NOT_FOUND", $"Immutable object '{expectedHash}' is missing.", "Restore the workspace object or recreate the project.");
        }

        var info = new FileInfo(path);
        if (info.Length != expectedSize || info.Length > MaxInMemoryArtifactBytes)
        {
            throw Errors.Input("OBJECT_SIZE_INVALID", $"Object size {info.Length} does not match expected {expectedSize} or exceeds the Stage 1 memory limit.", "Restore the object or use a resource below 512 MiB.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var hash = ContentHash.Compute(bytes);
        if (hash != expectedHash)
        {
            throw Errors.Verification("OBJECT_HASH_DRIFT", $"Object '{logicalPath}' no longer matches hash {expectedHash}.", "Restore the immutable object or recreate the project.");
        }

        return new ArtifactContent(StableIdentity.NormalizePath(logicalPath), hash, bytes);
    }

    private static async Task<ContentHash> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ContentHash(Convert.ToHexStringLower(bytes));
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }
    private static async Task WriteIdempotentAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(content))
            {
                throw Errors.Verification("IMMUTABLE_DOCUMENT_COLLISION", $"Immutable document '{path}' already contains different bytes.", "Investigate fingerprint generation; do not overwrite the document.");
            }

            return;
        }

        await WriteNewAtomicAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNewAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteReplaceAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("PATH_ESCAPES_WORKSPACE", $"Path '{relativePath}' escapes its workspace root.", "Use a logical path without rooted or parent-directory segments.");
        }

        return candidate;
    }

    private static string NormalizeRelative(string value) => value.Replace('\\', '/');

    private static bool IsReferenceId(string value) =>
        value.Length == 16
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CreateProjectId(string value)
    {
        var lowered = value.ToLowerInvariant();
        var result = NonIdentifierCharacterRegex().Replace(lowered, "-").Trim('-', '.', '_');
        if (result.Length == 0 || !char.IsLetterOrDigit(result[0]))
        {
            result = $"project-{ContentHash.Compute(Encoding.UTF8.GetBytes(value)).Value[..8]}";
        }

        return result[..Math.Min(64, result.Length)];
    }

    private static void EnsureSafeSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\') || value is "." or "..")
        {
            throw Errors.Input("PATH_SEGMENT_INVALID", $"The {description} is not a safe path segment.", "Use the exact identifier emitted by S2ModKit.");
        }
    }

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierCharacterRegex();
}

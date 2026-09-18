using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Adapters.Vpk;

public sealed class VpkResourceCatalog : IResourceCatalogInventory, IDisposable
{
    private const long MaxInMemoryArtifactBytes = 512L * 1024 * 1024;
    private readonly VpkCatalogSession session;
    private readonly string? allowedLogicalPath;
    private bool disposed;

    internal VpkResourceCatalog(
        VpkCatalogSession session,
        string provenanceKind,
        int priority,
        string? allowedLogicalPath = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceKind);
        this.allowedLogicalPath = allowedLogicalPath is null ? null : NormalizeLogicalPath(allowedLogicalPath);
        session.AddReference();
        Descriptor = new ResourceCatalogDescriptor("vpk", session.CatalogIdentity, provenanceKind, priority, "directory_md5_sha256_chunk_inventory_selected_crc_sha256");
    }

    public ResourceCatalogDescriptor Descriptor { get; }

    public ContentHash DirectoryHash => session.DirectoryHash;

    public ContentHash SourceContentHash => DirectoryHash;

    public string DirectoryPath => session.DirectoryPath;

    public Task<ResourceCatalogEntry?> FindEntryAsync(
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        if (allowedLogicalPath is not null
            && !string.Equals(normalizedPath, allowedLogicalPath, StringComparison.Ordinal))
        {
            return Task.FromResult<ResourceCatalogEntry?>(null);
        }

        var entry = session.FindEntry(normalizedPath);
        if (entry is null)
        {
            return Task.FromResult<ResourceCatalogEntry?>(null);
        }

        var size = (long)entry.TotalLength;
        if (size > MaxInMemoryArtifactBytes)
        {
            throw Errors.Input("OBJECT_SIZE_LIMIT_EXCEEDED", $"VPK resource '{normalizedPath}' exceeds the supported {MaxInMemoryArtifactBytes} byte in-memory limit.", "Choose a compiled resource below 512 MiB or add a reviewed streaming profile.");
        }

        return Task.FromResult<ResourceCatalogEntry?>(new ResourceCatalogEntry(
            normalizedPath,
            CreateEntrySourceIdentity(normalizedPath),
            size));
    }

    public Task<IReadOnlyList<ResourceCatalogEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var entries = session.ListEntries()
            .Where(entry => allowedLogicalPath is null
                || string.Equals(entry.LogicalPath, allowedLogicalPath, StringComparison.Ordinal))
            .Select(entry => new ResourceCatalogEntry(
                entry.LogicalPath,
                CreateEntrySourceIdentity(entry.LogicalPath),
                entry.Size))
            .ToArray();
        return Task.FromResult<IReadOnlyList<ResourceCatalogEntry>>(entries);
    }

    public async Task<ResourceCatalogArtifact?> TryOpenAsync(
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        var found = await FindEntryAsync(logicalPath, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var bytes = await session.ReadEntryAsync(found.LogicalPath, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != found.Size)
        {
            throw Errors.Verification("VPK_ENTRY_SIZE_DRIFT", $"VPK entry '{found.LogicalPath}' produced {bytes.LongLength} bytes after lookup reported {found.Size}.", "Reject the archive and retry from an immutable verified copy.");
        }

        return new ResourceCatalogArtifact(
            new ArtifactContent(found.LogicalPath, ContentHash.Compute(bytes), bytes),
            found.SourceIdentity);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        session.Release();
    }

    private string CreateEntrySourceIdentity(string logicalPath) => $"{session.DirectoryPath}::{logicalPath}";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    internal static string NormalizeLogicalPath(string logicalPath)
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
}

internal sealed class VpkCatalogSession : IDisposable
{
    private const ushort DirectoryArchiveIndex = 0x7fff;
    private readonly object referenceLock = new();
    private readonly SemaphoreSlim readLock = new(1, 1);
    private readonly Package package;
    private readonly IReadOnlyDictionary<string, PackageEntry> entries;
    private int referenceCount;
    private bool disposed;

    private VpkCatalogSession(
        string directoryPath,
        Package package,
        IReadOnlyDictionary<string, PackageEntry> entries,
        ContentHash directoryHash,
        ContentHash chunkInventoryHash)
    {
        DirectoryPath = directoryPath;
        this.package = package;
        this.entries = entries;
        DirectoryHash = directoryHash;
        ChunkInventoryHash = chunkInventoryHash;
        CatalogIdentity = $"vpk2:{directoryHash.Value}:{chunkInventoryHash.Value}";
    }

    public string DirectoryPath { get; }

    public ContentHash DirectoryHash { get; }

    public ContentHash ChunkInventoryHash { get; }

    public string CatalogIdentity { get; }

    public static VpkCatalogSession Open(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.GetFullPath(directoryPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
        if (!fileNameWithoutExtension.EndsWith("_dir", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".vpk", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input("VPK_DIRECTORY_NAME_INVALID", $"Base VPK '{fullPath}' is not named '*_dir.vpk'.", "Pass the split archive's directory VPK, not an individual numbered chunk.");
        }

        if (!File.Exists(fullPath))
        {
            throw Errors.Input("VPK_DIRECTORY_NOT_FOUND", $"Base VPK '{fullPath}' does not exist.", "Provide an existing read-only *_dir.vpk path.");
        }

        Package? package = null;
        try
        {
            package = new Package();
            package.Read(fullPath);
            if (!package.IsDirVPK || package.Version != 2)
            {
                throw Errors.Unsupported("VPK_BASE_PROFILE_UNSUPPORTED", $"Base archive '{fullPath}' is not a split VPK version 2 directory archive.", "Use a Source 2 *_dir.vpk version 2 archive.");
            }

            package.VerifyHashes();
            var normalizedEntries = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
            var packageEntries = package.Entries
                ?? throw Errors.Input("VPK_DIRECTORY_TREE_MISSING", $"Base archive '{fullPath}' did not expose a directory tree.", "Use an intact Source 2 directory VPK.");
            foreach (var entry in packageEntries.Values.SelectMany(group => group))
            {
                var logicalPath = VpkResourceCatalog.NormalizeLogicalPath(entry.GetFullPath());
                if (!normalizedEntries.TryAdd(logicalPath, entry))
                {
                    throw Errors.Input("VPK_DUPLICATE_LOGICAL_PATH", $"Base archive '{fullPath}' contains duplicate logical path '{logicalPath}'.", "Use an archive with one entry per normalized logical path.");
                }
            }

            var directoryHash = ComputeFileHash(fullPath);
            var chunkInventoryHash = ValidateAndHashChunkInventory(fullPath, normalizedEntries.Values);
            return new VpkCatalogSession(fullPath, package, normalizedEntries, directoryHash, chunkInventoryHash);
        }
        catch (S2ModKitException)
        {
            package?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
        {
            package?.Dispose();
            throw new S2ModKitException(
                new S2Error(
                    "VPK_DIRECTORY_INVALID",
                    "input",
                    $"Base VPK '{fullPath}' could not be opened and verified: {exception.Message}",
                    "Use an intact immutable Source 2 directory VPK and all referenced chunks.",
                    ErrorCategory.InputOrResolution),
                exception);
        }
    }

    public void AddReference()
    {
        lock (referenceLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            referenceCount = checked(referenceCount + 1);
        }
    }

    public void Release()
    {
        lock (referenceLock)
        {
            if (referenceCount <= 0)
            {
                return;
            }

            referenceCount--;
            if (referenceCount != 0)
            {
                return;
            }

            disposed = true;
            package.Dispose();
            readLock.Dispose();
        }
    }

    public void Dispose()
    {
        lock (referenceLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            referenceCount = 0;
            package.Dispose();
            readLock.Dispose();
        }
    }

    public PackageEntry? FindEntry(string normalizedLogicalPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return entries.GetValueOrDefault(normalizedLogicalPath);
    }

    public IReadOnlyList<(string LogicalPath, long Size)> ListEntries()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return entries
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => (item.Key, (long)item.Value.TotalLength))
            .ToArray();
    }

    public async Task<byte[]> ReadEntryAsync(string normalizedLogicalPath, CancellationToken cancellationToken)
    {
        await readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = entries.GetValueOrDefault(normalizedLogicalPath)
                ?? throw Errors.Verification("VPK_ENTRY_DISAPPEARED", $"VPK entry '{normalizedLogicalPath}' disappeared after catalog indexing.", "Retry from an immutable verified archive.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                package.ReadEntry(entry, out var bytes, validateCrc: true);
                cancellationToken.ThrowIfCancellationRequested();
                return bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
            {
                throw new S2ModKitException(
                    new S2Error(
                        "VPK_ENTRY_READ_FAILED",
                        "input",
                        $"VPK entry '{normalizedLogicalPath}' could not be read with CRC validation: {exception.Message}",
                        "Restore the referenced chunk from an intact game installation and retry.",
                        ErrorCategory.InputOrResolution),
                    exception);
            }
        }
        finally
        {
            readLock.Release();
        }
    }

    private static ContentHash ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return ContentHash.Compute(stream);
    }

    private static ContentHash ValidateAndHashChunkInventory(string directoryPath, IEnumerable<PackageEntry> entries)
    {
        var requirements = entries
            .Where(entry => entry.ArchiveIndex != DirectoryArchiveIndex)
            .GroupBy(entry => entry.ArchiveIndex)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Index = group.Key,
                RequiredLength = group.Max(entry => checked((long)entry.Offset + entry.Length)),
            })
            .ToArray();
        var prefix = Path.GetFileNameWithoutExtension(directoryPath)[..^4];
        var directory = Path.GetDirectoryName(directoryPath)
            ?? throw Errors.Input("VPK_DIRECTORY_INVALID", $"Base VPK '{directoryPath}' has no parent directory.", "Provide an absolute path to a directory VPK.");
        var inventory = new StringBuilder();
        foreach (var requirement in requirements)
        {
            var chunkName = $"{prefix}_{requirement.Index:D3}.vpk";
            var chunkPath = Path.Combine(directory, chunkName);
            if (!File.Exists(chunkPath))
            {
                throw Errors.Input("VPK_CHUNK_NOT_FOUND", $"Base VPK references missing chunk '{chunkPath}'.", "Restore every referenced numbered VPK chunk and retry.");
            }

            var actualLength = new FileInfo(chunkPath).Length;
            if (actualLength < requirement.RequiredLength)
            {
                throw Errors.Input("VPK_CHUNK_TRUNCATED", $"Chunk '{chunkPath}' has {actualLength} bytes but indexed entries require at least {requirement.RequiredLength}.", "Restore the complete numbered VPK chunk and retry.");
            }

            inventory.Append(requirement.Index)
                .Append(':')
                .Append(chunkName.ToLowerInvariant())
                .Append(':')
                .Append(actualLength)
                .Append(':')
                .Append(requirement.RequiredLength)
                .Append('\n');
        }

        return ContentHash.Compute(Encoding.UTF8.GetBytes(inventory.ToString()));
    }
}

public sealed class VpkResourceCatalogFactory : IResourceCatalogInventoryFactory
{
    public IResourceCatalogInventory OpenReadOnly(string baseVpkPath)
    {
        var session = VpkCatalogSession.Open(baseVpkPath);
        try
        {
            return new VpkResourceCatalog(session, "runtime_provided", priority: 100);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

public sealed class VpkProjectResourceSourceFactory : IVpkProjectResourceSourceFactory
{
    private const int OwnedPriority = 0;
    private const int RuntimePriority = 100;

    public ProjectResourceSource Create(VpkProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputLogicalPath = VpkResourceCatalog.NormalizeLogicalPath(request.InputLogicalPath);
        var session = VpkCatalogSession.Open(request.BaseVpkPath);
        VpkResourceCatalog? inputCatalog = null;
        VpkResourceCatalog? runtimeCatalog = null;
        try
        {
            inputCatalog = new VpkResourceCatalog(session, "owned", OwnedPriority, inputLogicalPath);
            runtimeCatalog = new VpkResourceCatalog(session, "runtime_provided", RuntimePriority);
            if (request.ExpectedDirectoryHash is { } expectedHash && inputCatalog.DirectoryHash != expectedHash)
            {
                throw Errors.Input("VPK_DIRECTORY_HASH_MISMATCH", $"Base VPK directory hash {inputCatalog.DirectoryHash} does not match expected {expectedHash}.", "Use the expected immutable game build or update the qualification input deliberately.");
            }

            return new ProjectResourceSource(
                inputLogicalPath,
                inputCatalog,
                [runtimeCatalog],
                [inputCatalog.DirectoryPath],
                [runtimeCatalog.DirectoryPath]);
        }
        catch
        {
            runtimeCatalog?.Dispose();
            inputCatalog?.Dispose();
            if (inputCatalog is null && runtimeCatalog is null)
            {
                session.Dispose();
            }

            throw;
        }
    }
}

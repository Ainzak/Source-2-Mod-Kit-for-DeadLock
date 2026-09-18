using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Vpk.Tests;

public sealed class VpkResourceCatalogTests
{
    [Fact]
    public async Task CatalogReadsDirectoryAndExternalEntriesAndHasDeterministicIdentity()
    {
        using var directory = new TestDirectory();
        var archive = SplitVpkFixture.Write(
            directory.Root,
            "pak01",
            [
                new("models/test.vmdl_c", [1, 2, 3, 4], 0),
                new("materials/base.vmat_c", [5, 6, 7], SplitVpkFixture.DirectoryArchiveIndex),
            ]);

        var factory = new VpkProjectResourceSourceFactory();
        using var first = factory.Create(new VpkProjectCreationRequest(directory.ProjectRoot, archive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch));
        using var second = factory.Create(new VpkProjectCreationRequest(directory.SecondProjectRoot, archive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch));

        var input = await first.InputCatalog.TryOpenAsync("models/test.vmdl_c", TestContext.Current.CancellationToken);
        var dependency = await first.DependencyCatalogs.Single().TryOpenAsync("materials/base.vmat_c", TestContext.Current.CancellationToken);

        Assert.NotNull(input);
        Assert.Equal([1, 2, 3, 4], input.Content.Bytes.ToArray());
        Assert.NotNull(dependency);
        Assert.Equal([5, 6, 7], dependency.Content.Bytes.ToArray());
        Assert.Equal(first.InputCatalog.Descriptor.SourceIdentity, second.InputCatalog.Descriptor.SourceIdentity);
        Assert.Equal("vpk", first.InputCatalog.Descriptor.SourceKind);
        Assert.Equal("owned", first.InputCatalog.Descriptor.ProvenanceKind);
        Assert.Equal("runtime_provided", first.DependencyCatalogs.Single().Descriptor.ProvenanceKind);
    }

    [Fact]
    public async Task InputCatalogIsEntryScopedWhileRuntimeCatalogCanResolveOtherEntries()
    {
        using var directory = new TestDirectory();
        var archive = SplitVpkFixture.Write(
            directory.Root,
            "pak01",
            [
                new("models/test.vmdl_c", [1], 0),
                new("models/other.vmdl_c", [2], 0),
            ]);
        using var source = new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(directory.ProjectRoot, archive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch));

        Assert.Null(await source.InputCatalog.FindEntryAsync("models/other.vmdl_c", TestContext.Current.CancellationToken));
        Assert.NotNull(await source.DependencyCatalogs.Single().FindEntryAsync("models/other.vmdl_c", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOnlyInventoryListsCanonicalMetadataWithoutOpeningChunkPayloads()
    {
        using var directory = new TestDirectory();
        var archive = SplitVpkFixture.Write(
            directory.Root,
            "pak01",
            [
                new("models/zeta.vmdl_c", [1, 2], 0),
                new("models/alpha.vmdl_c", [3, 4, 5], 1),
            ]);
        using var inventory = (IDisposable)new VpkResourceCatalogFactory().OpenReadOnly(archive);
        var catalog = (IResourceCatalogInventory)inventory;
        await using var firstLock = new FileStream(
            Path.Combine(directory.Root, "pak01_000.vpk"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        await using var secondLock = new FileStream(
            Path.Combine(directory.Root, "pak01_001.vpk"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var entries = await catalog.ListEntriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["models/alpha.vmdl_c", "models/zeta.vmdl_c"], entries.Select(entry => entry.LogicalPath));
        Assert.Equal([3L, 2L], entries.Select(entry => entry.Size));
        Assert.Equal(ContentHash.Compute(await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken)), catalog.SourceContentHash);
    }

    [Fact]
    public async Task ReadingOneChunkDoesNotOpenAnUnrelatedChunk()
    {
        using var directory = new TestDirectory();
        var archive = SplitVpkFixture.Write(
            directory.Root,
            "pak01",
            [
                new("models/selected.vmdl_c", [1, 2, 3], 0),
                new("models/unrelated.vmdl_c", [4, 5, 6], 1),
            ]);
        using var source = new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(directory.ProjectRoot, archive, "models/selected.vmdl_c", DateTimeOffset.UnixEpoch));
        await using var exclusiveLock = new FileStream(
            Path.Combine(directory.Root, "pak01_001.vpk"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var selected = await source.InputCatalog.TryOpenAsync("models/selected.vmdl_c", TestContext.Current.CancellationToken);

        Assert.NotNull(selected);
        Assert.Equal([1, 2, 3], selected.Content.Bytes.ToArray());
    }

    [Fact]
    public void CatalogRejectsMissingOrTruncatedReferencedChunk()
    {
        using var missingDirectory = new TestDirectory();
        var missingArchive = SplitVpkFixture.Write(
            missingDirectory.Root,
            "pak01",
            [new("models/test.vmdl_c", [1, 2, 3], 0)]);
        File.Delete(Path.Combine(missingDirectory.Root, "pak01_000.vpk"));

        var missing = Assert.Throws<S2ModKitException>(() => new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(missingDirectory.ProjectRoot, missingArchive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch)));
        Assert.Equal("VPK_CHUNK_NOT_FOUND", missing.Error.Code);

        using var truncatedDirectory = new TestDirectory();
        var truncatedArchive = SplitVpkFixture.Write(
            truncatedDirectory.Root,
            "pak01",
            [new("models/test.vmdl_c", [1, 2, 3], 0)]);
        using (var truncated = new FileStream(Path.Combine(truncatedDirectory.Root, "pak01_000.vpk"), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            truncated.SetLength(2);
        }

        var truncation = Assert.Throws<S2ModKitException>(() => new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(truncatedDirectory.ProjectRoot, truncatedArchive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch)));
        Assert.Equal("VPK_CHUNK_TRUNCATED", truncation.Error.Code);
    }

    [Fact]
    public async Task CatalogRejectsEntryCrcDriftAfterIndexing()
    {
        using var directory = new TestDirectory();
        var archive = SplitVpkFixture.Write(
            directory.Root,
            "pak01",
            [new("models/test.vmdl_c", [1, 2, 3], 0)]);
        using var source = new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(directory.ProjectRoot, archive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch));
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, "pak01_000.vpk"), [9, 2, 3], TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            source.InputCatalog.TryOpenAsync("models/test.vmdl_c", TestContext.Current.CancellationToken));

        Assert.Equal("VPK_ENTRY_READ_FAILED", exception.Error.Code);
    }

    [Fact]
    public void CatalogRejectsDuplicateOrUnsafeLogicalPathsAndExpectedHashDrift()
    {
        using var duplicateDirectory = new TestDirectory();
        var duplicateArchive = SplitVpkFixture.Write(
            duplicateDirectory.Root,
            "pak01",
            [
                new("models/test.vmdl_c", [1], 0),
                new("models/test.vmdl_c", [2], 0),
            ]);
        var duplicate = Assert.Throws<S2ModKitException>(() => new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(duplicateDirectory.ProjectRoot, duplicateArchive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch)));
        Assert.Equal("VPK_DUPLICATE_LOGICAL_PATH", duplicate.Error.Code);

        using var unsafeDirectory = new TestDirectory();
        var unsafeArchive = SplitVpkFixture.Write(
            unsafeDirectory.Root,
            "pak01",
            [new("../models/test.vmdl_c", [1], 0)]);
        var unsafePath = Assert.Throws<S2ModKitException>(() => new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(unsafeDirectory.ProjectRoot, unsafeArchive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch)));
        Assert.Equal("RESOURCE_LOGICAL_PATH_INVALID", unsafePath.Error.Code);

        using var hashDirectory = new TestDirectory();
        var hashArchive = SplitVpkFixture.Write(
            hashDirectory.Root,
            "pak01",
            [new("models/test.vmdl_c", [1], 0)]);
        var hashDrift = Assert.Throws<S2ModKitException>(() => new VpkProjectResourceSourceFactory().Create(
            new VpkProjectCreationRequest(hashDirectory.ProjectRoot, hashArchive, "models/test.vmdl_c", DateTimeOffset.UnixEpoch, ContentHash.Compute([9]))));
        Assert.Equal("VPK_DIRECTORY_HASH_MISMATCH", hashDrift.Error.Code);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "s2modkit-split-vpk-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string ProjectRoot => Path.Combine(Root, "project");

        public string SecondProjectRoot => Path.Combine(Root, "project-two");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The VPK v2 fixture must encode the format-mandated MD5 fields; test identities use SHA-256.")]
    private static class SplitVpkFixture
    {
        public const ushort DirectoryArchiveIndex = 0x7fff;
        private const uint Signature = 0x55AA1234;
        private const uint Version = 2;
        private const ushort EntryTerminator = 0xffff;
        private const int HeaderSize = 28;
        private static readonly UTF8Encoding Utf8 = new(false);

        public static string Write(string root, string prefix, IReadOnlyList<FixtureEntry> inputEntries)
        {
            var prepared = Prepare(inputEntries);
            var tree = BuildTree(prepared);
            var directoryData = prepared.Where(entry => entry.ArchiveIndex == DirectoryArchiveIndex).SelectMany(entry => entry.Bytes).ToArray();
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Signature);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)tree.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)directoryData.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), 48);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 0);

            var directoryPath = Path.Combine(root, $"{prefix}_dir.vpk");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
            {
                writer.Write(header);
                writer.Write(tree);
                writer.Write(directoryData);
                writer.Write(MD5.HashData(tree));
                writer.Write(MD5.HashData([]));
                writer.Flush();
                writer.Write(MD5.HashData(stream.ToArray()));
                writer.Flush();
                File.WriteAllBytes(directoryPath, stream.ToArray());
            }

            foreach (var chunk in prepared
                .Where(entry => entry.ArchiveIndex != DirectoryArchiveIndex)
                .GroupBy(entry => entry.ArchiveIndex))
            {
                File.WriteAllBytes(
                    Path.Combine(root, $"{prefix}_{chunk.Key:D3}.vpk"),
                    chunk.SelectMany(entry => entry.Bytes).ToArray());
            }

            return directoryPath;
        }

        private static List<PreparedEntry> Prepare(IReadOnlyList<FixtureEntry> inputEntries)
        {
            var offsets = new Dictionary<ushort, uint>();
            var prepared = new List<PreparedEntry>();
            foreach (var entry in inputEntries)
            {
                var normalized = entry.LogicalPath.Replace('\\', '/').ToLowerInvariant();
                var slash = normalized.LastIndexOf('/');
                var directory = slash < 0 ? " " : normalized[..slash];
                var file = slash < 0 ? normalized : normalized[(slash + 1)..];
                var dot = file.LastIndexOf('.');
                var extension = dot < 1 ? " " : file[(dot + 1)..];
                var name = dot < 1 ? file : file[..dot];
                var offset = offsets.GetValueOrDefault(entry.ArchiveIndex);
                prepared.Add(new PreparedEntry(extension, directory, name, entry.Bytes, entry.ArchiveIndex, offset, ComputeCrc32(entry.Bytes)));
                offsets[entry.ArchiveIndex] = checked(offset + (uint)entry.Bytes.Length);
            }

            return prepared;
        }

        private static byte[] BuildTree(IReadOnlyList<PreparedEntry> entries)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
            foreach (var extensionGroup in entries.GroupBy(entry => entry.Extension, StringComparer.Ordinal))
            {
                WriteCString(writer, extensionGroup.Key);
                foreach (var directoryGroup in extensionGroup.GroupBy(entry => entry.Directory, StringComparer.Ordinal))
                {
                    WriteCString(writer, directoryGroup.Key);
                    foreach (var entry in directoryGroup)
                    {
                        WriteCString(writer, entry.Name);
                        writer.Write(entry.Crc32);
                        writer.Write((ushort)0);
                        writer.Write(entry.ArchiveIndex);
                        writer.Write(entry.Offset);
                        writer.Write((uint)entry.Bytes.Length);
                        writer.Write(EntryTerminator);
                    }

                    WriteCString(writer, string.Empty);
                }

                WriteCString(writer, string.Empty);
            }

            WriteCString(writer, string.Empty);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteCString(BinaryWriter writer, string value)
        {
            writer.Write(Utf8.GetBytes(value));
            writer.Write((byte)0);
        }

        private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
        {
            var crc = uint.MaxValue;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 1 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ uint.MaxValue;
        }

        public sealed record FixtureEntry(string LogicalPath, byte[] Bytes, ushort ArchiveIndex);

        private sealed record PreparedEntry(
            string Extension,
            string Directory,
            string Name,
            byte[] Bytes,
            ushort ArchiveIndex,
            uint Offset,
            uint Crc32);
    }
}

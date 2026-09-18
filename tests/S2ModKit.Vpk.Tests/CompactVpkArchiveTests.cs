using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Vpk.Tests;

public sealed class CompactVpkArchiveTests
{
    [Fact]
    public async Task WriterIsDeterministicAndReaderVerifiesEveryEntry()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstPath = directory.PathOf("first.vpk");
        var secondPath = directory.PathOf("second.vpk");
        var firstEntries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            ["models/heroes/test/hero.vmdl_c"] = "model"u8.ToArray(),
            ["materials/test.vmat_c"] = "material"u8.ToArray(),
            ["README.txt"] = "readme"u8.ToArray(),
        };
        var secondEntries = firstEntries.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        await CompactVpkArchiveIO.WriteAsync(firstPath, firstEntries, cancellationToken);
        await CompactVpkArchiveIO.WriteAsync(secondPath, secondEntries, cancellationToken);
        var first = await CompactVpkArchiveIO.ReadAndVerifyAsync(firstPath, cancellationToken);
        var second = await CompactVpkArchiveIO.ReadAndVerifyAsync(secondPath, cancellationToken);

        Assert.Equal(await File.ReadAllBytesAsync(firstPath, cancellationToken), await File.ReadAllBytesAsync(secondPath, cancellationToken));
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(3, first.Entries.Count);
        Assert.Equal(ContentHash.Compute("model"u8), first.Entries.Single(entry => entry.LogicalPath.EndsWith("hero.vmdl_c", StringComparison.Ordinal)).ContentHash);
    }

    [Fact]
    public async Task CandidateBuilderReplacesOneEntryAndPreservesEveryOtherPayload()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var sourcePath = directory.PathOf("source.vpk");
        var targetPath = "models/heroes/test/hero.vmdl_c";
        var sourceBytes = "source-model"u8.ToArray();
        var replacementBytes = "replacement-model-with-different-size"u8.ToArray();
        await CompactVpkArchiveIO.WriteAsync(
            sourcePath,
            new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                [targetPath] = sourceBytes,
                ["materials/hero.vmat_c"] = "material"u8.ToArray(),
                ["textures/hero.vtex_c"] = "texture"u8.ToArray(),
            },
            cancellationToken);
        var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(sourcePath, cancellationToken);
        var builder = new DeterministicVpkCandidateBuilder();

        var candidate = await builder.BuildAsync(
            new VpkPackageBuildRequest(
                projectRoot,
                sourcePath,
                source.ContentHash,
                targetPath,
                ContentHash.Compute(sourceBytes),
                new ArtifactContent(targetPath, ContentHash.Compute(replacementBytes), replacementBytes)),
            cancellationToken);

        Assert.True(File.Exists(candidate.TemporaryPath));
        Assert.Equal(3, candidate.Comparison.SourceArchive.EntryCount);
        Assert.Equal(2, candidate.Comparison.UnchangedEntryCount);
        Assert.Equal(ContentHash.Compute(replacementBytes), candidate.Comparison.ReplacedEntry.OutputContentHash);
        Assert.Equal(source.ContentHash, (await CompactVpkArchiveIO.ReadAndVerifyAsync(sourcePath, cancellationToken)).ContentHash);
        await builder.DiscardAsync(candidate, cancellationToken);
        Assert.False(File.Exists(candidate.TemporaryPath));
    }

    [Fact]
    public async Task CandidateBuilderIsByteDeterministicAcrossRuns()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var sourcePath = directory.PathOf("source.vpk");
        const string targetPath = "models/hero.vmdl_c";
        var sourceBytes = "source"u8.ToArray();
        var replacementBytes = "replacement"u8.ToArray();
        await CompactVpkArchiveIO.WriteAsync(sourcePath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [targetPath] = sourceBytes,
            ["materials/hero.vmat_c"] = "other"u8.ToArray(),
        }, cancellationToken);
        var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(sourcePath, cancellationToken);
        var request = new VpkPackageBuildRequest(
            projectRoot,
            sourcePath,
            source.ContentHash,
            targetPath,
            ContentHash.Compute(sourceBytes),
            new ArtifactContent(targetPath, ContentHash.Compute(replacementBytes), replacementBytes));
        var builder = new DeterministicVpkCandidateBuilder();

        var first = await builder.BuildAsync(request, cancellationToken);
        var firstBytes = await File.ReadAllBytesAsync(first.TemporaryPath, cancellationToken);
        var second = await builder.BuildAsync(request, cancellationToken);
        var secondBytes = await File.ReadAllBytesAsync(second.TemporaryPath, cancellationToken);

        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(first.Comparison.OutputArchive.ContentHash, second.Comparison.OutputArchive.ContentHash);
        await builder.DiscardAsync(first, cancellationToken);
        await builder.DiscardAsync(second, cancellationToken);
    }

    [Fact]
    public async Task CandidateBuilderRefusesToDiscardAnUntrackedPath()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        var sourcePath = directory.PathOf("source.vpk");
        const string targetPath = "models/hero.vmdl_c";
        var sourceBytes = "source"u8.ToArray();
        var replacementBytes = "replacement"u8.ToArray();
        await CompactVpkArchiveIO.WriteAsync(sourcePath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [targetPath] = sourceBytes,
        }, cancellationToken);
        var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(sourcePath, cancellationToken);
        var builder = new DeterministicVpkCandidateBuilder();
        var candidate = await builder.BuildAsync(
            new VpkPackageBuildRequest(
                projectRoot,
                sourcePath,
                source.ContentHash,
                targetPath,
                ContentHash.Compute(sourceBytes),
                new ArtifactContent(targetPath, ContentHash.Compute(replacementBytes), replacementBytes)),
            cancellationToken);
        var unrelatedRoot = directory.CreateSubdirectory("unrelated");
        var unrelatedPath = Path.Combine(unrelatedRoot, "candidate.vpk");
        await File.WriteAllBytesAsync(unrelatedPath, "unrelated"u8.ToArray(), cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() =>
            builder.DiscardAsync(candidate with { TemporaryPath = unrelatedPath }, cancellationToken));

        Assert.Equal("VPK_TEMPORARY_CANDIDATE_UNTRACKED", exception.Error.Code);
        Assert.True(File.Exists(unrelatedPath));
        Assert.True(File.Exists(candidate.TemporaryPath));
        await builder.DiscardAsync(candidate, cancellationToken);
        Assert.False(File.Exists(candidate.TemporaryPath));
    }

    [Fact]
    public async Task CandidateBuilderRejectsSourceEntryDriftAndLeavesNoTemporaryRun()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectRoot = directory.CreateSubdirectory("project");
        var tempRoot = Path.Combine(projectRoot, "temp");
        Directory.CreateDirectory(tempRoot);
        var sourcePath = directory.PathOf("source.vpk");
        await CompactVpkArchiveIO.WriteAsync(sourcePath, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["models/hero.vmdl_c"] = "source"u8.ToArray(),
        }, cancellationToken);
        var source = await CompactVpkArchiveIO.ReadAndVerifyAsync(sourcePath, cancellationToken);
        var builder = new DeterministicVpkCandidateBuilder();

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => builder.BuildAsync(
            new VpkPackageBuildRequest(
                projectRoot,
                sourcePath,
                source.ContentHash,
                "models/hero.vmdl_c",
                ContentHash.Compute("different"u8),
                new ArtifactContent("models/hero.vmdl_c", ContentHash.Compute("replacement"u8), "replacement"u8.ToArray())),
            cancellationToken));

        Assert.Equal("VPK_SOURCE_ENTRY_HASH_DRIFT", exception.Error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tempRoot));
    }

    [Fact]
    public async Task ReaderRejectsTamperedArchiveChecksums()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = directory.PathOf("tampered.vpk");
        await CompactVpkArchiveIO.WriteAsync(path, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["models/hero.vmdl_c"] = "source-model"u8.ToArray(),
        }, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        bytes[^49] ^= 0x5a;
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => CompactVpkArchiveIO.ReadAndVerifyAsync(path, cancellationToken));

        Assert.Equal("VPK_MD5_MISMATCH", exception.Error.Code);
    }

    [Fact]
    public async Task ReaderRejectsExternalChunkProfileBeforePublication()
    {
        using var directory = new TestDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = directory.PathOf("external.vpk");
        await CompactVpkArchiveIO.WriteAsync(path, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["models/hero.vmdl_c"] = "source-model"u8.ToArray(),
        }, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var archiveIndex = FindSequence(bytes, [0xff, 0x7f]);
        Assert.True(archiveIndex > 28);
        bytes[archiveIndex] = 0;
        bytes[archiveIndex + 1] = 0;
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        var exception = await Assert.ThrowsAsync<S2ModKitException>(() => CompactVpkArchiveIO.ReadAndVerifyAsync(path, cancellationToken));

        Assert.Equal("VPK_ENTRY_PROFILE_UNSUPPORTED", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    private static int FindSequence(byte[] bytes, byte[] sequence)
    {
        for (var index = 0; index <= bytes.Length - sequence.Length; index++)
        {
            if (bytes.AsSpan(index, sequence.Length).SequenceEqual(sequence))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "s2modkit-vpk-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string CreateSubdirectory(string relativePath)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

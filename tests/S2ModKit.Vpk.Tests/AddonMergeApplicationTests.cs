using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Vpk.Tests;

public sealed class AddonMergeApplicationTests
{
    private const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";
    private const string Material = "models/heroes_wip/wraith/materials/body.vmat_c";
    private const string Sound = "sounds/vo/wraith/hello.vsnd_c";

    [Fact]
    public async Task DifferentResourcesMergeAndInputsStayUnchanged()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var a = await WriteAsync(directory, "pak03_dir.vpk", token, (Model, "model-a"));
        var b = await WriteAsync(directory, "pak04_dir.vpk", token, (Sound, "sound-b"));
        var before = (await HashAsync(a, token), await HashAsync(b, token));
        var output = directory.PathOf("out/merged_dir.vpk");

        var result = await Create().MergeAsync(Request([a, b], output), token);

        Assert.Equal("merged", result.Status);
        Assert.Equal(2, result.EntryCount);
        Assert.Empty(result.Conflicts);
        Assert.Equal("model-a", await ReadAsync(output, Model, token));
        Assert.Equal("sound-b", await ReadAsync(output, Sound, token));
        Assert.Equal(before, (await HashAsync(a, token), await HashAsync(b, token)));
    }

    [Fact]
    public async Task TheSameFileWithDifferentContentStopsUntilTheOwnerChooses()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var a = await WriteAsync(directory, "pak03_dir.vpk", token, (Model, "model-a"));
        var b = await WriteAsync(directory, "pak04_dir.vpk", token, (Model, "model-b"), (Sound, "sound-b"));
        var output = directory.PathOf("merged_dir.vpk");
        var keptPath = directory.PathOf("kept_dir.vpk");

        var error = await Assert.ThrowsAsync<S2ModKitException>(() => Create().MergeAsync(Request([a, b], output), token));
        var listed = await Create().MergeAsync(Request([a, b], null) with { DryRun = true }, token);
        var preferred = await Create().MergeAsync(Request([a, b], output) with { Prefer = ["pak04_dir.vpk"] }, token);
        var kept = await Create().MergeAsync(Request([a, b], keptPath) with { Keep = new Dictionary<string, string> { [Model] = "pak03_dir.vpk" } }, token);

        Assert.Equal("ADDON_MERGE_CONFLICT", error.Error.Code);
        Assert.Equal("conflicts", listed.Status);
        Assert.Null(Assert.Single(listed.Conflicts).Chosen);
        Assert.Equal("model-b", await ReadAsync(output, Model, token));
        Assert.Equal("pak04_dir.vpk", Assert.Single(preferred.Conflicts).Chosen);
        Assert.Equal("model-a", await ReadAsync(keptPath, Model, token));
        Assert.Equal("sound-b", await ReadAsync(keptPath, Sound, token));
        Assert.Equal("model", kept.Conflicts[0].Kind);
    }

    [Fact]
    public async Task IdenticalCopiesAreNotConflicts()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var a = await WriteAsync(directory, "pak03_dir.vpk", token, (Model, "same"));
        var b = await WriteAsync(directory, "pak04_dir.vpk", token, (Model, "same"), (Sound, "s"));

        var result = await Create().MergeAsync(Request([a, b], directory.PathOf("m_dir.vpk")), token);

        Assert.Equal("merged", result.Status);
        Assert.True(Assert.Single(result.Conflicts).IdenticalPayloads);
    }

    [Fact]
    public async Task ExistingOutputsUnknownNamesAndSingleInputsAreRefused()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var a = await WriteAsync(directory, "pak03_dir.vpk", token, (Model, "a"));
        var b = await WriteAsync(directory, "pak04_dir.vpk", token, (Sound, "b"));

        var exists = await Assert.ThrowsAsync<S2ModKitException>(() => Create().MergeAsync(Request([a, b], b), token));
        var unknown = await Assert.ThrowsAsync<S2ModKitException>(() => Create().MergeAsync(Request([a, b], directory.PathOf("m_dir.vpk")) with { Prefer = ["pak99_dir.vpk"] }, token));
        var single = await Assert.ThrowsAsync<S2ModKitException>(() => Create().MergeAsync(Request([a], directory.PathOf("m_dir.vpk")), token));

        Assert.Equal("ADDON_MERGE_OUTPUT_EXISTS", exists.Error.Code);
        Assert.Equal("ADDON_MERGE_UNKNOWN_INPUT", unknown.Error.Code);
        Assert.Equal("ADDON_MERGE_INPUTS", single.Error.Code);
        Assert.False(File.Exists(directory.PathOf("m_dir.vpk")));
    }

    [Fact]
    public async Task AModelUsingAMaterialAnotherInputReplacesIsReported()
    {
        using var directory = new TestDirectory();
        var token = TestContext.Current.CancellationToken;
        var a = await WriteAsync(directory, "pak03_dir.vpk", token, (Model, "model-a"));
        var b = await WriteAsync(directory, "pak04_dir.vpk", token, (Material, "material-b"));

        var result = await Create(new FixedDependencies("models/heroes_wip/wraith/materials/body.vmat")).MergeAsync(Request([a, b], null) with { DryRun = true }, token);

        var notice = Assert.Single(result.DependencyNotices);
        Assert.Equal(("pak03_dir.vpk", Material, "pak04_dir.vpk"), (notice.ModelInput, notice.ReferencedPath, notice.ReferencedInput));
        Assert.Equal("ready", result.Status);
    }

    private static AddonMergeApplication Create(IResourceDependencyReader? dependencies = null)
    {
        var archives = new ValvePakAddonArchiveContent();
        return new AddonMergeApplication(new ValvePakAddonTreeReader(), archives, archives, new AddonFileStore(), dependencies);
    }

    private static AddonMergeRequest Request(string[] inputs, string? output) =>
        new(inputs, output, [], new Dictionary<string, string>(), false);

    private static async Task<string> WriteAsync(TestDirectory directory, string name, CancellationToken token, params (string Path, string Text)[] entries)
    {
        var path = directory.PathOf("in/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await CompactVpkArchiveIO.WriteAsync(path, entries.ToDictionary(entry => entry.Path, entry => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(entry.Text)), token);
        return path;
    }

    private static async Task<string> ReadAsync(string archive, string logicalPath, CancellationToken token) =>
        System.Text.Encoding.UTF8.GetString(await new ValvePakAddonArchiveContent().ReadEntryAsync(archive, logicalPath, token));

    private static async Task<ContentHash> HashAsync(string path, CancellationToken token) =>
        ContentHash.Compute(await File.ReadAllBytesAsync(path, token));

    private sealed class FixedDependencies(string reference) : IResourceDependencyReader
    {
        public bool CanReadDependencies(ArtifactContent artifact) => true;

        public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResourceDependency>>([new ResourceDependency(reference, "1")]);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-merge-tests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(root);
        public string PathOf(string path) => Path.Combine(root, path);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

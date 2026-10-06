using NJsonSchema;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class InfluenceDiagnosticContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticSchemaAcceptsExplicitlyAdvisoryReportsAndRejectsMutationPermission(bool withField)
    {
        var hash = ContentHash.Compute([]);
        var effects = withField ? new Source2InfluenceFieldEffects(1, 0, 1, 1, 1, 0, 2, "not_assessed") : null;
        var buffer = new Source2InfluenceBuffer(0, 2, 2, hash, new(new(), new()), ["synthetic-call"], 2,
            true, true, 1, ["EXPERIMENTAL_PROCEDURAL_UNSUPPORTED"], "not_assessed_full_planner_required", effects,
            [new(0, "render", 0, "root", true, hash, 2, hash, withField ? 1 : null, withField ? 1 : null, withField ? 1 : null)]);
        var report = new Source2InfluenceDiagnosticReport(1, "source2_influence_diagnostic", "models/synthetic.vmdl_c", hash,
            new("synthetic", "test", "portable", hash, "1"), "test-1", false, "not_verified",
            withField ? "hypothetical_all_reported_buffers_positions_only" : "no_field", withField ? hash : null,
            [0], [new(0, "root", true)], [new("PHYS", 3, hash, "present_semantics_not_qualified")],
            [new(0, 0, 1, 1, "reported", null, hash, [0], [buffer])]);
        var schema = await ReadSchemaAsync();
        Assert.Empty(schema.Validate(JsonDefaults.Serialize(report)));
        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(report with { MutationPermission = true })));
        Assert.NotEmpty(schema.Validate(JsonDefaults.Serialize(report with { SchemaVersion = 99 })));
    }

    [Fact]
    public async Task ConfiguredSourceInfluencesHaveValidSchemaAndDoNotChangeInput()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_LOGICAL_PATH");
        var sha = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_CODEC");
        var codecSha = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_CODEC_SHA256");
        if (new[] { path, logical, sha, codec, codecSha }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set explicit S2MODKIT_TEST_INFLUENCE source/logical/hash/codec/hash inputs for proprietary integration.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(sha, ContentHash.Compute(bytes).Value);
        Assert.Equal(codecSha, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);
        var optionsPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_INFLUENCE_OPTIONS");
        var options = string.IsNullOrWhiteSpace(optionsPath) ? null : CoordinatedSelection.ReadOptions(await File.ReadAllBytesAsync(optionsPath, token));
        var adapter = new Source2CompiledModelAdapter(codec);
        var report = adapter.DiagnoseInfluences(new(logical!, new(sha!), bytes), options);
        Assert.False(report.MutationPermission);
        Assert.Equal("not_verified", report.DependencyClosureStatus);
        Assert.Contains(report.Meshes, m => m.Status == "reported");
        Assert.Empty((await ReadSchemaAsync()).Validate(JsonDefaults.Serialize(report)));
        Assert.Equal(sha, ContentHash.Compute(await File.ReadAllBytesAsync(path!, token)).Value);
        Assert.Throws<S2ModKitException>(() => adapter.DiagnoseInfluences(new(logical!, ContentHash.Compute([]), bytes), options));
    }

    private static async Task<JsonSchema> ReadSchemaAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "S2ModKit.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return await JsonSchema.FromFileAsync(Path.Combine(root!.FullName, "schemas", "influence-diagnostic.schema.json"),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}

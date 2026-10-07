using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
namespace S2ModKit.Source2.Tests;

public sealed class LocalPairedAuthoringTests
{
    [Fact]
    public async Task ConfiguredPairedAuthoringAndIndependentPreviewRequireCompleteSourceGeometry()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_" + name);
        var path = Env("MODEL"); var logical = Env("LOGICAL_PATH"); var planPath = Env("PLAN"); var hash = Env("SHA256");
        var codec = Env("CODEC"); var codecHash = Env("CODEC_SHA256");
        if (new[] { path, logical, planPath, hash, codec, codecHash }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set pinned S2MODKIT_TEST_PAIRED source, plan and codec variables.");
        var token = TestContext.Current.CancellationToken; var bytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(hash, ContentHash.Compute(bytes).Value); Assert.Equal(codecHash, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(bytes), bytes); var plan = MutationPlanJson.Read(await File.ReadAllBytesAsync(planPath!, token));
        var target = plan.Operations[0].PairedTransformTarget!; var adapter = new Source2CompiledModelAdapter(codec);
        var source = await adapter.ReadPairedAuthoringSourceAsync(input, target.PairedTransform.Members, token);
        Assert.Equal(input.ContentHash, source.InputHash); Assert.Equal(target.Buffers.Count, source.Buffers.Count);
        Assert.Equal(JsonDefaults.Serialize(target.ContextBuffers), JsonDefaults.Serialize(source.Context));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadDirectionalAuthoringSourceAsync(input, target.PairedTransform.Members, token));
        var geometry = await adapter.ReadPairedPreviewGeometryAsync(input, plan, token); var preview = PairedSelectionPreviewBuilder.Create(plan, geometry);
        Assert.Equal(target.Buffers.Sum(b => b.ChangedPositionCount), preview.Regions.Sum(r => r.ChangedPositions));
        Assert.Equal(2 * target.PairedTransform.Members[0].Lods.Count, preview.Regions.Count);
        Assert.All(preview.Regions, r => { Assert.True(r.ProtectedRecords > 0); Assert.True(r.ProceduralRecords > 0); Assert.True(r.MaximumDisplacement > 0); });
        var first = geometry.Buffers[0];
        foreach (var defect in new[] { "input", "plan", "missing_context", "duplicate", "topology", "position", "member" })
        {
            var changed = defect switch
            {
                "input" => geometry with { InputHash = ContentHash.Compute("wrong"u8) },
                "plan" => geometry with { PlanFingerprint = ContentHash.Compute("wrong"u8) },
                "missing_context" => geometry with { Buffers = geometry.Buffers.Where(b => b.Source.Selected).ToArray() },
                "duplicate" => geometry with { Buffers = [.. geometry.Buffers, first] },
                _ => geometry with
                {
                    Buffers = [defect switch
                {
                    "topology" => first with { TriangleIndices = [] },
                    "position" => first with { Points = [new(10000, 0, 0), .. first.Points.Skip(1)] },
                    _ => first with { MemberId = "invented" }
                }, .. geometry.Buffers.Skip(1)]
                }
            };
            Assert.Equal("PAIRED_PREVIEW_INVALID", Assert.Throws<S2ModKitException>(() => PairedSelectionPreviewBuilder.Create(plan, changed)).Error.Code);
        }
    }
}

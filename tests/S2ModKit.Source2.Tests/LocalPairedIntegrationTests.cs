using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalPairedIntegrationTests
{
    [Fact]
    public async Task ConfiguredPairedSourcePassesUnpublishedAtomicWritingAndRejectsStaleConsumers()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_LOGICAL_PATH");
        var planPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_PLAN");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_CODEC");
        var codecHash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_CODEC_SHA256");
        if (new[] { path, logical, planPath, hash, codec, codecHash }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set six S2MODKIT_TEST_PAIRED variables for a pinned immutable source, plan and codec.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token); Assert.Equal(hash, ContentHash.Compute(bytes).Value);
        Assert.Equal(codecHash, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(bytes), bytes);
        var plan = MutationPlanJson.Read(await File.ReadAllBytesAsync(planPath!, token));
        var target = plan.Operations.Single().PairedTransformTarget!;
        var reached = new List<AffineRewriteCheckpoint>();
        var adapter = new Source2CompiledModelAdapter(codec, reached.Add);
        var snapshot = await adapter.InspectAsync(input, token);
        Assert.True(adapter.CanRewrite(snapshot, plan));
        var candidate = await adapter.RewriteAsync(input, snapshot, plan, token);
        Assert.NotEqual(input.ContentHash, ContentHash.Compute(candidate.Content.Span));
        Assert.Equal(target.Buffers.Count, reached.Count(c => c == AffineRewriteCheckpoint.VertexBufferEncoded));
        Assert.Equal(target.BoxTargets.Select(b => b.ResourceBlockIndex).Distinct().Count(), reached.Count(c => c == AffineRewriteCheckpoint.MetadataSerialized));
        Assert.Equal(1, reached.Count(c => c == AffineRewriteCheckpoint.EnvelopeRebuilt));
        Assert.Contains(AffineRewriteCheckpoint.ReopenVerified, reached);
        Assert.Equal(candidate.Content.ToArray(), (await adapter.RewriteAsync(input, snapshot, plan, token)).Content.ToArray());
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);
        foreach (var point in Enum.GetValues<AffineRewriteCheckpoint>())
        {
            var injected = false; var encodings = 0;
            var failing = new Source2CompiledModelAdapter(codec, current =>
            {
                if (current == AffineRewriteCheckpoint.VertexBufferEncoded) encodings++;
                if (point != current || (point == AffineRewriteCheckpoint.VertexBufferEncoded && encodings < Math.Min(2, target.Buffers.Count))) return;
                injected = true; throw new InvalidOperationException("Injected unpublished paired failure.");
            });
            Assert.Equal("PAIRED_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token))).Error.Code);
            Assert.True(injected);
        }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelling = new Source2CompiledModelAdapter(codec, current =>
        {
            if (current == AffineRewriteCheckpoint.ReopenVerified) cancelled.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.RewriteAsync(input, snapshot, plan, cancelled.Token));
        var families = target.ProceduralInputs.ConsumerFamilies.Select(f => f.Category == "attachment" && f.Records.Count > 0
            ? f with { Records = f.Records.Select((r, i) => i == 0 ? r with { SourcePayloadHash = ContentHash.Compute("forged-input"u8), ExpectedPayloadHash = ContentHash.Compute("forged-input"u8) } : r).ToArray() } : f).ToArray();
        var altered = target with { ProceduralInputs = target.ProceduralInputs with { ConsumerFamilies = families, ConsumerInventoryHash = MutationPlanJson.ComputeDirectionalFactsHash(families) } };
        Assert.NotEqual(target.ProceduralInputs.ConsumerInventoryHash, altered.ProceduralInputs.ConsumerInventoryHash);
        altered = altered with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(altered) };
        var provisional = plan with { Operations = [plan.Operations[0] with { PairedTransformTarget = altered }] };
        var stale = provisional with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(provisional) };
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(stale));
        Assert.Equal("PAIRED_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot, stale, token))).Error.Code);
        var legacy = PairedContractValidator.Operation(plan.Operations[0]) with
        {
            Version = 9,
            Granularity = "directional_buffer_vertices",
            PairedTransform = null,
            SourceTrianglePolicy = null,
            ProceduralInputPolicy = null,
            DirectionalTransform = new(target.PairedTransform.Members, target.PairedTransform.Fields[0].Field, target.PairedTransform.Protection)
        };
        Assert.Throws<S2ModKitException>(() => ((ITransformOperationPlanner)adapter).PlanTransform(new(input, snapshot, legacy, plan.Operations[0].SelectedDrawCalls)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path!, token));
    }
}

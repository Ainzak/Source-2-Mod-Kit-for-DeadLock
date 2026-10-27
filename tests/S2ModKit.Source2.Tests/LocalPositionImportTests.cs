using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NJsonSchema;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class LocalPositionImportTests
{
    private static readonly string[] CorruptionCases = ["position", "protected_position", "frame", "hidden_attribute"];
    [Fact]
    public async Task ConfiguredExactImportNoOpDeterminismAndCorruptionRejectsWithoutChangingInputs()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("S2MODKIT_TEST_IMPORT_" + name);
        var names = new[] { "MODEL", "LOGICAL_PATH", "SHA256", "CODEC", "CODEC_SHA256", "SELECTION", "BUNDLE", "DELTA", "BUNDLE_SHA256" };
        if (names.Any(n => string.IsNullOrWhiteSpace(Env(n)))) Assert.Skip("Set nine S2MODKIT_TEST_IMPORT variables for immutable source, authoring artifacts and pinned codec.");
        var token = TestContext.Current.CancellationToken; var sourceBytes = await File.ReadAllBytesAsync(Env("MODEL")!, token);
        Assert.Equal(Env("SHA256"), ContentHash.Compute(sourceBytes).Value); Assert.Equal(Env("CODEC_SHA256"), ContentHash.Compute(await File.ReadAllBytesAsync(Env("CODEC")!, token)).Value);
        var input = new ArtifactContent(Env("LOGICAL_PATH")!, ContentHash.Compute(sourceBytes), sourceBytes); var adapter = new Source2CompiledModelAdapter(Env("CODEC"));
        var selection = PositionReturnValidator.ReadSelection(await File.ReadAllBytesAsync(Env("SELECTION")!, token));
        var assets = new PositionImportAssets(PositionAuthoringJson.Canonical(selection), await File.ReadAllBytesAsync(Env("BUNDLE")!, token), await File.ReadAllBytesAsync(Env("DELTA")!, token));
        Assert.Equal(Env("BUNDLE_SHA256"), ContentHash.Compute(assets.Bundle.Span).Value);
        var before = await adapter.InspectAsync(input, token); var plan = await Plan(assets);
        var recipe = PositionImportContract.Recipe(selection, PositionImportContract.Binding(assets));
        await Schema("v12/recipe.schema.json", JsonDefaults.SerializeToUtf8(recipe));
        var planBytes = JsonDefaults.SerializeToUtf8(plan); await Schema("v8/mutation-plan.schema.json", planBytes);
        Assert.Equal(plan.Fingerprint, MutationPlanJson.Read(planBytes).Fingerprint);
        if (Env("EVIDENCE") is { } evidenceFile)
        {
            var evidenceBytes = await File.ReadAllBytesAsync(evidenceFile, token);
            var evidence = JsonDefaults.Deserialize<EvidenceReport>(evidenceBytes, "Configured import evidence");
            await Schema("v13/evidence.schema.json", JsonDefaults.SerializeToUtf8(evidence));
            var badEvidence = evidence with { Boundaries = [] };
            Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(badEvidence));
        }
        var firstBuild = await adapter.RewritePositionImportAsync(input, before, plan, assets, token); var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(firstBuild.Content.Span), firstBuild.Content);
        var proof = await adapter.VerifyPositionImportAsync(input, output, plan, assets, token); Assert.Equal("passed", proof.Observed.StructuralStatus);
        var repeated = await adapter.RewritePositionImportAsync(input, before, plan, assets, token); Assert.Equal(firstBuild.Content.ToArray(), repeated.Content.ToArray());
        foreach (var stop in new[] { AffineRewriteCheckpoint.VertexBufferEncoded, AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.OutputReopened })
        {
            using var cancellation = new CancellationTokenSource();
            var interrupted = new Source2CompiledModelAdapter(Env("CODEC"), checkpoint => { if (checkpoint == stop) cancellation.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RewritePositionImportAsync(input, before, plan, assets, cancellation.Token));
            Assert.Equal(sourceBytes, input.Bytes.ToArray());
        }
        var t = plan.Operations[0].PositionImportTarget!; var first = t.Buffers[0];
        var mesh = firstBuild.Snapshot.Lods.Single(l => l.Level == first.Lod).Meshes.Single(m => m.MeshOrdinal == first.MeshOrdinal);
        var vb = mesh.Geometry!.VertexBuffers.Single(v => v.Ordinal == first.BufferOrdinal); var layout = vb.PositionLayout;
        using var resource = new Resource(); resource.Read(new MemoryStream(output.Bytes.ToArray()));
        var descriptor = resource.Blocks.OfType<BinaryKV3>().Single(b => b.Type.ToString() == "CTRL").Data.Root["embedded_meshes"][first.MeshOrdinal];
        var fields = descriptor["m_vertexBuffers"][first.BufferOrdinal]["m_inputLayoutFields"].Values;
        var normal = fields.Single(f => f["m_pSemanticName"].ToString(CultureInfo.InvariantCulture) == "NORMAL");
        var frameOffset = int.Parse(normal["m_nOffset"].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var envelope = ResourceEnvelopeReader.Read(output.Bytes); using var codec = NativeMeshOptimizerCodec.Open(Env("CODEC")!);
        var decoded = codec.DecodeVertexBuffer(envelope.Blocks[vb.ResourceBlockIndex].Payload.ToArray(), vb.VertexCount, vb.Stride);
        var attributeOffsets = fields
            .Where(f => f["m_pSemanticName"].ToString(CultureInfo.InvariantCulture) is "TEXCOORD" or "BLENDINDICES" or "BLENDWEIGHT")
            .ToDictionary(f => f["m_pSemanticName"].ToString(CultureInfo.InvariantCulture), f => int.Parse(f["m_nOffset"].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        Assert.Contains("TEXCOORD", attributeOffsets.Keys); Assert.Contains("BLENDINDICES", attributeOffsets.Keys);
        foreach (var defect in CorruptionCases.Concat(attributeOffsets.Keys))
        {
            var corrupt = decoded.ToArray(); var vertex = defect == "protected_position" ? selection.Protection[0].VertexIds[0] : first.FrameClosureIds[0];
            var offset = attributeOffsets.TryGetValue(defect, out var attribute) ? attribute : defect switch { "frame" => vertex * vb.Stride + frameOffset, "hidden_attribute" => Enumerable.Range(0, vb.Stride).First(i => !(i >= layout.Offset && i < layout.Offset + 12) && !(i >= frameOffset && i < frameOffset + 4)), _ => vertex * vb.Stride + layout.Offset };
            corrupt[offset] ^= 1;
            var wrong = Replace(vb.ResourceBlockIndex, codec.EncodeVertexBuffer(corrupt, vb.VertexCount, vb.Stride, 1));
            var claimed = Rehash(plan, t with { Buffers = t.Buffers.Select(b => b == first ? b with { ExpectedDecodedHash = ContentHash.Compute(corrupt) } : b).ToArray() });
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportAsync(input, wrong, claimed, assets, token));
        }
        var box = t.BoxTargets[0]; var wrongBox = box with { ExpectedWords = box.ExpectedWords.Select((w, i) => i == 0 ? BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(w) + .01f) : w).ToArray() };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportAsync(input, output, Rehash(plan, t with { BoxTargets = t.BoxTargets.Select(b => b == box ? wrongBox : b).ToArray() }), assets, token));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportAsync(input, output, Rehash(plan, t with { Buffers = t.Buffers.Select(b => b == first ? b with { FrameClosureIds = [] } : b).ToArray() }), assets, token));
        var payload = t.PreservationTargets.First(p => p.FieldPath == "$payload"); var raw = envelope.Blocks[payload.ResourceBlockIndex].Payload.ToArray(); raw[^1] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportAsync(input, Replace(payload.ResourceBlockIndex, raw), plan, assets, token));
        var stale = assets with { Delta = "{}"u8.ToArray() }; await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportAsync(input, output, plan, stale, token));
        var bundle = PositionAuthoringJson.Read<PositionAuthoringBundle>(assets.Bundle);
        var returned = PositionAuthoringJson.Read<PositionAuthoringDelta>(assets.Delta);
        var noopSelection = selection with { BoundaryFramePolicy = new("keep_positions_and_frames", 1) };
        var noop = assets with { Selection = PositionAuthoringJson.Canonical(noopSelection), Delta = PositionAuthoringJson.Canonical(returned with { Members = bundle.Members.Where(m => m.Editable).Select(m => new PositionReturnedMember(m.MemberId, m.Positions)).ToArray() }) };
        var noPlan = await Plan(noop); Assert.Empty(noPlan.Operations[0].TargetBlocks);
        var unchanged = await adapter.RewritePositionImportAsync(input, before, noPlan, noop, token); Assert.Equal(sourceBytes, unchanged.Content.ToArray());
        Assert.Equal("passed", (await adapter.VerifyPositionImportAsync(input, input, noPlan, noop, token)).Observed.StructuralStatus);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(Env("MODEL")!, token));
        async Task<MutationPlan> Plan(PositionImportAssets a)
        {
            var recipe = PositionImportContract.Recipe(PositionReturnValidator.ReadSelection(a.Selection), PositionImportContract.Binding(a)); var op = await adapter.PlanPositionImportAsync(input, before, (ImportPositionsOperation)recipe.Operations[0], a, token);
            var p = new MutationPlan(recipe.RecipeId, input.ContentHash, input.ContentHash, [op]) { SchemaVersion = 8, Inputs = [new(input.LogicalPath, input.ContentHash, input.Bytes.Length)] };
            return p with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(p) };
        }
        ArtifactContent Replace(int index, byte[] bytes) { var b = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [index] = bytes }); return new(input.LogicalPath, ContentHash.Compute(b), b); }
        async Task Schema(string name, byte[] bytes)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
            var schema = await JsonSchema.FromFileAsync(Path.Combine(root!.FullName, "schemas", name), token);
            Assert.Empty(schema.Validate(Encoding.UTF8.GetString(bytes)));
        }
    }
    private static MutationPlan Rehash(MutationPlan p, PlannedPositionImportTarget t)
    {
        t = t with { TargetFingerprint = PositionImportContract.TargetHash(t) }; p = p with { Operations = [p.Operations[0] with { PositionImportTarget = t }] }; return p with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(p) };
    }
}

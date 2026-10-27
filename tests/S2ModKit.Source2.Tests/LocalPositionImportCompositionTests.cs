using System.Text;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class LocalPositionImportCompositionTests
{
    [Fact]
    public async Task ConfiguredAtomicCompositionKeepsExactShapeAndRejectsDrawGeometryPlanAndMetadataDrift()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("S2MODKIT_TEST_IMPORT_" + name);
        var names = new[] { "MODEL", "LOGICAL_PATH", "SHA256", "CODEC", "CODEC_SHA256", "SELECTION", "BUNDLE", "DELTA", "BUNDLE_SHA256", "REMOVAL_RECIPE" };
        if (names.Any(n => string.IsNullOrWhiteSpace(Env(n)))) Assert.Skip("Set pinned import inputs and S2MODKIT_TEST_IMPORT_REMOVAL_RECIPE for the combined native check.");
        var token = TestContext.Current.CancellationToken; var sourceBytes = await File.ReadAllBytesAsync(Env("MODEL")!, token);
        Assert.Equal(Env("SHA256"), ContentHash.Compute(sourceBytes).Value); Assert.Equal(Env("CODEC_SHA256"), ContentHash.Compute(await File.ReadAllBytesAsync(Env("CODEC")!, token)).Value);
        var input = new ArtifactContent(Env("LOGICAL_PATH")!, ContentHash.Compute(sourceBytes), sourceBytes); var adapter = new Source2CompiledModelAdapter(Env("CODEC"));
        var selection = PositionReturnValidator.ReadSelection(await File.ReadAllBytesAsync(Env("SELECTION")!, token));
        var assets = new PositionImportAssets(PositionAuthoringJson.Canonical(selection), await File.ReadAllBytesAsync(Env("BUNDLE")!, token), await File.ReadAllBytesAsync(Env("DELTA")!, token));
        Assert.Equal(Env("BUNDLE_SHA256"), ContentHash.Compute(assets.Bundle.Span).Value);
        var before = await adapter.InspectAsync(input, token); var recipe = PositionImportContract.Recipe(selection, PositionImportContract.Binding(assets));
        var imported = await adapter.PlanPositionImportAsync(input, before, (ImportPositionsOperation)recipe.Operations[0], assets, token);
        var inner = new MutationPlan(recipe.RecipeId, input.ContentHash, input.ContentHash, [imported]) { SchemaVersion = 8, Inputs = [new(input.LogicalPath, input.ContentHash, input.Bytes.Length)] };
        inner = inner with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(inner) };
        var removalRecipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(Env("REMOVAL_RECIPE")!, token), "Pinned removal");
        var removal = MutationPlanner.CreatePlan(before, removalRecipe).Operations.Single();
        var draws = await adapter.PlanImportDrawProjectionAsync(input, removal, token);
        var plan = PositionImportCompositionContract.Assemble(inner, removal, draws);
        await Schema("v9/mutation-plan.schema.json", JsonDefaults.SerializeToUtf8(plan));
        var read = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan)); Assert.Equal(plan.Fingerprint, read.Fingerprint);
        var mixed = JsonNode.Parse(JsonDefaults.SerializeToUtf8(plan))!; mixed["operations"]![1]!["positionImportTarget"] = null;
        Assert.Throws<S2ModKitException>(() => MutationPlanJson.Read(Encoding.UTF8.GetBytes(mixed.ToJsonString())));
        var shape = await adapter.RewritePositionImportAsync(input, before, inner, assets, token);
        var combined = await adapter.RewritePositionImportCompositionAsync(input, before, plan, assets, token);
        var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(combined.Content.Span), combined.Content);
        var proof = await adapter.VerifyPositionImportCompositionAsync(input, output, plan, assets, token);
        Assert.Equal(shape.Content.ToArray(), proof.Projection.Content.ToArray());
        Assert.Equal("passed", proof.ImportVerification.Observed.StructuralStatus);
        Assert.True(ModelVerifier.Verify(before, combined.Snapshot, plan).IsValid);
        Assert.Equal(removal.SelectedDrawCalls.Count, before.Lods.Sum(l => l.Meshes.Sum(m => m.DrawCalls.Count)) - combined.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.DrawCalls.Count)));
        var repeated = await adapter.RewritePositionImportCompositionAsync(input, before, plan, assets, token); Assert.Equal(combined.Content.ToArray(), repeated.Content.ToArray());
        foreach (var stop in new[] { AffineRewriteCheckpoint.VertexBufferEncoded, AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.MetadataSerialized, AffineRewriteCheckpoint.EnvelopeRebuilt, AffineRewriteCheckpoint.OutputReopened })
        {
            using var cancel = new CancellationTokenSource(); var interrupted = new Source2CompiledModelAdapter(Env("CODEC"), checkpoint => { if (checkpoint == stop) cancel.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RewritePositionImportCompositionAsync(input, before, plan, assets, cancel.Token));
        }
        var forgedDraws = draws.Select((d, i) => i == 0 ? d with { EntryHash = ContentHash.Compute("forged"u8) } : d).ToArray();
        var forged = PositionImportCompositionContract.Assemble(inner, removal, forgedDraws);
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportCompositionAsync(input, output, forged, assets, token));
        var originalTarget = inner.Operations[0].PositionImportTarget!;
        var relocated = originalTarget with { PositionImport = originalTarget.PositionImport with { Binding = originalTarget.PositionImport.Binding with { DeltaArtifact = originalTarget.PositionImport.Binding.DeltaArtifact with { Path = "elsewhere/delta.json" } } } };
        Assert.Equal(PositionImportContract.TargetHash(originalTarget), PositionImportContract.TargetHash(relocated));
        Assert.Equal(inner.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(inner with { Operations = [imported with { PositionImportTarget = relocated }] }));
        var relocatedInner = inner with { Operations = [imported with { PositionImportTarget = relocated }] };
        Assert.Equal(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(plan with { Operations = [relocatedInner.Operations[0], removal], PositionImportComposition = plan.PositionImportComposition! with { ImportPlan = relocatedInner } }));
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        var padding = envelope.Blocks.First(b => b.PaddingAfter.Length > 0);
        var wrongPadding = output.Bytes.ToArray(); wrongPadding[padding.Offset + padding.Payload.Length] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportCompositionAsync(input,
            new(input.LogicalPath, ContentHash.Compute(wrongPadding), wrongPadding), plan, assets, token));
        var block = removal.TargetBlocks[0].Index;
        foreach (var defect in new[] { "extra_removal", "retained_material", "scene_bounds" })
        {
            using var resource = new Resource(); resource.Read(new MemoryStream(output.Bytes.ToArray()));
            var mesh = (Mesh)resource.Blocks[block]; var scene = mesh.Data["m_sceneObjects"][0]; var entries = scene["m_drawCalls"];
            if (defect == "extra_removal") entries.RemoveAt(0);
            if (defect == "retained_material") entries[0]["m_material"] = entries[1]["m_material"];
            if (defect == "scene_bounds") scene["m_vMinBounds"] = scene["m_vMaxBounds"];
            using var stream = new MemoryStream(); mesh.Serialize(stream);
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportCompositionAsync(input, Replace(block, stream.ToArray()), plan, assets, token));
        }
        var b = originalTarget.Buffers[0]; var vb = before.Lods.Single(l => l.Level == b.Lod).Meshes.Single(m => m.MeshOrdinal == b.MeshOrdinal).Geometry!.VertexBuffers.Single(v => v.Ordinal == b.BufferOrdinal);
        using var codec = NativeMeshOptimizerCodec.Open(Env("CODEC")!);
        var decoded = codec.DecodeVertexBuffer(envelope.Blocks[vb.ResourceBlockIndex].Payload.ToArray(), vb.VertexCount, vb.Stride);
        foreach (var offset in new[] { b.FrameClosureIds[0] * vb.Stride + vb.PositionLayout.Offset, vb.Stride - 1 })
        {
            var wrong = decoded.ToArray(); wrong[offset] ^= 1;
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportCompositionAsync(input, Replace(vb.ResourceBlockIndex, codec.EncodeVertexBuffer(wrong, vb.VertexCount, vb.Stride, 1)), plan, assets, token));
        }
        var opaque = originalTarget.PreservationTargets.First(p => p.FieldPath == "$payload"); var raw = envelope.Blocks[opaque.ResourceBlockIndex].Payload.ToArray(); raw[^1] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPositionImportCompositionAsync(input, Replace(opaque.ResourceBlockIndex, raw), plan, assets, token));
        if (Env("COMPOSITION_EVIDENCE") is { } evidencePath)
        {
            var e = JsonDefaults.Deserialize<EvidenceReport>(await File.ReadAllBytesAsync(evidencePath, token), "Combined evidence");
            await Schema("v14/evidence.schema.json", JsonDefaults.SerializeToUtf8(e));
            Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(e with { PositionImportComposition = e.PositionImportComposition! with { ProjectionHash = e.Output!.ContentHash } }));
            Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(e with { Boundaries = [] }));
            Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(e with { PositionImportComposition = e.PositionImportComposition! with { ImportEvidence = e.PositionImportComposition.ImportEvidence with { Command = "plan" } } }));
            Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(e with { Extensions = new Dictionary<string, System.Text.Json.JsonElement> { ["undeclared"] = System.Text.Json.JsonSerializer.SerializeToElement(true) } }));
        }
        var bundle = PositionAuthoringJson.Read<PositionAuthoringBundle>(assets.Bundle);
        var returned = PositionAuthoringJson.Read<PositionAuthoringDelta>(assets.Delta);
        var noopAssets = assets with
        {
            Selection = PositionAuthoringJson.Canonical(selection with { BoundaryFramePolicy = new("keep_positions_and_frames", 1) }),
            Delta = PositionAuthoringJson.Canonical(returned with { Members = bundle.Members.Where(m => m.Editable).Select(m => new PositionReturnedMember(m.MemberId, m.Positions)).ToArray() })
        };
        var noopRecipe = PositionImportContract.Recipe(PositionReturnValidator.ReadSelection(noopAssets.Selection), PositionImportContract.Binding(noopAssets));
        var noopOp = await adapter.PlanPositionImportAsync(input, before, (ImportPositionsOperation)noopRecipe.Operations[0], noopAssets, token);
        var noopInner = inner with { Operations = [noopOp] }; noopInner = noopInner with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(noopInner) };
        Assert.Empty(noopOp.TargetBlocks);
        await Schema("v8/mutation-plan.schema.json", JsonDefaults.SerializeToUtf8(noopInner));
        var noopPlan = PositionImportCompositionContract.Assemble(noopInner, removal, draws);
        var coatOnly = await adapter.RewritePositionImportCompositionAsync(input, before, noopPlan, noopAssets, token);
        var coatOnlyProof = await adapter.VerifyPositionImportCompositionAsync(input, new(input.LogicalPath, ContentHash.Compute(coatOnly.Content.Span), coatOnly.Content), noopPlan, noopAssets, token);
        Assert.Equal(sourceBytes, coatOnlyProof.Projection.Content.ToArray());
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(Env("MODEL")!, token));
        ArtifactContent Replace(int index, byte[] bytes) { var result = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [index] = bytes }); return new(input.LogicalPath, ContentHash.Compute(result), result); }
        async Task Schema(string name, byte[] bytes)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "global.json"))) d = d.Parent;
            var schema = await JsonSchema.FromFileAsync(Path.Combine(d!.FullName, "schemas", name), token); Assert.Empty(schema.Validate(Encoding.UTF8.GetString(bytes)));
        }
    }
}

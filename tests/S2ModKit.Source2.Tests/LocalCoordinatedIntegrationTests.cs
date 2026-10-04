using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalCoordinatedIntegrationTests
{
    [Fact]
    public async Task ConfiguredCoordinatedResourcePassesAtomicWriteReopenAndAdversaries()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COORDINATED_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COORDINATED_LOGICAL_PATH");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COORDINATED_RECIPE");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COORDINATED_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COORDINATED_CODEC");
        if (new[] { path, logical, recipePath, hash, codec }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set the five S2MODKIT_TEST_COORDINATED variables for an explicit immutable source and codec.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token); Assert.Equal(hash, ContentHash.Compute(bytes).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(bytes), bytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath!, token), "Coordinated recipe");
        var adapter = new Source2CompiledModelAdapter(codec);
        var snapshot = await adapter.InspectAsync(input, token);
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        Assert.Equal(5, plan.SchemaVersion); Assert.True(adapter.CanRewrite(snapshot, plan));
        Assert.Equal(JsonDefaults.Serialize(plan), JsonDefaults.Serialize(MutationPlanner.CreatePlan(snapshot, recipe, input, adapter)));
        var candidate = await adapter.RewriteAsync(input, snapshot, plan, token);
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);
        Assert.Equal(candidate.Content.ToArray(), (await adapter.RewriteAsync(input, snapshot, plan, token)).Content.ToArray());
        var output = new ArtifactContent(input.LogicalPath, candidate.Snapshot.Artifact.ContentHash, candidate.Content);
        var audit = await adapter.VerifyCoordinatedTransformAsync(input, output, plan, token);
        var t = plan.Operations[0].CoordinatedTransformTarget!;
        var sourceGeometry = await adapter.ReadCoordinatedPreviewGeometryAsync(input, plan, token);
        var preview = CoordinatedSelectionPreviewBuilder.Create(plan, sourceGeometry, snapshot);
        Assert.Equal(t.Buffers.Count, preview.Lods.Sum(l => l.Buffers.Count));
        Assert.All(preview.Lods, l => Assert.Equal(snapshot.Lods.Single(s => s.Level == l.Lod).Meshes.Sum(m => m.Geometry!.VertexBuffers.Count), l.Buffers.Count + l.Context.Count));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadCoordinatedPreviewGeometryAsync(input with { ContentHash = ContentHash.Compute("wrong"u8) }, plan, token));
        var firstPreviewLod = sourceGeometry.Lods[0];
        Assert.Throws<S2ModKitException>(() => CoordinatedSelectionPreviewBuilder.Create(plan, sourceGeometry with
        { Lods = [firstPreviewLod with { Buffers = firstPreviewLod.Buffers.Skip(1).ToArray() }, .. sourceGeometry.Lods.Skip(1)] }, snapshot));
        Assert.Equal(t.Buffers.Count, audit.Buffers.Count); Assert.Equal(t.BoxTargets.Count, audit.Boxes.Count);
        Assert.Equal(t.ZeroBoxTargets.Count, audit.ZeroBoxes.Count); Assert.Equal(t.ZeroRenderSphereTargets.Count, audit.ZeroRenderSpheres.Count);
        Assert.All(audit.ZeroBoxes, z => { Assert.Equal("passed", z.PreservationStatus); Assert.Equal("untested", z.ContainmentStatus); Assert.Equal("untested", z.ConsumerStatus); });
        Assert.All(audit.ZeroRenderSpheres, z => { Assert.Equal("passed", z.PreservationStatus); Assert.Equal("untested", z.ConsumerStatus); });
        var forgedTargets = new List<PlannedCoordinatedTransformTarget>
        {
            t with { Buffers = [t.Buffers[0] with { MaskHash = ContentHash.Compute("forged-mask"u8) }, .. t.Buffers.Skip(1)] },
            t with { Buffers = [t.Buffers[0] with { WeightHash = ContentHash.Compute("forged-weight"u8) }, .. t.Buffers.Skip(1)] },
            t with { BoxTargets = t.BoxTargets.Skip(1).ToArray() },
            t with { BoxTargets = [t.BoxTargets[0] with { ContributorSetHash = ContentHash.Compute("forged-contributors"u8) }, .. t.BoxTargets.Skip(1)] },
            t with { PreservationTargets = t.PreservationTargets.Skip(1).ToArray() },
            t with { SeamInventory = t.SeamInventory with { CompleteSourcePositionIdentity = ContentHash.Compute("forged-seams"u8) } },
        };
        if (t.ZeroBoxTargets.Count > 0)
            forgedTargets.Add(t with { ZeroBoxTargets = [t.ZeroBoxTargets[0] with { ContributorSetHash = ContentHash.Compute("forged-zero-contributor"u8) }, .. t.ZeroBoxTargets.Skip(1)] });
        if (t.ZeroRenderSphereTargets.Count > 0)
            forgedTargets.Add(t with { ZeroRenderSphereTargets = [t.ZeroRenderSphereTargets[0] with { Storage = t.ZeroRenderSphereTargets[0].Storage == "binary64" ? "binary32" : "binary64" }, .. t.ZeroRenderSphereTargets.Skip(1)] });
        foreach (var forged in forgedTargets)
        {
            var altered = Rehash(plan, forged); _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(altered));
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyCoordinatedTransformAsync(input, output, altered, token));
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot, altered, token));
        }
        await RejectAlteredMember(adapter, codec!, input, output, plan, token);
        await RejectAlteredZeroFields(adapter, input, output, plan, token);
        foreach (var checkpoint in new[] { AffineRewriteCheckpoint.PositionsTransformed, AffineRewriteCheckpoint.PackedFramesTransformed, AffineRewriteCheckpoint.VertexBufferEncoded,
            AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.MetadataSerialized, AffineRewriteCheckpoint.EnvelopeRebuilt,
            AffineRewriteCheckpoint.OutputReopened, AffineRewriteCheckpoint.ReopenVerified })
        {
            var reached = false;
            var failing = new Source2CompiledModelAdapter(codec, point =>
            {
                if (point != checkpoint) return;
                reached = true; throw new InvalidOperationException("injected coordinated failure");
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token));
            Assert.True(reached);
        }
        foreach (var checkpoint in new[] { AffineRewriteCheckpoint.VertexBufferEncoded, AffineRewriteCheckpoint.MetadataSerialized })
        {
            var completed = 0;
            var failing = new Source2CompiledModelAdapter(codec, point =>
            {
                if (point == checkpoint && ++completed == 2) throw new InvalidOperationException("injected failure after an earlier member or MDAT completed");
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token));
            Assert.Equal(2, completed);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path!, token));
    }

    private static MutationPlan Rehash(MutationPlan plan, PlannedCoordinatedTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { CoordinatedTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static async Task RejectAlteredMember(Source2CompiledModelAdapter adapter, string codec, ArtifactContent input,
        ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var target = plan.Operations[0].CoordinatedTransformTarget!; var buffer = target.Buffers[0];
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        using var native = NativeMeshOptimizerCodec.Open(codec);
        var decoded = native.DecodeVertexBuffer(envelope.Blocks[buffer.VertexResourceBlockIndex].Payload.ToArray(), buffer.VertexCount, buffer.PositionLayout.Stride);
        var offset = buffer.PositionLayout.Offset;
        BinaryPrimitives.WriteSingleLittleEndian(decoded.AsSpan(offset), BinaryPrimitives.ReadSingleLittleEndian(decoded.AsSpan(offset)) + 0.01f);
        var encoded = native.EncodeVertexBuffer(decoded, buffer.VertexCount, buffer.PositionLayout.Stride, 1);
        var changed = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [buffer.VertexResourceBlockIndex] = encoded });
        var forged = Rehash(plan, target with
        {
            Buffers = [buffer with
        { ExpectedPositionHash = Source2CompiledModelAdapter.EllipsoidPositionHash(decoded, buffer.PositionLayout, buffer.VertexCount), ExpectedDecodedVertexBufferHash = ContentHash.Compute(decoded) }, .. target.Buffers.Skip(1)]
        });
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(forged));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyCoordinatedTransformAsync(input, new(output.LogicalPath, ContentHash.Compute(changed), changed), forged, token));
    }

    private static async Task RejectAlteredZeroFields(Source2CompiledModelAdapter adapter, ArtifactContent input,
        ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var target = plan.Operations[0].CoordinatedTransformTarget!;
        if (target.ZeroBoxTargets.Count > 0)
        {
            var box = target.ZeroBoxTargets[0];
            var changed = LocalExperimentalVisualIntegrationTests.MutateMesh(output, box.ResourceBlockIndex, mesh =>
                KvNumericMutation.ReplaceVector3(mesh["m_skeleton"]["m_bones"][box.BoneIndex]["m_bbox"], "m_vecCenter",
                    new(), new() { X = BitConverter.UInt32BitsToSingle(0x80000000) }, "altered typed zero box"));
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyCoordinatedTransformAsync(input, changed, plan, token));
        }
        if (target.ZeroRenderSphereTargets.Count > 0)
        {
            var sphere = target.ZeroRenderSphereTargets[0];
            var paired = target.ZeroBoxTargets.Single(b => b.ResourceBlockIndex == sphere.ResourceBlockIndex && b.FieldPath == sphere.PairedZeroBoxFieldPath);
            var changed = LocalExperimentalVisualIntegrationTests.MutateMesh(output, sphere.ResourceBlockIndex, mesh =>
            {
                var bone = mesh["m_skeleton"]["m_bones"][paired.BoneIndex];
                bone.Remove("m_flSphereRadius");
                bone.Add("m_flSphereRadius", sphere.Storage == "binary64" ? new ValveKeyValue.KVObject(0f) : new ValveKeyValue.KVObject(0d));
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyCoordinatedTransformAsync(input, changed, plan, token));
        }
    }
}

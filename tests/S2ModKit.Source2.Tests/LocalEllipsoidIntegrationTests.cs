using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed class LocalEllipsoidIntegrationTests
{
    [Fact]
    public async Task ConfiguredEllipsoidPreviewRetainsEveryLodPointAndTriangleAndRejectsSourceDrift()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_LOGICAL_PATH");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_RECIPE");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_CODEC");
        if (new[] { path, logical, recipePath, hash, codec }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set the five S2MODKIT_TEST_ELLIPSOID variables for an explicit immutable source and codec.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(hash, ContentHash.Compute(bytes).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(bytes), bytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath!, token), "Ellipsoid recipe");
        var adapter = new Source2CompiledModelAdapter(codec);
        var snapshot = await adapter.InspectAsync(input, token);
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        var geometry = await adapter.ReadEllipsoidPreviewGeometryAsync(input, plan, token);
        var preview = EllipsoidSelectionPreviewBuilder.Create(plan, geometry);
        Assert.Equal(snapshot.Lods.Count, preview.Lods.Count);
        foreach (var lod in preview.Lods)
        {
            Assert.Equal(lod.Buffer.VertexCount, lod.Points.Count);
            Assert.Equal(plan.Operations[0].SelectedDrawCalls.Where(c => c.Lod == lod.Buffer.Lod).Sum(c => c.IndexCount), lod.TriangleIndices.Count);
            Assert.Equal(lod.Buffer.CoreVertexCount, lod.Points.Count(p => p.Membership == "core"));
            Assert.Equal(lod.Buffer.TransitionVertexCount, lod.Points.Count(p => p.Membership == "transition"));
            Assert.Equal(lod.Buffer.PinnedVertexCount, lod.Points.Count(p => p.Membership == "pinned"));
            Assert.NotEmpty(lod.Context);
            Assert.All(lod.Points.Where(p => p.Membership == "pinned"), p => Assert.Equal(p.Original, p.Predicted));
        }
        var repeated = EllipsoidSelectionPreviewBuilder.Create(plan, await adapter.ReadEllipsoidPreviewGeometryAsync(input, plan, token));
        Assert.Equal(preview.PreviewFingerprint, repeated.PreviewFingerprint);
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        var buffer = target.Buffers[0];
        var forgedLayout = target with
        {
            Buffers = [buffer with
        {
            PositionLayout = buffer.PositionLayout with { Stride = buffer.PositionLayout.Stride + 4 },
            PackedFrameLayout = buffer.PackedFrameLayout with { Stride = buffer.PackedFrameLayout.Stride + 4 },
        }, .. target.Buffers.Skip(1)]
        };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadEllipsoidPreviewGeometryAsync(input, Rehash(plan, forgedLayout), token));
        var drifted = bytes.ToArray(); drifted[^1] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadEllipsoidPreviewGeometryAsync(input with { Bytes = drifted }, plan, token));
        Assert.Equal(hash, ContentHash.Compute(await File.ReadAllBytesAsync(path!, token)).Value);
    }

    [Fact]
    public async Task ConfiguredEllipsoidPlansWritesAndRejectsRehashedAdversarialOutputs()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_LOGICAL_PATH");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_RECIPE");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_ELLIPSOID_CODEC");
        if (new[] { path, logical, recipePath, hash, codec }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set the five S2MODKIT_TEST_ELLIPSOID variables to qualify an explicit proprietary source and codec.");
        var token = TestContext.Current.CancellationToken;
        var inputBytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(hash, ContentHash.Compute(inputBytes).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(inputBytes), inputBytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath!, token), "Ellipsoid recipe");
        var adapter = new Source2CompiledModelAdapter(codec);
        var snapshot = await adapter.InspectAsync(input, token);
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        Assert.Equal(4, plan.SchemaVersion);
        Assert.Equal(JsonDefaults.Serialize(plan), JsonDefaults.Serialize(MutationPlanner.CreatePlan(snapshot, recipe, input, adapter)));
        Assert.True(adapter.CanRewrite(snapshot, plan));
        var candidate = await adapter.RewriteAsync(input, snapshot, plan, token);
        var output = new ArtifactContent(input.LogicalPath, candidate.Snapshot.Artifact.ContentHash, candidate.Content);
        Assert.Equal(candidate.Content.ToArray(), (await adapter.RewriteAsync(input, snapshot, plan, token)).Content.ToArray());
        var audit = await adapter.VerifyEllipsoidTransformAsync(input, output, plan, token);
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        Assert.Equal(snapshot.Lods.Count, target.Buffers.Count);
        Assert.All(target.Buffers, b => { Assert.True(b.PinnedVertexCount > 0); Assert.True(b.TransitionVertexCount > 0); Assert.True(b.ChangedPositionCount > 0); });
        Assert.All(audit.Boxes, b => Assert.Equal("passed", b.Status));
        Assert.All(audit.PreservedMetadata, p => Assert.Equal("passed", p.Status));
        foreach (var forged in new[]
        {
            target with { Buffers = [target.Buffers[0] with { MaskHash = ContentHash.Compute("forged-mask"u8) }, .. target.Buffers.Skip(1)] },
            target with { Buffers = [target.Buffers[0] with { WeightHash = ContentHash.Compute("forged-weight"u8) }, .. target.Buffers.Skip(1)] },
            target with { BoxTargets = target.BoxTargets.Skip(1).ToArray() },
            target with { BoxTargets = [target.BoxTargets[0] with { ContributorSetHash = ContentHash.Compute("omitted-contributor"u8) }, .. target.BoxTargets.Skip(1)] },
            target with { BoxTargets = [target.BoxTargets[0] with { ContributorCount = target.BoxTargets[0].ContributorCount + 1 }, .. target.BoxTargets.Skip(1)] },
            target with { PreservationTargets = target.PreservationTargets.Skip(1).ToArray() },
            target with { BoxTargets = [target.BoxTargets[0] with { Growth = [target.BoxTargets[0].Growth[0] with { Delta = 1 }, .. target.BoxTargets[0].Growth.Skip(1)] }, .. target.BoxTargets.Skip(1)] },
        })
        {
            var changedPlan = Rehash(plan, forged);
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(changedPlan));
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, output, changedPlan, token));
        }
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot,
            Rehash(plan, target with { Buffers = [target.Buffers[0] with { MaskHash = ContentHash.Compute("forged-mask"u8) }, .. target.Buffers.Skip(1)] }), token));
        var omitted = target.Buffers[0];
        var omittedTarget = target with
        {
            Buffers = target.Buffers.Skip(1).ToArray(),
            BoxTargets = target.BoxTargets.Where(b => b.ResourceBlockIndex != omitted.ResourceBlockIndex).ToArray(),
            PreservationTargets = target.PreservationTargets.Where(p => p.ResourceBlockIndex != omitted.ResourceBlockIndex).ToArray(),
            MaximumDisplacement = target.Buffers.Skip(1).Max(b => b.MaximumDisplacement),
        };
        var omittedPlan = Rehash(plan, omittedTarget);
        omittedPlan = omittedPlan with
        {
            Operations = [omittedPlan.Operations[0] with
        {
            SelectedDrawCalls = plan.Operations[0].SelectedDrawCalls.Where(c => c.Lod != omitted.Lod).ToArray(),
            TargetBlocks = plan.Operations[0].TargetBlocks.Where(b => b.Index != omitted.ResourceBlockIndex && b.Index != omitted.VertexResourceBlockIndex).ToArray(),
        }]
        };
        omittedPlan = omittedPlan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(omittedPlan) };
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(omittedPlan));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, output, omittedPlan, token));
        if (target.LocalTransform.Field.Kind == "mirrored_ellipsoids")
        {
            Assert.NotNull(target.Mirror);
            Assert.All(target.Buffers, b => { Assert.Equal(2, b.MirroredMasks!.Count); Assert.All(b.MirroredMasks, m => Assert.True(m.ChangedPositionCount > 0)); });
            var first = target.Buffers[0];
            var forgedMask = first with { MirroredMasks = [first.MirroredMasks![0], first.MirroredMasks[1] with { MaskHash = ContentHash.Compute("forged-reflected-mask"u8) }] };
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, output,
                Rehash(plan, target with { Buffers = [forgedMask, .. target.Buffers.Skip(1)] }), token));
            var field = target.LocalTransform.Field;
            var staleIntent = target.LocalTransform with { Field = field with { MirrorPlane = field.MirrorPlane! with { Coordinate = field.MirrorPlane.Coordinate - 1 } } };
            var fieldMath = EllipsoidFieldMath.Create(staleIntent, target.DisplacementLimit);
            var stale = target with { LocalTransform = staleIntent, Mirror = EllipsoidFieldMath.Mirror(fieldMath) };
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, output, Rehash(plan, stale), token));
        }
        await RejectAlteredVertexWords(adapter, codec!, input, output, plan, token);
        await RejectOversizedBox(adapter, input, output, plan, token);
        await RejectOtherPayload(adapter, input, output, plan, token);

        foreach (var checkpoint in new[] { AffineRewriteCheckpoint.PositionsTransformed, AffineRewriteCheckpoint.VertexBufferEncoded,
            AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.MetadataSerialized, AffineRewriteCheckpoint.EnvelopeRebuilt,
            AffineRewriteCheckpoint.OutputReopened, AffineRewriteCheckpoint.ReopenVerified })
        {
            var reached = false;
            var failing = new Source2CompiledModelAdapter(codec, point =>
            {
                if (point != checkpoint) return;
                reached = true;
                throw new InvalidOperationException("injected ellipsoid failure");
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token));
            Assert.True(reached);
        }
        var intent = (TransformComponentOperation)recipe.Operations[0];
        var empty = recipe with
        {
            Operations = [intent with { LocalTransform = intent.LocalTransform! with
        { Field = intent.LocalTransform.Field with { Center = new() { X = 10000, Y = 10000, Z = 10000 } } } }]
        };
        Assert.Equal("ELLIPSOID_EMPTY_LOD_EFFECT", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, empty, input, adapter)).Error.Code);
        var tinyLimit = recipe with { Operations = [intent with { Limits = new() { MaximumVertexDisplacement = 0.001f } }] };
        Assert.Equal("TRANSFORM_DISPLACEMENT_EXCEEDED", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, tinyLimit, input, adapter)).Error.Code);
        Assert.Equal(inputBytes, await File.ReadAllBytesAsync(path!, token));
    }

    private static MutationPlan Rehash(MutationPlan plan, PlannedEllipsoidTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputeEllipsoidTargetFingerprint(target) };
        plan = plan with { Operations = [plan.Operations[0] with { EllipsoidTransformTarget = target }] };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static async Task RejectAlteredVertexWords(Source2CompiledModelAdapter adapter, string codec, ArtifactContent input,
        ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        var b = target.Buffers[0];
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        var sourceEnvelope = ResourceEnvelopeReader.Read(input.Bytes);
        using var native = NativeMeshOptimizerCodec.Open(codec);
        var original = native.DecodeVertexBuffer(sourceEnvelope.Blocks[b.VertexResourceBlockIndex].Payload.ToArray(), b.VertexCount, b.PositionLayout.Stride);
        var decoded = native.DecodeVertexBuffer(envelope.Blocks[b.VertexResourceBlockIndex].Payload.ToArray(), b.VertexCount, b.PositionLayout.Stride);
        var field = target.LocalTransform.Field;
        var math = new EllipsoidScale(new(field.Center.X, field.Center.Y, field.Center.Z), new(field.OuterRadii.X, field.OuterRadii.Y, field.OuterRadii.Z), field.CoreFraction, target.LocalTransform.UniformScale, target.DisplacementLimit);
        var pinned = Enumerable.Range(0, b.VertexCount).First(v => math.Evaluate(ReadPoint(original, v * b.PositionLayout.Stride + b.PositionLayout.Offset)).Membership == EllipsoidMembership.Pinned);
        foreach (var offset in new[] { pinned * b.PositionLayout.Stride + b.PositionLayout.Offset,
            pinned * b.PackedFrameLayout.Stride + b.PackedFrameLayout.Offset,
            // A byte outside position/frame fields, such as UV or skin weights.
            Enumerable.Range(0, b.PositionLayout.Stride).First(n => (n < b.PositionLayout.Offset || n >= b.PositionLayout.Offset + 12) && (n < b.PackedFrameLayout.Offset || n >= b.PackedFrameLayout.Offset + 4)) })
        {
            var altered = (byte[])decoded.Clone(); altered[offset] ^= 1;
            var encoded = native.EncodeVertexBuffer(altered, b.VertexCount, b.PositionLayout.Stride, 1);
            var candidate = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [b.VertexResourceBlockIndex] = encoded });
            var forgedBuffer = b with
            {
                ExpectedDecodedVertexBufferHash = ContentHash.Compute(altered),
                ExpectedPositionHash = Source2CompiledModelAdapter.EllipsoidPositionHash(altered, b.PositionLayout, b.VertexCount),
                ExpectedPackedFrameHash = Source2PackedFrameCodec.HashSelected(altered, b.PackedFrameLayout, Enumerable.Range(0, b.VertexCount).ToArray())
            };
            var forged = Rehash(plan, target with { Buffers = [forgedBuffer, .. target.Buffers.Skip(1)] });
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(forged));
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, new(output.LogicalPath, ContentHash.Compute(candidate), candidate), forged, token));
        }
    }

    private static Point3 ReadPoint(byte[] bytes, int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8)));

    private static async Task RejectOtherPayload(Source2CompiledModelAdapter adapter, ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        var unchanged = envelope.Blocks.First(b => !plan.Operations[0].TargetBlocks.Any(t => t.Index == b.Index) && b.Payload.Length > 0);
        var altered = unchanged.Payload.ToArray(); altered[^1] ^= 1;
        var bytes = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [unchanged.Index] = altered });
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, new(output.LogicalPath, ContentHash.Compute(bytes), bytes), plan, token));
    }

    private static async Task RejectOversizedBox(Source2CompiledModelAdapter adapter, ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var target = plan.Operations[0].EllipsoidTransformTarget!;
        var box = target.BoxTargets.First(b => b.Storage == "min_max");
        var words = box.ExpectedWords.ToArray();
        words[3] = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(words[3]) + 10);
        var originalBounds = new Bounds3(ToPoint(box.OriginalWords, 0), ToPoint(box.OriginalWords, 3));
        var oversizedBounds = new Bounds3(ToPoint(words, 0), ToPoint(words, 3));
        var oversizedBox = box with
        {
            ExpectedWords = words,
            Growth = CullingEnvelopeVerifier.MeasureGrowth(originalBounds, oversizedBounds)
            .Select(g => new ExperimentalBoundsGrowth(g.Measure, g.OriginalValue, g.PlannedValue, g.Delta)).ToArray()
        };
        var oversized = LocalExperimentalVisualIntegrationTests.MutateMesh(output, box.ResourceBlockIndex, mesh =>
            KvNumericMutation.ReplaceVector3(mesh["m_sceneObjects"][0], "m_vMaxBounds", LocalExperimentalVisualIntegrationTests.VectorWords(box.ExpectedWords, 3),
                LocalExperimentalVisualIntegrationTests.VectorWords(words, 3), "oversized box fixture"));
        // Both actual bytes and all expected words/hashes are forged consistently.
        // Containment succeeds, but independent retain-and-expand policy must reject.
        var forged = Rehash(plan, target with { BoxTargets = target.BoxTargets.Select(b => b == box ? oversizedBox : b).ToArray() });
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(forged));
        var error = await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, oversized, forged, token));
        Assert.Equal("ELLIPSOID_RESULT_DRIFT", error.Error.Code);
        var changedSphere = LocalExperimentalVisualIntegrationTests.MutateMesh(output, box.ResourceBlockIndex, mesh =>
        {
            var bone = mesh["m_skeleton"]["m_bones"][0];
            var radius = Source2CompiledModelAdapter.ExperimentalFloat(bone["m_flSphereRadius"]);
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", radius, radius + 1, "altered sphere fixture");
        });
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, changedSphere, plan, token));
        var extraMetadata = LocalExperimentalVisualIntegrationTests.MutateMesh(output, box.ResourceBlockIndex, mesh => mesh.Add("synthetic_undeclared_metadata", (ValveKeyValue.KVObject)1));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyEllipsoidTransformAsync(input, extraMetadata, plan, token));
    }

    private static Point3 ToPoint(IReadOnlyList<uint> words, int offset) => new(BitConverter.UInt32BitsToSingle(words[offset]),
        BitConverter.UInt32BitsToSingle(words[offset + 1]), BitConverter.UInt32BitsToSingle(words[offset + 2]));
}

using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalRegionScaleIntegrationTests
{
    [Fact]
    public async Task ConfiguredRegionWritesAndIndependentlyVerifiesPinnedNeckAndFrames()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_REGION_MODEL");
        var logicalPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_REGION_LOGICAL_PATH");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_REGION_RECIPE");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_REGION_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_REGION_CODEC");
        if (new[] { path, logicalPath, recipePath, hash, codec }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set the five S2MODKIT_TEST_REGION variables for the proprietary region writer check.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(hash, ContentHash.Compute(bytes).Value);
        var input = new ArtifactContent(logicalPath!, ContentHash.Compute(bytes), bytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath!, token), "Region recipe");
        var adapter = new Source2CompiledModelAdapter(codec);
        var snapshot = await adapter.InspectAsync(input, token);
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        var discovery = await new ExperimentalComponentDiscoveryService(adapter, adapter).DiscoverAsync(input, snapshot, token);
        var selectedIds = plan.Operations[0].SelectedDrawCalls.Select(call => call.DrawCallId).Order(StringComparer.Ordinal).ToArray();
        var component = discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>().Single(candidate =>
            candidate.Lods.SelectMany(lod => lod.DrawCallIds).Order(StringComparer.Ordinal).SequenceEqual(selectedIds));
        Assert.All(component.Capabilities.Where(capability => capability.OperationVersion is 5 or 6), capability => Assert.Equal("available", capability.Availability));
        var requested = (TransformComponentOperation)recipe.Operations[0];
        var scaffold = await new ComponentRecipeScaffolder(adapter).CreateAsync(input, snapshot, discovery,
            new RecipeScaffoldRequest([component.CandidateId], "region-scale", "unused.json", requested.Transform.UniformScale,
                MaximumVertexDisplacement: requested.Limits!.MaximumVertexDisplacement,
                Experimental: new(requested.RuntimeMetadataPolicy!, requested.Transform.Pivot, requested.Region)), token);
        var scaffoldPlan = MutationPlanner.CreatePlan(snapshot, scaffold, input, adapter);
        Assert.Equal(requested.Region, scaffoldPlan.Operations[0].ExperimentalTransformTarget!.Region!.Selection);
        Assert.Equal(JsonDefaults.Serialize(scaffold), JsonDefaults.Serialize(JsonDefaults.Deserialize<RecipeDocument>(JsonDefaults.SerializeToUtf8(scaffold), "scaffold")));
        Assert.Equal(3, plan.SchemaVersion);
        Assert.Equal(JsonDefaults.Serialize(plan), JsonDefaults.Serialize(MutationPlanner.CreatePlan(snapshot, recipe, input, adapter)));
        var target = plan.Operations[0].ExperimentalTransformTarget!;
        Assert.Equal(snapshot.Lods.Count, target.Region!.Buffers.Count);
        Assert.All(target.Region.Buffers, b => { Assert.True(b.PinnedVertexCount > 0); Assert.True(b.TransitionVertexCount > 0); Assert.True(b.FullVertexCount > 0); });
        var candidate = await adapter.RewriteAsync(input, snapshot, plan, token);
        Assert.Equal(candidate.Content.ToArray(), (await adapter.RewriteAsync(input, snapshot, plan, token)).Content.ToArray());
        var output = new ArtifactContent(input.LogicalPath, candidate.Snapshot.Artifact.ContentHash, candidate.Content);
        var audit = await adapter.VerifyExperimentalTransformAsync(input, output, plan, token);
        Assert.All(audit.Boxes, box => Assert.Equal("passed", box.Status));
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);
        var drift = target with { Region = target.Region with { Buffers = [target.Region.Buffers[0] with { MaskHash = ContentHash.Compute("forged"u8) }, .. target.Region.Buffers.Skip(1)] } };
        var forged = plan with { Operations = [plan.Operations[0] with { ExperimentalTransformTarget = drift }] };
        forged = forged with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(forged) };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot, forged, token));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, output, forged, token));
        var omitted = plan with { Operations = [plan.Operations[0] with { ExperimentalTransformTarget = target with { BoxTargets = target.BoxTargets.Skip(1).ToArray() } }] };
        omitted = omitted with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(omitted) };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, output, omitted, token));
        var missingLod = plan with
        {
            Operations = [plan.Operations[0] with { ExperimentalTransformTarget = target with
        { Region = target.Region with { Buffers = target.Region.Buffers.Skip(1).ToArray() } } }]
        };
        missingLod = missingLod with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(missingLod) };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, output, missingLod, token));
        var geometry = target.GeometryTargets[0];
        var buffer = snapshot.Lods.Single(lod => lod.Level == geometry.Lod).Meshes.Single(mesh => mesh.MeshOrdinal == geometry.MeshOrdinal)
            .Geometry!.VertexBuffers[geometry.VertexBufferOrdinal];
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        using (var native = NativeMeshOptimizerCodec.Open(codec!))
        {
            var decoded = native.DecodeVertexBuffer(envelope.Blocks[geometry.VertexResourceBlockIndex].Payload.ToArray(), buffer.VertexCount, buffer.Stride);
            var region = target.Region.Selection;
            var axisOffset = region.Axis switch { "x" => 0, "y" => 4, _ => 8 };
            var pinned = Enumerable.Range(0, buffer.VertexCount).First(vertex =>
                ReadFloat(decoded, vertex * buffer.Stride + buffer.PositionLayout.Offset + axisOffset) <= region.PinnedThrough);
            var frame = target.Region.Buffers[0].PackedFrameLayout;
            // Forge both the output and its expected hashes. Source policy, not a matching
            // fingerprint/hash, must reject a moved exclusion or an undeclared frame edit.
            foreach (var positionEdit in new[] { true, false })
            {
                var altered = (byte[])decoded.Clone();
                var offset = pinned * buffer.Stride + (positionEdit ? buffer.PositionLayout.Offset : frame.Offset);
                if (positionEdit) BinaryPrimitives.WriteSingleLittleEndian(altered.AsSpan(offset), ReadFloat(altered, offset) + 0.125f);
                else altered[offset] ^= 1;
                var encoded = native.EncodeVertexBuffer(altered, buffer.VertexCount, buffer.Stride, 1);
                var candidateBytes = ResourceEnvelopeWriter.Rebuild(envelope,
                    new Dictionary<int, ReadOnlyMemory<byte>> { [geometry.VertexResourceBlockIndex] = encoded });
                var alteredTarget = target with
                {
                    GeometryTargets = [geometry with { ExpectedDecodedVertexBufferHash = ContentHash.Compute(altered) }, .. target.GeometryTargets.Skip(1)],
                    Region = target.Region with
                    {
                        Buffers = [target.Region.Buffers[0] with
                    { ExpectedPackedFrameHash = Source2PackedFrameCodec.HashSelected(altered, frame, Enumerable.Range(0, buffer.VertexCount).ToArray()) }, .. target.Region.Buffers.Skip(1)]
                    },
                };
                var alteredPlan = plan with { Operations = [plan.Operations[0] with { ExperimentalTransformTarget = alteredTarget }] };
                alteredPlan = alteredPlan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(alteredPlan) };
                var alteredOutput = new ArtifactContent(output.LogicalPath, ContentHash.Compute(candidateBytes), candidateBytes);
                await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, alteredOutput, alteredPlan, token));
            }
        }
        foreach (var checkpoint in new[] { AffineRewriteCheckpoint.PositionsTransformed, AffineRewriteCheckpoint.VertexBufferEncoded,
            AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.MetadataSerialized, AffineRewriteCheckpoint.EnvelopeRebuilt })
        {
            var reached = false;
            var failing = new Source2CompiledModelAdapter(codec, observed =>
            {
                if (observed != checkpoint) return;
                reached = true;
                throw new InvalidOperationException("injected region failure");
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token));
            Assert.True(reached);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path!, token));
    }

    private static float ReadFloat(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));
}

using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class LocalExperimentalVisualIntegrationTests
{
    [Fact]
    public async Task ConfiguredResourcePlansCompleteBuffersAndLeavesInputImmutable()
    {
        var modelPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_VISUAL_MODEL");
        var logicalPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_VISUAL_LOGICAL_PATH");
        var hashText = Environment.GetEnvironmentVariable("S2MODKIT_TEST_VISUAL_SHA256");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_VISUAL_RECIPE");
        var codecPath = Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(logicalPath) || string.IsNullOrWhiteSpace(hashText)
            || string.IsNullOrWhiteSpace(recipePath) || string.IsNullOrWhiteSpace(codecPath))
            Assert.Skip("Set the five S2MODKIT_TEST_VISUAL variables to enable the proprietary visual-only planning check.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(modelPath, token);
        var hash = new ContentHash(hashText);
        Assert.Equal(hash, ContentHash.Compute(bytes));
        var input = new ArtifactContent(StableIdentity.NormalizePath(logicalPath), hash, bytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath, token), "Experimental integration recipe");
        var adapter = new Source2CompiledModelAdapter(codecPath);
        var snapshot = await adapter.InspectAsync(input, token);
        var first = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        var second = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter);
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(JsonDefaults.Serialize(first), JsonDefaults.Serialize(second));
        var target = Assert.Single(first.Operations).ExperimentalTransformTarget!;
        Assert.NotNull(target);
        Assert.Equal(snapshot.Lods.Count, target.GeometryTargets.Count);
        Assert.True(target.BoxTargets.Count > target.GeometryTargets.Count);
        Assert.Contains(target.PreservationTargets, field => field.Category == "root_sphere" && field.Scope == "affected");
        Assert.Contains(target.PreservationTargets, field => field.Category == "render_sphere" && field.Scope == "affected");
        Assert.All(target.GeometryTargets, geometry =>
        {
            var mesh = snapshot.Lods.Single(lod => lod.Level == geometry.Lod).Meshes.Single(mesh => mesh.MeshOrdinal == geometry.MeshOrdinal);
            Assert.Equal(mesh.Geometry!.VertexBuffers[geometry.VertexBufferOrdinal].VertexCount, geometry.SelectedVertexCount);
            Assert.Equal(["position"], geometry.AllowedChangedAttributes);
            Assert.InRange(geometry.MaximumDisplacement, float.Epsilon, recipe.Operations.OfType<TransformComponentOperation>().Single().Limits.MaximumVertexDisplacement);
        });
        var strict = StrictRecipe(recipe);
        var exception = Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, strict, input, adapter));
        Assert.Equal(Environment.GetEnvironmentVariable("S2MODKIT_TEST_VISUAL_STRICT_ERROR") ?? "AFFINE_BOUNDS_UNSUPPORTED", exception.Error.Code);
        var changed = first with
        {
            Operations = [first.Operations[0] with
        {
            ExperimentalTransformTarget = target with { BoxTargets = target.BoxTargets.Skip(1).ToArray() },
        }]
        };
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<MutationPlan>(JsonDefaults.SerializeToUtf8(changed), "Tampered experimental plan"));
        Assert.True(adapter.CanRewrite(snapshot, first));
        var candidate = await adapter.RewriteAsync(input, snapshot, first, token);
        var repeated = await adapter.RewriteAsync(input, snapshot, second, token);
        Assert.Equal(candidate.Content.ToArray(), repeated.Content.ToArray());
        var output = new ArtifactContent(input.LogicalPath, candidate.Snapshot.Artifact.ContentHash, candidate.Content);
        var audit = await adapter.VerifyExperimentalTransformAsync(input, output, first, token);
        Assert.All(audit.Boxes, box => Assert.Equal("passed", box.Status));
        Assert.All(audit.PreservedMetadata, field => Assert.Equal("passed", field.Status));
        Assert.Equal(3, audit.Boundaries.Count(boundary => boundary.Status == "untested"));
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, first).IsValid);
        var omitted = changed with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(changed) };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, output, omitted, token));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot, omitted, token));
        var sceneBox = target.BoxTargets.First(box => box.Storage == "min_max");
        var oversizedWords = sceneBox.ExpectedWords.ToArray();
        oversizedWords[3] = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(oversizedWords[3]) + 8f);
        var oversizedBox = sceneBox with { ExpectedWords = oversizedWords };
        var oversized = MutateMesh(output, sceneBox.ResourceBlockIndex, mesh =>
        {
            var scene = mesh["m_sceneObjects"][0];
            KvNumericMutation.ReplaceVector3(scene, "m_vMaxBounds", VectorWords(sceneBox.ExpectedWords, 3), VectorWords(oversizedWords, 3), "adversarial scene");
        });
        // Also forge a consistent fingerprint/observed target. Merely checking plan equality
        // would accept this containing but excessively enlarged result; policy verification must not.
        var oversizedPlan = first with
        {
            Operations = [first.Operations[0] with
        {
            ExperimentalTransformTarget = target with { BoxTargets = target.BoxTargets.Select(box => box == sceneBox ? oversizedBox : box).ToArray() },
        }]
        };
        oversizedPlan = oversizedPlan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(oversizedPlan) };
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, oversized, oversizedPlan, token));
        var changedSphere = MutateMesh(output, sceneBox.ResourceBlockIndex, mesh =>
        {
            var bone = mesh["m_skeleton"]["m_bones"][0];
            var radius = Source2CompiledModelAdapter.ExperimentalFloat(bone["m_flSphereRadius"]);
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", radius, radius + 1f, "adversarial sphere");
        });
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, changedSphere, first, token));
        foreach (var block in ResourceEnvelopeReader.Read(output.Bytes).Blocks.Where(block => block.Type is "DSTF" or "PHYS"))
        {
            var changedPayload = block.Payload.ToArray();
            changedPayload[^1] ^= 1;
            var bytesWithDrift = ResourceEnvelopeWriter.Rebuild(ResourceEnvelopeReader.Read(output.Bytes),
                new Dictionary<int, ReadOnlyMemory<byte>> { [block.Index] = changedPayload });
            var drift = new ArtifactContent(output.LogicalPath, ContentHash.Compute(bytesWithDrift), bytesWithDrift);
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyExperimentalTransformAsync(input, drift, first, token));
        }
        foreach (var checkpoint in new[] { AffineRewriteCheckpoint.PositionsTransformed, AffineRewriteCheckpoint.VertexBufferEncoded,
            AffineRewriteCheckpoint.BoundsUpdated, AffineRewriteCheckpoint.MetadataSerialized, AffineRewriteCheckpoint.EnvelopeRebuilt })
        {
            var reached = false;
            var failing = new Source2CompiledModelAdapter(codecPath, observed =>
            {
                if (observed != checkpoint) return;
                reached = true;
                throw new InvalidOperationException("Injected experimental failure.");
            });
            await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, first, token));
            Assert.True(reached);
        }
        Assert.Equal(hash, ContentHash.Compute(await File.ReadAllBytesAsync(modelPath, token)));
    }

    private static ArtifactContent MutateMesh(ArtifactContent output, int blockIndex, Action<KVObject> mutate)
    {
        using var stream = new MemoryStream(output.Bytes.ToArray(), writable: false);
        using var resource = new Resource { FileName = output.LogicalPath };
        resource.Read(stream, verifyFileSize: true, leaveOpen: true);
        var mesh = (Mesh)resource.Blocks[blockIndex];
        mutate(mesh.Data);
        using var serialized = new MemoryStream();
        mesh.Serialize(serialized);
        var bytes = ResourceEnvelopeWriter.Rebuild(ResourceEnvelopeReader.Read(output.Bytes),
            new Dictionary<int, ReadOnlyMemory<byte>> { [blockIndex] = serialized.ToArray() });
        return new(output.LogicalPath, ContentHash.Compute(bytes), bytes);
    }

    private static TransformVector3 VectorWords(IReadOnlyList<uint> words, int offset) => new()
    {
        X = BitConverter.UInt32BitsToSingle(words[offset]),
        Y = BitConverter.UInt32BitsToSingle(words[offset + 1]),
        Z = BitConverter.UInt32BitsToSingle(words[offset + 2]),
    };

    private static RecipeDocument StrictRecipe(RecipeDocument recipe)
    {
        var visual = recipe.Operations.OfType<TransformComponentOperation>().Single();
        return recipe with
        {
            SchemaVersion = 5,
            Operations = [visual with
            {
                Version = 4, RuntimeMetadataPolicy = null,
                Transform = new()
                {
                    Pivot = visual.Transform.Pivot,
                    Scale = new() { X = visual.Transform.UniformScale, Y = visual.Transform.UniformScale, Z = visual.Transform.UniformScale },
                    Rotation = new() { Kind = "identity" }, Frame = new() { Kind = "model" },
                },
            }],
        };
    }
}

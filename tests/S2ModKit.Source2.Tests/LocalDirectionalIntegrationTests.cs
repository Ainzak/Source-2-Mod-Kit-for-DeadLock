using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed partial class LocalDirectionalIntegrationTests
{
    [Fact]
    public async Task ConfiguredDirectionalSourcePassesDeterministicUnpublishedWritingAndAtomicFailureControls()
    {
        var path = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_MODEL");
        var logical = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_LOGICAL_PATH");
        var recipePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_RECIPE");
        var hash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_SHA256");
        var codec = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_CODEC");
        var codecHash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_DIRECTIONAL_CODEC_SHA256");
        if (new[] { path, logical, recipePath, hash, codec, codecHash }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set six S2MODKIT_TEST_DIRECTIONAL variables for an explicit source, recipe and hash-pinned codec.");
        var token = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(path!, token); Assert.Equal(hash, ContentHash.Compute(bytes).Value);
        Assert.Equal(codecHash, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(bytes), bytes);
        var recipe = JsonDefaults.Deserialize<RecipeDocument>(await File.ReadAllBytesAsync(recipePath!, token), "Directional source recipe");
        var adapter = new Source2CompiledModelAdapter(codec); var snapshot = await adapter.InspectAsync(input, token);
        var authoring = await adapter.ReadDirectionalAuthoringSourceAsync(input, ((TransformComponentOperation)recipe.Operations[0]).DirectionalTransform!.Members, token);
        Assert.Equal(input.ContentHash, authoring.InputHash);
        Assert.Equal(snapshot.Lods.Count, authoring.Buffers.Select(b => b.Source.Lod).Distinct().Count());
        Assert.All(authoring.Buffers, b => Assert.Equal(b.Source.VertexCount, b.Points.Count));
        Assert.All(authoring.Bones, b => Assert.True((b.Assertion is null) != (b.UnavailableReason is null)));
        Assert.All(authoring.Bones.Where(b => b.Assertion is not null), b => Assert.Equal(snapshot.Lods.Count, Assert.IsType<DirectionalBoneAssertion>(b.Assertion).Lods.Count));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadDirectionalAuthoringSourceAsync(input with { ContentHash = ContentHash.Compute("stale"u8) }, authoring.Members, token));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.ReadDirectionalAuthoringSourceAsync(input, [authoring.Members[0] with { Lods = authoring.Members[0].Lods.Skip(1).ToArray() }], token));
        var plan = MutationPlanner.CreatePlan(snapshot, recipe, input, adapter); var target = plan.Operations[0].DirectionalTransformTarget!;
        Assert.Equal(6, plan.SchemaVersion); Assert.True(adapter.CanRewrite(snapshot, plan));
        Assert.Equal(JsonDefaults.Serialize(plan), JsonDefaults.Serialize(MutationPlanner.CreatePlan(snapshot, recipe, input, adapter)));
        var reached = new List<AffineRewriteCheckpoint>();
        var recording = new Source2CompiledModelAdapter(codec, reached.Add);
        var candidate = await recording.RewriteAsync(input, snapshot, plan, token);
        Assert.Equal(candidate.Content.ToArray(), (await adapter.RewriteAsync(input, snapshot, plan, token)).Content.ToArray());
        Assert.Equal(target.Buffers.Count, reached.Count(c => c == AffineRewriteCheckpoint.VertexBufferEncoded));
        Assert.Equal(target.Buffers.Select(b => b.ResourceBlockIndex).Distinct().Count(), reached.Count(c => c == AffineRewriteCheckpoint.MetadataSerialized));
        Assert.Equal(1, reached.Count(c => c == AffineRewriteCheckpoint.EnvelopeRebuilt));
        Assert.True(ModelVerifier.Verify(snapshot, candidate.Snapshot, plan).IsValid);
        var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(candidate.Content.Span), candidate.Content);
        var verification = await adapter.VerifyDirectionalTransformAsync(input, output, plan, token);
        Assert.Equal(target.Buffers.Count, verification.Observed.Buffers.Count);
        Assert.Equal(target.BoxTargets.Count, verification.Boxes.Count);
        Assert.All(verification.Boundaries.Where(b => b.Name.StartsWith("directional_", StringComparison.Ordinal)), b => Assert.Equal("passed", b.Status));
        var previewGeometry = await adapter.ReadDirectionalPreviewGeometryAsync(input, plan, token);
        var preview = DirectionalSelectionPreviewBuilder.Create(plan, previewGeometry);
        Assert.Equal(target.ContextBuffers.Count, preview.Buffers.Count);
        Assert.Equal(target.Buffers.Select(b => b.Lod).Distinct(), preview.Measurements.Select(m => m.Lod));
        foreach (var measurement in preview.Measurements)
        {
            Assert.Equal(target.Buffers.Where(b => b.Lod == measurement.Lod).Sum(b => b.ChangedPositionCount), measurement.ChangedCount);
            Assert.Equal(target.Protection.Union.Where(s => s.Lod == measurement.Lod).Sum(s => s.VertexCount), measurement.ProtectedCount);
            Assert.True(measurement.MaximumDisplacementOverModelDiagonal > 0);
        }
        Assert.Equal(preview.PreviewFingerprint, DirectionalSelectionPreviewBuilder.Create(plan, await adapter.ReadDirectionalPreviewGeometryAsync(input, plan, token)).PreviewFingerprint);
        var missingContext = previewGeometry with { Buffers = previewGeometry.Buffers.Where(b => b.Source.Selected).ToArray() };
        Assert.Equal("DIRECTIONAL_PREVIEW_INVALID", Assert.Throws<S2ModKitException>(() => DirectionalSelectionPreviewBuilder.Create(plan, missingContext)).Error.Code);
        await RejectDirectionalOutputTampering(adapter, codec!, input, output, plan, token);
        foreach (var point in Enum.GetValues<AffineRewriteCheckpoint>())
        {
            var injected = false;
            var failing = new Source2CompiledModelAdapter(codec, current =>
            {
                if (point != current) return;
                injected = true; throw new InvalidOperationException("injected directional candidate failure");
            });
            Assert.Equal("DIRECTIONAL_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => failing.RewriteAsync(input, snapshot, plan, token))).Error.Code);
            Assert.True(injected);
        }
        var first = target.Buffers[0];
        var forged = new[]
        {
            target with { BoxTargets = target.BoxTargets.Skip(1).ToArray(), BoxClosures = target.BoxClosures.Skip(1).ToArray() },
            target with { ContextBuffers = target.ContextBuffers.Select(c => c.Selected ? c : c with { MeshMask = c.MeshMask ^ 1 }).ToArray() },
            target with { Coincidences = target.Coincidences.Select(c => c with { CompleteSourcePositionHash = ContentHash.Compute("forged-context"u8) }).ToArray() },
            target with { BoxTargets = target.BoxTargets.Select(b => b with { ContributorSetHash = ContentHash.Compute("forged-box-membership"u8) }).ToArray() },
            target with { Buffers = target.Buffers.Select(b => b == first ? b with { WeightHash = ContentHash.Compute("forged-weights"u8) } : b).ToArray() },
        };
        foreach (var altered in forged)
        {
            var stale = Rehash(plan, altered);
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(stale));
            Assert.Equal("DIRECTIONAL_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => adapter.RewriteAsync(input, snapshot, stale, token))).Error.Code);
            Assert.Equal("DIRECTIONAL_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, output, stale, token))).Error.Code);
        }
        var operation = (TransformComponentOperation)recipe.Operations.Single();
        var visual = operation.DirectionalTransform!;
        var assertion = Assert.IsType<DirectionalVertexAssertion>(visual.Protection.Assertions[0]);
        if (visual.Members[0].Lods.Count > 1)
        {
            var omittedLod = visual.Members[0].Lods[^1].Lod;
            var incomplete = operation with
            {
                ExpectedMatchesByLod = operation.ExpectedMatchesByLod.Where(p => p.Key != omittedLod.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(),
                ExpectedVerticesByLod = operation.ExpectedVerticesByLod.Where(p => p.Key != omittedLod.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToDictionary(),
                DirectionalTransform = visual with
                {
                    Members = visual.Members.Select(m => m with { Lods = m.Lods.Where(l => l.Lod != omittedLod).ToArray() }).ToArray(),
                    Protection = visual.Protection with { Assertions = [assertion with { Sets = assertion.Sets.Where(s => s.Lod != omittedLod).ToArray() }] }
                },
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = visual.Members.SelectMany(m => m.Lods.Where(l => l.Lod != omittedLod)).SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
            };
            Assert.Equal("LOD_COVERAGE_INCOMPLETE", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, recipe with { Operations = [incomplete] }, input, adapter)).Error.Code);
        }
        var changedIndex = target.WordAudits.Single(a => a.MemberId == first.MemberId && a.Lod == first.Lod).ChangedPositionIndices[0];
        var movedSets = assertion.Sets.Select(s => s.MemberId == first.MemberId && s.Lod == first.Lod
            ? s with { VertexIndices = [changedIndex], VertexCount = 1, VertexSetHash = DirectionalContractValidator.VertexSetHash([changedIndex]) } : s).ToArray();
        var moved = recipe with
        {
            Operations = [operation with { DirectionalTransform = visual with
        { Protection = visual.Protection with { Assertions = [assertion with { Sets = movedSets }] } } }]
        };
        Assert.Equal("DIRECTIONAL_PROTECTED_WORD_CHANGED", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, moved, input, adapter)).Error.Code);
        if (visual.Members.Count > 1)
        {
            var member = visual.Members[0]; var ids = member.Lods.SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray();
            var single = operation with
            {
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = ids },
                ExpectedMatchesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture), l => l.DrawCallIds.Count),
                ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture), l => l.ExpectedVertices),
                DirectionalTransform = visual with
                {
                    Members = [member],
                    Protection = visual.Protection with
                    { Assertions = [assertion with { Sets = assertion.Sets.Where(s => s.MemberId == member.MemberId).ToArray() }] }
                },
            };
            Assert.Equal("DIRECTIONAL_EXCLUDED_COINCIDENT_MATE", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(snapshot, recipe with { Operations = [single] }, input, adapter)).Error.Code);
        }
        await RejectChangedSource("root_bounds", "EXPERIMENTAL_ROOT_METADATA_UNSUPPORTED");
        await RejectChangedSource("unknown_stream", "EXPERIMENTAL_VERTEX_STREAM_UNSUPPORTED");
        if (snapshot.Artifact.Blocks.Any(b => b.Type == "DSTF"))
            await RejectChangedSource("unknown_family", "EXPERIMENTAL_DISTANCE_FIELD_UNSUPPORTED");
        else
            Assert.DoesNotContain(target.PreservationTargets, p => p.Category == "distance_field");
        await RejectChangedSource("shared_lod", "EXPERIMENTAL_LAYOUT_UNSUPPORTED");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path!, token));
        Assert.Equal(codecHash, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);

        async Task RejectChangedSource(string variant, string reason)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var resource = new Resource { FileName = input.LogicalPath }; resource.Read(stream, verifyFileSize: true, leaveOpen: true);
            var model = resource.Blocks.OfType<Model>().Single(); var block = resource.Blocks.IndexOf(model);
            if (variant == "root_bounds") model.Data.Add("m_vMinBounds", Array(0f, 0f, 0f));
            if (variant == "shared_lod")
            {
                var masks = KVObject.Array(); var ordinal = 0;
                foreach (var value in model.Data["m_refLODGroupMasks"].Values) masks.Add(ordinal++ == first.MeshOrdinal ? new KVObject(3UL) : value);
                model.Data["m_refLODGroupMasks"] = masks;
            }
            if (variant == "unknown_stream")
            {
                var control = resource.Blocks.OfType<BinaryKV3>().Single(b => b.Type.ToString() == "CTRL" && b.Data.Root.ContainsKey("embedded_meshes"));
                block = resource.Blocks.IndexOf(control);
                var descriptor = control.Data.Root["embedded_meshes"].Values.ElementAt(first.MeshOrdinal)["m_vertexBuffers"].Values.ElementAt(first.VertexBufferOrdinal);
                var layout = descriptor["m_inputLayoutFields"];
                var optionalUv = layout.Values.FirstOrDefault(f => f["m_pSemanticName"].ToString(System.Globalization.CultureInfo.InvariantCulture).Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase));
                if (optionalUv is not null) optionalUv["m_pSemanticName"] = new KVObject("UNCHARACTERIZED");
                else
                {
                    // A qualified buffer need not have UVs. Add an explicitly unknown
                    // descriptor instead of assuming a particular source attribute exists.
                    var unknown = KVObject.Collection(); var existing = layout.Values.First();
                    foreach (var key in existing.Keys) unknown.Add(key, existing[key]);
                    unknown["m_pSemanticName"] = new KVObject("UNCHARACTERIZED");
                    layout.Add(unknown);
                }
            }
            if (variant == "unknown_family")
            {
                var distance = resource.Blocks.OfType<BinaryKV3>().First(b => b.Type.ToString() == "DSTF");
                block = resource.Blocks.IndexOf(distance); distance.Data.Root.Add("unclassified_family", KVObject.Collection());
            }
            using var payload = new MemoryStream(); resource.Blocks[block].Serialize(payload);
            var changed = ResourceEnvelopeWriter.Rebuild(ResourceEnvelopeReader.Read(bytes), new Dictionary<int, ReadOnlyMemory<byte>> { [block] = payload.ToArray() });
            var artifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(changed), changed);
            var changedSnapshot = await adapter.InspectAsync(artifact, token);
            Assert.Equal(reason, Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(changedSnapshot, recipe with { InputHash = artifact.ContentHash }, artifact, adapter)).Error.Code);
        }
    }

    private static KVObject Array(params float[] values)
    {
        var array = KVObject.Array(); foreach (var value in values) array.Add(new KVObject(value)); return array;
    }

    private static MutationPlan Rehash(MutationPlan plan, PlannedDirectionalTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(target) };
        var altered = plan with { Operations = [plan.Operations[0] with { DirectionalTransformTarget = target }] };
        return altered with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(altered) };
    }
}

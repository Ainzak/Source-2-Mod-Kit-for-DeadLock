using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class LocalPairedVerificationTests
{
    [Fact]
    public async Task ConfiguredPairedVerifierRejectsCoherentlyRehashedGeometryDispatchPartitionsBoxesAndConsumers()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable("S2MODKIT_TEST_PAIRED_" + name);
        var path = Env("MODEL"); var logical = Env("LOGICAL_PATH"); var planPath = Env("PLAN"); var hash = Env("SHA256");
        var codec = Env("CODEC"); var codecHash = Env("CODEC_SHA256"); var candidatePath = Env("CANDIDATE");
        if (new[] { path, logical, planPath, hash, codec, codecHash, candidatePath }.Any(string.IsNullOrWhiteSpace))
            Assert.Skip("Set seven S2MODKIT_TEST_PAIRED variables for immutable source, candidate, plan and pinned codec.");
        var token = TestContext.Current.CancellationToken; var sourceBytes = await File.ReadAllBytesAsync(path!, token);
        Assert.Equal(hash, ContentHash.Compute(sourceBytes).Value); Assert.Equal(codecHash, ContentHash.Compute(await File.ReadAllBytesAsync(codec!, token)).Value);
        var input = new ArtifactContent(logical!, ContentHash.Compute(sourceBytes), sourceBytes);
        var candidateBytes = await File.ReadAllBytesAsync(candidatePath!, token);
        var output = new ArtifactContent(logical!, ContentHash.Compute(candidateBytes), candidateBytes);
        var plan = MutationPlanJson.Read(await File.ReadAllBytesAsync(planPath!, token)); var target = plan.Operations[0].PairedTransformTarget!;
        var adapter = new Source2CompiledModelAdapter(codec);
        var verification = await adapter.VerifyPairedTransformAsync(input, output, plan, token);
        Assert.Equal(target.Buffers.Count, verification.Observed.Buffers.Count); Assert.Equal(target.BoxTargets.Count, verification.Boxes.Count);
        Assert.All(verification.Boundaries.Where(b => b.Name.StartsWith("paired_", StringComparison.Ordinal)), b => Assert.Equal("passed", b.Status));
        var envelope = ResourceEnvelopeReader.Read(output.Bytes); using var native = NativeMeshOptimizerCodec.Open(codec!);
        var first = target.Buffers[0]; var dispatch = target.Dispatch[0]; var original = native.DecodeVertexBuffer(envelope.Blocks[first.VertexResourceBlockIndex].Payload.ToArray(), first.VertexCount, first.PositionLayout.Stride);
        foreach (var defect in new[] { "second_position", "protected_frame", "procedural_position", "hidden_attribute" })
        {
            var changed = original.ToArray();
            if (defect == "second_position")
            {
                var vertex = dispatch.Fields[1].ChangedPositionIndices[0]; var offset = vertex * first.PositionLayout.Stride + first.PositionLayout.Offset;
                BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(offset), BinaryPrimitives.ReadSingleLittleEndian(changed.AsSpan(offset)) + .01f);
            }
            else if (defect == "procedural_position")
            {
                var fixedSet = target.ProceduralInputs.Union.Single(s => s.Lod == first.Lod && s.MeshOrdinal == first.MeshOrdinal && s.VertexBufferOrdinal == first.VertexBufferOrdinal);
                Assert.NotEmpty(fixedSet.VertexIndices); changed[fixedSet.VertexIndices[0] * first.PositionLayout.Stride + first.PositionLayout.Offset] ^= 1;
            }
            else if (defect == "protected_frame")
            {
                var fixedSet = target.Protection.Union.Single(s => s.Lod == first.Lod && s.MeshOrdinal == first.MeshOrdinal && s.VertexBufferOrdinal == first.VertexBufferOrdinal);
                changed[fixedSet.VertexIndices[0] * first.PackedFrameLayout.Stride + first.PackedFrameLayout.Offset] ^= 1;
            }
            else
            {
                var offset = Enumerable.Range(0, first.PositionLayout.Stride).First(i => !(i >= first.PositionLayout.Offset && i < first.PositionLayout.Offset + 12)
                    && !(i >= first.PackedFrameLayout.Offset && i < first.PackedFrameLayout.Offset + 4)); changed[offset] ^= 1;
            }
            var wrong = Replace(first.VertexResourceBlockIndex, native.EncodeVertexBuffer(changed, first.VertexCount, first.PositionLayout.Stride, 1));
            var claimed = plan;
            if (defect == "second_position")
            {
                var positionHash = Source2CompiledModelAdapter.DirectionalPositionWords(changed, first.PositionLayout, Enumerable.Range(0, first.VertexCount).ToArray());
                var closures = target.BoxClosures.Select(c =>
                {
                    var rows = c.Contributors.Select(r => r.Lod == first.Lod && r.MeshOrdinal == first.MeshOrdinal && r.VertexBufferOrdinal == first.VertexBufferOrdinal
                        ? r with { ExpectedPositionHash = positionHash } : r).ToArray();
                    return c with { Contributors = rows, ClosureHash = MutationPlanJson.ComputeDirectionalFactsHash(rows) };
                }).ToArray();
                claimed = Rehash(plan, target with
                {
                    Buffers = target.Buffers.Select(b => b == first ? b with { ExpectedDecodedVertexBufferHash = ContentHash.Compute(changed), ExpectedPositionHash = positionHash } : b).ToArray(),
                    BoxClosures = closures,
                    BoxTargets = target.BoxTargets.Select(b => b with
                    { ContributorSetHash = closures.Single(c => c.ResourceBlockIndex == b.ResourceBlockIndex && c.FieldPath == b.FieldPath).ClosureHash }).ToArray()
                });
                Assert.True(ModelVerifier.Verify(await adapter.InspectAsync(input, token), await adapter.InspectAsync(wrong, token), claimed).IsValid);
            }
            await Reject(wrong, claimed);
        }
        var moved = dispatch.Fields[0].ChangedPositionIndices[0]; var wasCore = dispatch.Fields[0].CoreIndices.Contains(moved);
        var rowsWithWrongDispatch = dispatch.Fields.Select((f, i) => f with
        {
            CoreIndices = wasCore ? Move(f.CoreIndices, i) : f.CoreIndices,
            TransitionIndices = wasCore ? f.TransitionIndices : Move(f.TransitionIndices, i),
            ChangedPositionIndices = Move(f.ChangedPositionIndices, i),
            ChangedFrameIndices = f.ChangedFrameIndices.Contains(moved) || dispatch.Fields[0].ChangedFrameIndices.Contains(moved) ? Move(f.ChangedFrameIndices, i) : f.ChangedFrameIndices
        }).Select(f => f with { MembershipHash = PairedContractValidator.FieldMembershipHash(first.VertexCount, f) }).ToArray();
        var wrongDispatch = dispatch with
        {
            Fields = rowsWithWrongDispatch,
            MembershipHash = PairedContractValidator.DispatchMembershipHash(first.VertexCount, rowsWithWrongDispatch),
            WeightHash = PairedContractValidator.DispatchWeightHash(rowsWithWrongDispatch)
        };
        await Reject(output, Rehash(plan, target with
        {
            Dispatch = target.Dispatch.Select(d => d == dispatch ? wrongDispatch : d).ToArray(),
            Buffers = target.Buffers.Select(b => b == first ? b with { MaskHash = wrongDispatch.MembershipHash, WeightHash = wrongDispatch.WeightHash } : b).ToArray()
        }));
        var sourceFaces = target.SourceTriangles[0]; var partitions = sourceFaces.SourcePartitions.ToArray();
        var collapsed = Array.FindIndex(partitions, p => p != 0); Assert.True(collapsed >= 0); partitions[collapsed] = partitions[collapsed] == 1 ? (byte)2 : (byte)1;
        var partitionHash = ContentHash.Compute(partitions);
        await Reject(output, Rehash(plan, target with
        {
            SourceTriangles = target.SourceTriangles.Select(f => f == sourceFaces
            ? f with { SourcePartitions = partitions, SourcePartitionHash = partitionHash, ExpectedPartitionHash = partitionHash } : f).ToArray()
        }));
        var scene = target.BoxTargets.First(b => b.Storage == "min_max"); var words = scene.ExpectedWords.ToArray();
        words[3] = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(words[3]) + 1);
        var oversized = LocalExperimentalVisualIntegrationTests.MutateMesh(output, scene.ResourceBlockIndex, mesh =>
            KvNumericMutation.ReplaceVector3(mesh["m_sceneObjects"][0], "m_vMaxBounds", LocalExperimentalVisualIntegrationTests.VectorWords(scene.ExpectedWords, 3),
                LocalExperimentalVisualIntegrationTests.VectorWords(words, 3), "oversized paired box"));
        var growth = CullingEnvelopeVerifier.MeasureGrowth(new Bounds3(Point(scene.OriginalWords, 0), Point(scene.OriginalWords, 3)),
            new Bounds3(Point(words, 0), Point(words, 3))).Select(g => new ExperimentalBoundsGrowth(g.Measure, g.OriginalValue, g.PlannedValue, g.Delta)).ToArray();
        await Reject(oversized, Rehash(plan, target with { BoxTargets = target.BoxTargets.Select(b => b == scene ? b with { ExpectedWords = words, Growth = growth } : b).ToArray() }));
        var families = target.ProceduralInputs.ConsumerFamilies.Select(f => f.Category == "attachment" ? f with
        {
            Records = f.Records.Select((r, i) => i == 0
            ? r with { SourcePayloadHash = ContentHash.Compute("forged-consumer"u8), ExpectedPayloadHash = ContentHash.Compute("forged-consumer"u8) } : r).ToArray()
        } : f).ToArray();
        await Reject(output, Rehash(plan, target with
        {
            ProceduralInputs = target.ProceduralInputs with
            { ConsumerFamilies = families, ConsumerInventoryHash = MutationPlanJson.ComputeDirectionalFactsHash(families) }
        }));
        var sphere = target.PreservationTargets.First(p => p.Category == "render_sphere");
        var sphereIndex = int.Parse(sphere.FieldPath.AsSpan("m_skeleton.m_bones[".Length, sphere.FieldPath.IndexOf(']') - "m_skeleton.m_bones[".Length), System.Globalization.CultureInfo.InvariantCulture);
        await Reject(LocalExperimentalVisualIntegrationTests.MutateMesh(output, sphere.ResourceBlockIndex, mesh =>
            mesh["m_skeleton"]["m_bones"][sphereIndex]["m_flSphereRadius"] = (KVObject)(BitConverter.UInt32BitsToSingle(sphere.OriginalWords[0]) + 1)), plan);
        foreach (var block in target.PreservationTargets.Where(p => p.FieldPath == "$payload"))
        {
            var changed = envelope.Blocks[block.ResourceBlockIndex].Payload.ToArray(); changed[^1] ^= 1;
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPairedTransformAsync(input, Replace(block.ResourceBlockIndex, changed), plan, token));
        }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path!, token)); Assert.Equal(candidateBytes, await File.ReadAllBytesAsync(candidatePath!, token));

        int[] Move(IReadOnlyList<int> ids, int field) => (field == 0 ? ids.Where(i => i != moved) : ids.Append(moved)).Distinct().Order().ToArray();
        async Task Reject(ArtifactContent wrong, MutationPlan claimed)
        {
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(claimed));
            Assert.Equal("PAIRED_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyPairedTransformAsync(input, wrong, claimed, token))).Error.Code);
        }
        ArtifactContent Replace(int block, byte[] payload)
        {
            var rebuilt = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [block] = payload });
            return new(output.LogicalPath, ContentHash.Compute(rebuilt), rebuilt);
        }
        static Point3 Point(IReadOnlyList<uint> words, int start) => new(BitConverter.UInt32BitsToSingle(words[start]), BitConverter.UInt32BitsToSingle(words[start + 1]), BitConverter.UInt32BitsToSingle(words[start + 2]));
    }

    private static MutationPlan Rehash(MutationPlan plan, PlannedPairedTransformTarget target)
    {
        target = target with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(target) };
        var result = plan with { Operations = [plan.Operations[0] with { PairedTransformTarget = target }] };
        return result with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(result) };
    }
}

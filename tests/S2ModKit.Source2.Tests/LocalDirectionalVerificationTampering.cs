using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed partial class LocalDirectionalIntegrationTests
{
    private static async Task RejectDirectionalOutputTampering(Source2CompiledModelAdapter adapter, string codec, ArtifactContent input,
        ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        var target = plan.Operations[0].DirectionalTransformTarget!; var first = target.Buffers[0];
        var audit = target.WordAudits.Single(a => a.MemberId == first.MemberId && a.Lod == first.Lod);
        var envelope = ResourceEnvelopeReader.Read(output.Bytes);
        using var native = NativeMeshOptimizerCodec.Open(codec);
        var original = native.DecodeVertexBuffer(envelope.Blocks[first.VertexResourceBlockIndex].Payload.ToArray(), first.VertexCount, first.PositionLayout.Stride);
        foreach (var change in new[] { "active_position", "pinned_position", "protected_frame", "hidden_attribute" })
        {
            var modified = original.ToArray();
            if (change is "active_position" or "pinned_position")
            {
                var vertex = change == "active_position" ? audit.ChangedPositionIndices[0] : audit.PinnedIndices[0];
                var offset = vertex * first.PositionLayout.Stride + first.PositionLayout.Offset;
                var value = BinaryPrimitives.ReadSingleLittleEndian(modified.AsSpan(offset));
                BinaryPrimitives.WriteSingleLittleEndian(modified.AsSpan(offset), value + .01f);
            }
            else if (change == "protected_frame")
            {
                var set = target.Protection.Union.Single(s => s.Lod == first.Lod && s.MeshOrdinal == first.MeshOrdinal && s.VertexBufferOrdinal == first.VertexBufferOrdinal);
                modified[set.VertexIndices[0] * first.PackedFrameLayout.Stride + first.PackedFrameLayout.Offset] ^= 1;
            }
            else
            {
                var offset = Enumerable.Range(0, first.PositionLayout.Stride).First(i => !(i >= first.PositionLayout.Offset && i < first.PositionLayout.Offset + 12)
                    && !(i >= first.PackedFrameLayout.Offset && i < first.PackedFrameLayout.Offset + 4));
                modified[offset] ^= 1;
            }
            var tampered = Replace(first.VertexResourceBlockIndex, native.EncodeVertexBuffer(modified, first.VertexCount, first.PositionLayout.Stride, 1));
            var claimed = plan;
            if (change == "active_position")
            {
                // Coherently rehash the fake output and all affected contributor rows. A hash comparison
                // against the planner alone could accept this; independent field reconstruction must reject.
                var positionHash = Source2CompiledModelAdapter.DirectionalPositionWords(modified, first.PositionLayout, Enumerable.Range(0, first.VertexCount).ToArray());
                var closures = target.BoxClosures.Select(c =>
                {
                    var contributors = c.Contributors.Select(r => r.Lod == first.Lod && r.MeshOrdinal == first.MeshOrdinal && r.VertexBufferOrdinal == first.VertexBufferOrdinal
                        ? r with { ExpectedPositionHash = positionHash } : r).ToArray();
                    return c with { Contributors = contributors, ClosureHash = MutationPlanJson.ComputeDirectionalFactsHash(contributors) };
                }).ToArray();
                claimed = Rehash(plan, target with
                {
                    Buffers = target.Buffers.Select(b => b == first ? b with { ExpectedDecodedVertexBufferHash = ContentHash.Compute(modified), ExpectedPositionHash = positionHash } : b).ToArray(),
                    BoxClosures = closures,
                    BoxTargets = target.BoxTargets.Select(b => b with { ContributorSetHash = closures.Single(c => c.ResourceBlockIndex == b.ResourceBlockIndex && c.FieldPath == b.FieldPath).ClosureHash }).ToArray()
                });
                _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(claimed));
            }
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, tampered, claimed, token));
        }
        var excluded = target.ContextBuffers.First(c => !c.Selected);
        var snapshot = await adapter.InspectAsync(output, token);
        var excludedBuffer = snapshot.Lods.Single(l => l.Level == excluded.Lod).Meshes.Single(m => m.MeshOrdinal == excluded.MeshOrdinal).Geometry!.VertexBuffers[excluded.VertexBufferOrdinal];
        var excludedBytes = native.DecodeVertexBuffer(envelope.Blocks[excluded.VertexResourceBlockIndex].Payload.ToArray(), excluded.VertexCount, excludedBuffer.Stride);
        excludedBytes[0] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input,
            Replace(excluded.VertexResourceBlockIndex, native.EncodeVertexBuffer(excludedBytes, excluded.VertexCount, excludedBuffer.Stride, 1)), plan, token));
        var indexBytes = envelope.Blocks[first.IndexResourceBlockIndex].Payload.ToArray(); indexBytes[^1] ^= 1;
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, Replace(first.IndexResourceBlockIndex, indexBytes), plan, token));

        var scene = target.BoxTargets.First(b => b.Storage == "min_max");
        var wrongWords = scene.ExpectedWords.ToArray(); wrongWords[3] = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(wrongWords[3]) + 1);
        var wrongBox = LocalExperimentalVisualIntegrationTests.MutateMesh(output, scene.ResourceBlockIndex, mesh =>
        {
            var item = mesh["m_sceneObjects"][0];
            KvNumericMutation.ReplaceVector3(item, "m_vMaxBounds", LocalExperimentalVisualIntegrationTests.VectorWords(scene.ExpectedWords, 3),
                LocalExperimentalVisualIntegrationTests.VectorWords(wrongWords, 3), "containing but oversized box");
        });
        var oldBounds = new Bounds3(Point(scene.OriginalWords, 0), Point(scene.OriginalWords, 3));
        var wrongBounds = new Bounds3(Point(wrongWords, 0), Point(wrongWords, 3));
        var wrongGrowth = CullingEnvelopeVerifier.MeasureGrowth(oldBounds, wrongBounds).Select(g => new ExperimentalBoundsGrowth(g.Measure, g.OriginalValue, g.PlannedValue, g.Delta)).ToArray();
        var wrongPlan = Rehash(plan, target with { BoxTargets = target.BoxTargets.Select(b => b == scene ? b with { ExpectedWords = wrongWords, Growth = wrongGrowth } : b).ToArray() });
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(wrongPlan));
        Assert.Equal("DIRECTIONAL_RESULT_DRIFT", (await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, wrongBox, wrongPlan, token))).Error.Code);

        var sphere = target.PreservationTargets.First(p => p.Category == "render_sphere");
        var sphereIndex = int.Parse(sphere.FieldPath.AsSpan("m_skeleton.m_bones[".Length, sphere.FieldPath.IndexOf(']') - "m_skeleton.m_bones[".Length), System.Globalization.CultureInfo.InvariantCulture);
        var changedSphere = LocalExperimentalVisualIntegrationTests.MutateMesh(output, sphere.ResourceBlockIndex, mesh =>
            mesh["m_skeleton"]["m_bones"][sphereIndex]["m_flSphereRadius"] = new KVObject(BitConverter.UInt32BitsToSingle(sphere.OriginalWords[0]) + 1));
        await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, changedSphere, plan, token));
        using (var stream = new MemoryStream(output.Bytes.ToArray(), writable: false))
        using (var resource = new Resource { FileName = output.LogicalPath })
        {
            resource.Read(stream, verifyFileSize: true, leaveOpen: true);
            var model = resource.Blocks.OfType<Model>().Single();
            model.Data.Add("undeclared_root_change", new KVObject(true));
            using var serialized = new MemoryStream(); model.Serialize(serialized);
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, Replace(resource.Blocks.IndexOf(model), serialized.ToArray()), plan, token));
        }
        foreach (var payload in target.PreservationTargets.Where(p => p.FieldPath == "$payload"))
        {
            var bytes = envelope.Blocks[payload.ResourceBlockIndex].Payload.ToArray(); bytes[^1] ^= 1;
            await Assert.ThrowsAsync<S2ModKitException>(() => adapter.VerifyDirectionalTransformAsync(input, Replace(payload.ResourceBlockIndex, bytes), plan, token));
        }

        ArtifactContent Replace(int block, byte[] bytes)
        {
            var rebuilt = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>> { [block] = bytes });
            return new(output.LogicalPath, ContentHash.Compute(rebuilt), rebuilt);
        }
        static Point3 Point(IReadOnlyList<uint> words, int start) => new(BitConverter.UInt32BitsToSingle(words[start]), BitConverter.UInt32BitsToSingle(words[start + 1]), BitConverter.UInt32BitsToSingle(words[start + 2]));
    }
}

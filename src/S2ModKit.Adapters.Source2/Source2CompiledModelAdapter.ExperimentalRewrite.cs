using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsExperimentalTransformPlan(MutationPlan plan) =>
        plan.SchemaVersion is 2 or 3 && plan.Operations is [{ Kind: "transform_component", Version: 5 or 6, ExperimentalTransformTarget: not null }];

    private static bool CanRewriteExperimentalTransform(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsExperimentalTransformPlan(plan) || plan.InputHash != model.Artifact.ContentHash) return false;
        try
        {
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
            var source = plan.Operations[0].ExperimentalTransformTarget!.SourceBlocks;
            return source.Count == model.Artifact.Blocks.Count && source.All(block =>
                model.Artifact.Blocks.Any(actual => actual.Index == block.Index && actual.Type == block.Type && actual.ContentHash == block.InputHash));
        }
        catch (Exception exception) when (exception is S2ModKitException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private RewriteCandidate RewriteExperimentalTransform(ArtifactContent input, ModelSnapshot model,
        MutationPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            using var parsed = Parse(input, retainGeometryAnalysis: true);
            ValidateSnapshotAgreement(model, parsed.Snapshot);
            var operation = plan.Operations.Single();
            var target = operation.ExperimentalTransformTarget!;
            var request = ExperimentalRequest(input, parsed.Snapshot, operation);
            // Regeneration here detects forged/stale input plans, not output correctness.
            // Output policy is checked separately by VerifyExperimentalTransformAsync.
            var fresh = PlanExperimentalTransform(request, parsed);
            if (JsonDefaults.Serialize(fresh.ExperimentalTransformTarget) != JsonDefaults.Serialize(target)
                || !fresh.TargetBlocks.SequenceEqual(operation.TargetBlocks))
                throw ExperimentalAuditFailure("The frozen experimental plan does not reproduce from the immutable input.");

            var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
            using var codec = OpenGeometryCodec();
            foreach (var profile in ExperimentalProfiles(request, parsed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometry = target.GeometryTargets.Single(item => item.MeshOrdinal == profile.Mesh.MeshOrdinal);
                var intended = ExperimentalPositions(profile, target);
                if (ContentHash.Compute(intended) != geometry.ExpectedDecodedVertexBufferHash)
                    throw ExperimentalAuditFailure("The selected serialized positions differ from the dry-run plan.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed);
                var encoded = EncodeDeterministically(codec, intended, profile.Vertices.Snapshot, "experimental writer");
                if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride)
                    .AsSpan().SequenceEqual(intended))
                    throw ExperimentalAuditFailure("The position buffer changed during its codec round trip.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);
                replacements.Add(geometry.VertexResourceBlockIndex, encoded);
                foreach (var box in target.BoxTargets.Where(box => box.ResourceBlockIndex == profile.Mesh.BlockIndex))
                    ReplaceExperimentalBox(profile.Mesh.Block.Data, box, reverse: false);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
                replacements.Add(profile.Mesh.BlockIndex, SerializeDeterministically(profile.Mesh.Block.Serialize, "experimental MDAT"));
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
            }
            if (!operation.TargetBlocks.Select(block => block.Index).ToHashSet().SetEquals(replacements.Keys))
                throw ExperimentalAuditFailure("The replacement block inventory differs from the frozen allowlist.");
            var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
            var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes);
            _ = VerifyExperimentalTransformCore(input, output, plan, cancellationToken);
            using var reopened = Parse(output, retainGeometryAnalysis: true);
            return new RewriteCandidate(input.LogicalPath, bytes, reopened.Snapshot);
        }
        catch (S2ModKitException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException
            or NotSupportedException or OverflowException or IndexOutOfRangeException or KeyNotFoundException
            or ValveResourceFormat.Utils.UnexpectedMagicException)
        {
            throw new S2ModKitException(new S2Error("EXPERIMENTAL_REWRITE_FAILED", "source2_adapter",
                $"The experimental candidate failed before publication: {exception.Message}",
                "Reject the candidate; do not modify the immutable input.", ErrorCategory.UnsupportedCapability), exception);
        }
    }

    private static TransformPlanningRequest ExperimentalRequest(ArtifactContent input, ModelSnapshot model, PlannedOperation operation)
    {
        var target = operation.ExperimentalTransformTarget!;
        return new(input, model, new TransformComponentOperation
        {
            OperationId = operation.OperationId,
            Version = operation.Version,
            Granularity = operation.Version == 6 ? "axis_ramp_vertices" : "draw_call_vertices",
            Region = target.Region?.Selection,
            Selector = target.Selector,
            RuntimeMetadataPolicy = target.RuntimeMetadataPolicy,
            ExpectedMatchesByLod = operation.SelectedDrawCalls.GroupBy(call => call.Lod)
                .ToDictionary(group => group.Key.ToString(CultureInfo.InvariantCulture), group => group.Count()),
            ExpectedVerticesByLod = target.GeometryTargets.ToDictionary(item => item.Lod.ToString(CultureInfo.InvariantCulture), item => item.SelectedVertexCount),
            Transform = new() { Pivot = target.PivotIntent, UniformScale = target.UniformScale },
            Limits = new() { MaximumVertexDisplacement = target.DisplacementLimit },
        }, operation.SelectedDrawCalls);
    }

    private static Source2AffineProfile[] ExperimentalProfiles(TransformPlanningRequest request, ParsedModel parsed) =>
        request.SelectedDrawCalls.GroupBy(call => call.MeshOrdinal).OrderBy(group => group.Key)
            .Select(group => CreateRootBufferProfile(request, parsed, group.ToArray(), preserveAuthoredEnvelopes: true)).ToArray();

    private static byte[] ExperimentalPositions(Source2AffineProfile profile, PlannedExperimentalTransformTarget target)
    {
        if (target.Region is { } region)
        {
            var calculated = PlanRegionBuffer(profile, region.Selection, target.UniformScale, target.Pivot.Point);
            if (S2ModKit.Application.JsonDefaults.Serialize(calculated.Facts) != S2ModKit.Application.JsonDefaults.Serialize(region.Buffers.Single(b => b.MeshOrdinal == profile.Mesh.MeshOrdinal)))
                throw ExperimentalAuditFailure("Region mask or packed-frame facts drifted before rewrite.");
            return calculated.Bytes;
        }
        var result = (byte[])profile.Vertices.Decoded.Clone();
        var transform = new UniformTransform(ToAffinePoint(target.Pivot.Point), target.UniformScale, default);
        foreach (var index in profile.SelectedVertices)
            WritePosition(result, profile.Vertices.Snapshot.PositionLayout, index,
                transform.Apply(Source2GeometryAnalyzer.ReadPosition(profile.Vertices, index)));
        VerifyOnlyPositionBytesChanged(profile.Vertices.Decoded, result, profile.Vertices.Snapshot.PositionLayout, profile.SelectedVertices);
        return result;
    }

    // Only these two characterized field shapes may be changed. No arbitrary KV path interpreter.
    private static (KVObject Parent, string First, string Second) ExperimentalBoxLocation(KVObject data, PlannedExperimentalBoxTarget box)
    {
        if (box.Storage == "min_max" && box.FieldPath == "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds")
            return (ExperimentalArray(data, "m_sceneObjects")[0], "m_vMinBounds", "m_vMaxBounds");
        const string prefix = "m_skeleton.m_bones[";
        const string suffix = "].m_bbox.m_vecCenter+m_vecSize";
        if (box.Storage != "center_half_extent" || !box.FieldPath.StartsWith(prefix, StringComparison.Ordinal)
            || !box.FieldPath.EndsWith(suffix, StringComparison.Ordinal)
            || !int.TryParse(box.FieldPath.AsSpan(prefix.Length, box.FieldPath.Length - prefix.Length - suffix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            throw ExperimentalAuditFailure("A box target is outside the exact field allowlist.");
        var bones = ExperimentalArray(ExperimentalCollection(data, "m_skeleton"), "m_bones");
        if ((uint)index >= (uint)bones.Count) throw ExperimentalAuditFailure("A box target refers to an absent bone.");
        return (ExperimentalCollection(bones[index], "m_bbox"), "m_vecCenter", "m_vecSize");
    }

    private static uint[] ReadExperimentalBox(KVObject data, PlannedExperimentalBoxTarget box)
    {
        var (parent, first, second) = ExperimentalBoxLocation(data, box);
        return Words(ExperimentalVector(parent, first), ExperimentalVector(parent, second));
    }

    private static void ReplaceExperimentalBox(KVObject data, PlannedExperimentalBoxTarget box, bool reverse)
    {
        var (parent, first, second) = ExperimentalBoxLocation(data, box);
        var before = reverse ? box.ExpectedWords : box.OriginalWords;
        var after = reverse ? box.OriginalWords : box.ExpectedWords;
        KvNumericMutation.ReplaceVector3(parent, first, ExperimentalVectorWords(before, 0), ExperimentalVectorWords(after, 0), box.FieldPath);
        KvNumericMutation.ReplaceVector3(parent, second, ExperimentalVectorWords(before, 3), ExperimentalVectorWords(after, 3), box.FieldPath);
    }

    private static TransformVector3 ExperimentalVectorWords(IReadOnlyList<uint> words, int offset) => new()
    {
        X = BitConverter.UInt32BitsToSingle(words[offset]),
        Y = BitConverter.UInt32BitsToSingle(words[offset + 1]),
        Z = BitConverter.UInt32BitsToSingle(words[offset + 2]),
    };

    private static Point3 ExperimentalPointWords(IReadOnlyList<uint> words, int offset) =>
        ToAffinePoint(ExperimentalVectorWords(words, offset));

    private static S2ModKitException ExperimentalAuditFailure(string message) =>
        Errors.Verification("EXPERIMENTAL_REOPEN_AUDIT_FAILED", message, "Reject the candidate; no build or package may be published.");
}

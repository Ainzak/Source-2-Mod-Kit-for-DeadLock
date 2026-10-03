using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsEllipsoidPlan(MutationPlan plan) => plan.SchemaVersion == 4
        && plan.Operations is [{ Kind: "transform_component", Version: 7, EllipsoidTransformTarget: not null }];

    private static bool CanRewriteEllipsoid(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsEllipsoidPlan(plan) || plan.InputHash != model.Artifact.ContentHash) return false;
        try
        {
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
            var sources = plan.Operations[0].EllipsoidTransformTarget!.SourceBlocks;
            return sources.Count == model.Artifact.Blocks.Count && sources.All(b => model.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash));
        }
        catch (Exception e) when (e is S2ModKitException or ArgumentException or InvalidOperationException) { return false; }
    }

    // Selection is resolved from the immutable snapshot and frozen selector, rather than
    // trusting caller-supplied target completeness. The audit uses this read-only resolver too.
    private static TransformPlanningRequest EllipsoidRequest(ArtifactContent input, ModelSnapshot model, PlannedOperation operation)
    {
        var target = operation.EllipsoidTransformTarget!;
        var selector = target.Selector;
        var selected = model.Lods.SelectMany(lod => lod.Meshes.SelectMany(mesh => mesh.DrawCalls
            .Where(draw => selector.Kind == "material_exact" ? draw.MaterialPath == selector.MaterialPath : selector.DrawCallIds!.Contains(draw.Id, StringComparer.Ordinal))
            .Select(draw => new SelectedDrawCall(lod.Level, mesh.ResourcePath, mesh.MeshOrdinal, mesh.ResourceBlockIndex,
                draw.Id, draw.MaterialPath, draw.DrawCallOrdinal, draw.IndexStart, draw.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        if (JsonDefaults.Serialize(selected) != JsonDefaults.Serialize(operation.SelectedDrawCalls))
            throw EllipsoidDrift("The selector does not independently reproduce the frozen complete draw-call selection.");
        return new(input, model, new TransformComponentOperation
        {
            OperationId = operation.OperationId,
            Version = 7,
            Granularity = "ellipsoid_vertices",
            Transform = null!,
            Selector = selector,
            LocalTransform = target.LocalTransform,
            RuntimeMetadataPolicy = target.RuntimeMetadataPolicy,
            ExpectedMatchesByLod = selected.GroupBy(c => c.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
            ExpectedVerticesByLod = target.Buffers.ToDictionary(b => b.Lod.ToString(CultureInfo.InvariantCulture), b => b.VertexCount),
            Limits = new() { MaximumVertexDisplacement = target.DisplacementLimit },
        }, selected);
    }

    private RewriteCandidate RewriteEllipsoid(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, CancellationToken token)
    {
        try
        {
            using var parsed = Parse(input, retainGeometryAnalysis: true);
            ValidateSnapshotAgreement(snapshot, parsed.Snapshot);
            var operation = plan.Operations.Single();
            var target = operation.EllipsoidTransformTarget!;
            var request = EllipsoidRequest(input, parsed.Snapshot, operation);
            // Input regeneration catches forged plans. It is not output verification.
            var fresh = PlanEllipsoidTransform(request, parsed);
            if (JsonDefaults.Serialize(fresh.EllipsoidTransformTarget) != JsonDefaults.Serialize(target) || !fresh.TargetBlocks.SequenceEqual(operation.TargetBlocks))
                throw EllipsoidDrift("The frozen target does not reproduce from immutable source bytes.");
            var (profiles, _) = ResolveExperimentalPlanningProfiles(request, parsed);
            var math = EllipsoidMath(target.LocalTransform, target.DisplacementLimit);
            var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
            using var codec = OpenGeometryCodec();
            foreach (var profile in profiles)
            {
                token.ThrowIfCancellationRequested();
                var (intended, _, facts) = PlanEllipsoidBuffer(input, profile, math);
                if (JsonDefaults.Serialize(facts) != JsonDefaults.Serialize(target.Buffers.Single(b => b.MeshOrdinal == profile.Mesh.MeshOrdinal)))
                    throw EllipsoidDrift("Buffer words or mask facts changed before serialization.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed);
                var encoded = EncodeDeterministically(codec, intended, profile.Vertices.Snapshot, "ellipsoid writer");
                if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(intended))
                    throw EllipsoidDrift("The codec changed prescribed decoded words.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);
                replacements.Add(profile.Vertices.Snapshot.ResourceBlockIndex, encoded);
                foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == profile.Mesh.BlockIndex)) ReplaceExperimentalBox(profile.Mesh.Block.Data, box, reverse: false);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
                replacements.Add(profile.Mesh.BlockIndex, SerializeDeterministically(profile.Mesh.Block.Serialize, "ellipsoid MDAT"));
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
            }
            if (!replacements.Keys.ToHashSet().SetEquals(operation.TargetBlocks.Select(b => b.Index))) throw EllipsoidDrift("Replacement blocks exceed or omit the exact allowlist.");
            var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
            var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes);
            _ = VerifyEllipsoidCore(input, output, plan, token);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.ReopenVerified);
            using var reopened = Parse(output, retainGeometryAnalysis: true);
            return new(input.LogicalPath, bytes, reopened.Snapshot);
        }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw EllipsoidDrift($"The isolated candidate failed before publication: {e.Message}"); }
    }
}

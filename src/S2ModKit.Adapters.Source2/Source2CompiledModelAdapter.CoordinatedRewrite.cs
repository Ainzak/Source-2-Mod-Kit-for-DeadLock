using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsCoordinatedPlan(MutationPlan plan) => plan.SchemaVersion == 5
        && plan.Operations is [{ Kind: "transform_component", Version: 8, CoordinatedTransformTarget: not null }];

    private static bool CanRewriteCoordinated(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsCoordinatedPlan(plan) || plan.InputHash != model.Artifact.ContentHash) return false;
        try
        {
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
            var sources = plan.Operations[0].CoordinatedTransformTarget!.SourceBlocks;
            return sources.Count == model.Artifact.Blocks.Count && sources.All(b => model.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash));
        }
        catch (Exception e) when (e is S2ModKitException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static TransformPlanningRequest CoordinatedRequest(ArtifactContent input, ModelSnapshot model, PlannedOperation operation)
    {
        var selector = operation.CoordinatedTransformTarget!.Selector;
        var selected = model.Lods.SelectMany(lod => lod.Meshes.SelectMany(mesh => mesh.DrawCalls
            .Where(draw => selector.DrawCallIds!.Contains(draw.Id, StringComparer.Ordinal))
            .Select(draw => new SelectedDrawCall(lod.Level, mesh.ResourcePath, mesh.MeshOrdinal, mesh.ResourceBlockIndex,
                draw.Id, draw.MaterialPath, draw.DrawCallOrdinal, draw.IndexStart, draw.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        if (JsonDefaults.Serialize(selected) != JsonDefaults.Serialize(operation.SelectedDrawCalls))
            throw CoordinatedDrift("The source selector does not reproduce the exact member union.");
        return new(input, model, CoordinatedContractValidator.Operation(operation), selected);
    }

    private RewriteCandidate RewriteCoordinated(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, CancellationToken token)
    {
        try
        {
            using var parsed = Parse(input, retainGeometryAnalysis: true);
            ValidateSnapshotAgreement(snapshot, parsed.Snapshot);
            var operation = plan.Operations.Single(); var target = operation.CoordinatedTransformTarget!;
            var request = CoordinatedRequest(input, parsed.Snapshot, operation);
            var fresh = PlanCoordinatedTransform(request, parsed);
            if (JsonDefaults.Serialize(fresh.CoordinatedTransformTarget) != JsonDefaults.Serialize(target) || !fresh.TargetBlocks.SequenceEqual(operation.TargetBlocks))
                throw CoordinatedDrift("The frozen combined target does not reproduce from immutable source bytes.");
            var members = ResolveCoordinatedProfiles(request, parsed);
            var math = new CoordinatedFieldMath(target.CoordinatedTransform.Field, target.DisplacementLimit);
            var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
            using var codec = OpenGeometryCodec();
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                var (intended, _, facts) = PlanCoordinatedBuffer(input, member, math);
                var profile = member.Profile;
                if (JsonDefaults.Serialize(facts) != JsonDefaults.Serialize(target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == profile.Mesh.Lod)))
                    throw CoordinatedDrift("Member words or masks changed before encoding.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PackedFramesTransformed);
                var encoded = EncodeDeterministically(codec, intended, profile.Vertices.Snapshot, "coordinated writer");
                if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(intended))
                    throw CoordinatedDrift("The codec changed prescribed member words.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);
                replacements.Add(profile.Vertices.Snapshot.ResourceBlockIndex, encoded);
            }
            // Every member is encoded before any shared metadata is serialized. Each MDAT
            // receives the complete combined box closure exactly once; zero fields stay intact.
            foreach (var group in members.GroupBy(m => m.Profile.Mesh.BlockIndex))
            {
                token.ThrowIfCancellationRequested();
                var mesh = group.First().Profile.Mesh;
                foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == mesh.BlockIndex)) ReplaceExperimentalBox(mesh.Block.Data, box, reverse: false);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
                replacements.Add(mesh.BlockIndex, SerializeDeterministically(mesh.Block.Serialize, "coordinated MDAT"));
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
            }
            if (!replacements.Keys.ToHashSet().SetEquals(operation.TargetBlocks.Select(b => b.Index))) throw CoordinatedDrift("Replacement blocks exceed or omit the exact closure.");
            var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
            var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes);
            _ = VerifyCoordinatedCore(input, output, plan, token);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.ReopenVerified);
            using var reopened = Parse(output, retainGeometryAnalysis: true);
            return new(input.LogicalPath, bytes, reopened.Snapshot);
        }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw CoordinatedDrift($"The atomic candidate failed before publication: {e.Message}"); }
    }

    private static S2ModKitException CoordinatedDrift(string message) => Errors.Verification("COORDINATED_AUDIT_FAILED", message,
        "Reject the candidate; regenerate complete member and metadata facts from immutable input.");
}

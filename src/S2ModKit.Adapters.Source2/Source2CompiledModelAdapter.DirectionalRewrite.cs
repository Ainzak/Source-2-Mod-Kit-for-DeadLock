using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static bool IsDirectionalPlan(MutationPlan plan) => plan.SchemaVersion == 6
        && plan.Operations is [{ Kind: "transform_component", Version: 9, DirectionalTransformTarget: not null }];

    private static bool CanRewriteDirectional(ModelSnapshot model, MutationPlan plan)
    {
        if (!IsDirectionalPlan(plan) || plan.InputHash != model.Artifact.ContentHash) return false;
        try
        {
            _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
            var sources = plan.Operations[0].DirectionalTransformTarget!.SourceBlocks;
            return sources.Count == model.Artifact.Blocks.Count && sources.All(b => model.Artifact.Blocks.Any(a => a.Index == b.Index && a.Type == b.Type && a.ContentHash == b.InputHash));
        }
        catch (Exception e) when (e is S2ModKitException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static TransformPlanningRequest DirectionalRequest(ArtifactContent input, ModelSnapshot model, PlannedOperation operation)
    {
        var selector = operation.DirectionalTransformTarget!.Selector;
        var selected = model.Lods.SelectMany(lod => lod.Meshes.SelectMany(mesh => mesh.DrawCalls
            .Where(draw => selector.DrawCallIds!.Contains(draw.Id, StringComparer.Ordinal))
            .Select(draw => new SelectedDrawCall(lod.Level, mesh.ResourcePath, mesh.MeshOrdinal, mesh.ResourceBlockIndex,
                draw.Id, draw.MaterialPath, draw.DrawCallOrdinal, draw.IndexStart, draw.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        if (JsonDefaults.Serialize(selected) != JsonDefaults.Serialize(operation.SelectedDrawCalls))
            throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "Source rediscovery did not reproduce the complete member union.");
        return new(input, model, DirectionalContractValidator.Operation(operation), selected);
    }

    // Returns bytes in memory only. Application publication stays gated on a separate independent verifier.
    private RewriteCandidate RewriteDirectional(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var parsed = Parse(input, retainGeometryAnalysis: true);
            ValidateSnapshotAgreement(snapshot, parsed.Snapshot);
            var operation = plan.Operations.Single(); var target = operation.DirectionalTransformTarget!;
            var request = DirectionalRequest(input, parsed.Snapshot, operation);
            // A new immutable-source parse rechecks dependency/ownership/protection/codec facts;
            // this is writer preflight, never evidence of an independent deformation verifier.
            var fresh = PlanDirectionalTransform(request, parsed);
            if (JsonDefaults.Serialize(fresh.DirectionalTransformTarget) != JsonDefaults.Serialize(target) || !fresh.TargetBlocks.SequenceEqual(operation.TargetBlocks))
                throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "The frozen target does not reproduce from immutable source bytes.");
            var members = ResolveDirectionalProfiles(request, parsed);
            var field = target.DirectionalTransform.Field;
            var math = new DirectionalEllipsoidScale(ToAffinePoint(target.Pivot.Point), ToAffinePoint(field.OuterRadii), field.CoreFraction, ToAffinePoint(field.Scale), target.DisplacementLimit);
            var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
            var intendedBuffers = new Dictionary<int, byte[]>();
            using var codec = OpenGeometryCodec();
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested(); var p = member.Profile;
                var intended = CalculateDirectionalWords(p.Vertices.Decoded, p.Vertices.Snapshot.PositionLayout, p.PackedFrameLayout, p.Vertices.Snapshot.VertexCount, math);
                var facts = DirectionalBufferFacts(input, member, intended);
                if (JsonDefaults.Serialize(facts.Buffer) != JsonDefaults.Serialize(target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod))
                    || JsonDefaults.Serialize(facts.Audit) != JsonDefaults.Serialize(target.WordAudits.Single(a => a.MemberId == member.MemberId && a.Lod == p.Mesh.Lod)))
                    throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "Member words or masks changed before encoding.");
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PackedFramesTransformed);
                var encoded = EncodeDeterministically(codec, intended.Bytes, p.Vertices.Snapshot, "directional candidate writer");
                if (!codec.DecodeVertexBuffer(encoded, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(intended.Bytes))
                    throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "Encoded candidate did not retain the prescribed complete buffer.");
                replacements.Add(p.Vertices.Snapshot.ResourceBlockIndex, encoded);
                intendedBuffers.Add(p.Vertices.Snapshot.ResourceBlockIndex, intended.Bytes);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);
            }
            foreach (var group in members.GroupBy(m => m.Profile.Mesh.BlockIndex))
            {
                token.ThrowIfCancellationRequested(); var mesh = group.First().Profile.Mesh;
                foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == mesh.BlockIndex)) ReplaceExperimentalBox(mesh.Block.Data, box, reverse: false);
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
                replacements.Add(mesh.BlockIndex, SerializeDeterministically(mesh.Block.Serialize, "directional MDAT"));
                ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
            }
            if (!replacements.Keys.ToHashSet().SetEquals(operation.TargetBlocks.Select(b => b.Index)))
                throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "The candidate replacement inventory differs from the exact closure.");
            var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
            var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes);
            using var reopened = Parse(output, retainGeometryAnalysis: true);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.OutputReopened);
            // Audit serialized bytes against the already frozen word/box allowlist. This does not
            // independently prove the field calculation and cannot authorize a published build.
            AuditDirectionalCandidate(input, output, plan, intendedBuffers, reopened);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.ReopenVerified);
            return new(input.LogicalPath, bytes, reopened.Snapshot);
        }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", $"The unpublished atomic candidate failed: {e.Message}"); }
    }

    private void AuditDirectionalCandidate(ArtifactContent input, ArtifactContent output, MutationPlan plan,
        Dictionary<int, byte[]> intendedBuffers, ParsedModel reopened)
    {
        using var source = Parse(input, retainGeometryAnalysis: true);
        var operation = plan.Operations.Single(); var target = operation.DirectionalTransformTarget!;
        _ = VerifyExperimentalEnvelope(source, reopened.Envelope, output, target.SourceBlocks, operation.TargetBlocks);
        var members = ResolveDirectionalProfiles(DirectionalRequest(input, source.Snapshot, operation), source);
        var observed = ResolveDirectionalProfiles(DirectionalRequest(output, reopened.Snapshot, operation), reopened)
            .ToDictionary(m => (m.MemberId, m.Profile.Mesh.Lod));
        if (observed.Count != members.Length || intendedBuffers.Count != members.Length)
            throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "Serialized complete-member coverage differs.");
        foreach (var member in members)
        {
            var before = member.Profile; var after = observed[(member.MemberId, before.Mesh.Lod)].Profile;
            if (!after.Vertices.Decoded.AsSpan().SequenceEqual(intendedBuffers[before.Vertices.Snapshot.ResourceBlockIndex]))
                throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "Serialized member words differ from the intended buffer.");
        }
        foreach (var group in members.GroupBy(m => m.Profile.Mesh.MeshOrdinal))
        {
            var before = group.First().Profile; var after = observed[(group.First().MemberId, before.Mesh.Lod)].Profile;
            VerifyCoordinatedMeshStorage(before, after, group.Select(m => m.Profile.Vertices.Snapshot.Ordinal).ToHashSet());
            foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == before.Mesh.BlockIndex)) ReplaceExperimentalBox(after.Mesh.Block.Data, box, reverse: true);
            if (KvSemanticHasher.ComputeComplete(before.Mesh.Block.Data) != KvSemanticHasher.ComputeComplete(after.Mesh.Block.Data))
                throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", "MDAT changed outside the prescribed unique box fields.");
        }
    }
}

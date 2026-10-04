using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private sealed record CoordinatedResolvedBuffer(string MemberId, Source2AffineProfile Profile);

    private static CoordinatedResolvedBuffer[] ResolveCoordinatedProfiles(TransformPlanningRequest request, ParsedModel parsed)
    {
        CoordinatedContractValidator.ValidateRecipe(new() { SchemaVersion = 9, RecipeId = "coordinated", InputHash = request.Input.ContentHash, Operations = [request.Operation] });
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF"))
            throw CoordinatedUnsupported("Incomplete MDAT coverage and raw MBUF resources remain unsupported.");
        var declared = request.Operation.CoordinatedTransform!;
        var actualLods = parsed.Snapshot.Lods.Select(l => l.Level).Order().ToArray();
        if (!actualLods.SequenceEqual(declared.Members[0].Lods.Select(l => l.Lod))) throw CoordinatedUnsupported("Member maps must cover every actual LOD.");
        var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in declared.Members)
        {
            var readerOperation = new TransformComponentOperation
            {
                OperationId = member.MemberId,
                Version = 5,
                Granularity = "draw_call_vertices",
                RuntimeMetadataPolicy = request.Operation.RuntimeMetadataPolicy,
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = member.Lods.SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal).ToArray() },
                ExpectedMatchesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.DrawCallIds.Count),
                ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices),
            };
            // This invokes only the characterized read-side profile on the SAME parse.
            // It never plans a member's metadata or merges legacy output plans.
            foreach (var map in member.Lods)
            {
                var calls = request.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw CoordinatedUnsupported("A member/LOD must resolve to one complete ordinary mesh buffer.");
                var reader = new TransformPlanningRequest(request.Input, request.Model, readerOperation, calls);
                var profile = CreateRootBufferProfile(reader, parsed, calls, preserveAuthoredEnvelopes: true);
                if (BitOperations.PopCount(profile.Mesh.LodMask) != 1 || profile.SelectedVertices.Length != profile.Vertices.Snapshot.VertexCount
                    || profile.SelectedVertices.Where((v, i) => v != i).Any() || profile.SelectedVertices.Length != map.ExpectedVertices)
                    throw CoordinatedUnsupported("Shared LOD, incomplete buffer or vertex-count drift is unsupported.");
                ValidateExperimentalVertexStreams(profile);
                result.Add(new(member.MemberId, profile));
            }
        }
        if (result.Select(r => (r.Profile.Mesh.MeshOrdinal, r.Profile.Vertices.Snapshot.Ordinal)).Distinct().Count() != result.Count
            || result.Select(r => r.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count)
            throw CoordinatedUnsupported("Member write ranges overlap.");
        return result.OrderBy(r => r.Profile.Mesh.Lod).ThenBy(r => r.Profile.Mesh.MeshOrdinal).ThenBy(r => r.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }

    private TransformPlanningResult PlanCoordinatedTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var members = ResolveCoordinatedProfiles(request, parsed);
        var profiles = members.Select(m => m.Profile).ToArray();
        var (model, rootSpheres, roots) = ResolveExperimentalRootMetadata(parsed, profiles);
        var math = new CoordinatedFieldMath(request.Operation.CoordinatedTransform!.Field, request.Operation.Limits.MaximumVertexDisplacement);
        var buffers = new List<PlannedCoordinatedBuffer>();
        var afterByMesh = profiles.GroupBy(p => p.Mesh.MeshOrdinal).ToDictionary(g => g.Key, g => (Point3[])g.First().AllBeforePositions.Clone());
        var boxes = new List<PlannedExperimentalBoxTarget>();
        var zeros = new List<PlannedZeroBoneBoxPreservationTarget>();
        var zeroSpheres = new List<PlannedZeroRenderSpherePreservationTarget>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        var predicted = new Dictionary<(int Mesh, int Buffer), Point3[]>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            var (bytes, points, facts) = PlanCoordinatedBuffer(request.Input, member, math);
            var profile = member.Profile;
            var encoded = EncodeDeterministically(codec, bytes, profile.Vertices.Snapshot, "coordinated planning");
            if (!codec.DecodeVertexBuffer(encoded, profile.Vertices.Snapshot.VertexCount, profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(bytes))
                throw CoordinatedUnsupported("A member failed the exact native codec round trip.");
            buffers.Add(facts);
            points.CopyTo(afterByMesh[profile.Mesh.MeshOrdinal], profile.BufferBaseOffset);
            predicted.Add((profile.Mesh.MeshOrdinal, profile.Vertices.Snapshot.Ordinal), points);
        }
        // Metadata sees all final serialized member points simultaneously, and unchanged
        // contributors from every other buffer. Each shared field is planned exactly once.
        foreach (var group in profiles.GroupBy(p => p.Mesh.MeshOrdinal))
        {
            var first = group.First();
            var allAfter = afterByMesh[group.Key];
            boxes.Add(PlanExperimentalSceneBox(request.Input, first, allAfter));
            var selected = group.SelectMany(p => p.SelectedVertices.Select(v => p.BufferBaseOffset + v)).ToHashSet();
            var affected = first.Metadata.BoneBounds.Where(b => b.InfluencedVertices.Any(selected.Contains)).ToArray();
            if (affected.Length == 0) throw CoordinatedUnsupported("Complete affected bone contributor closure is missing.");
            foreach (var bone in affected)
            {
                if (bone.LocalBoundsSize.X > 0 && bone.LocalBoundsSize.Y > 0 && bone.LocalBoundsSize.Z > 0)
                    boxes.Add(PlanExperimentalBoneBox(request.Input, first, bone, allAfter));
                else
                {
                    var (zero, sphere) = CoordinatedZeroTargets(request.Input, parsed, first, bone, request.Operation);
                    zeros.Add(zero);
                    if (sphere is not null) zeroSpheres.Add(sphere);
                }
            }
            AddCoordinatedRenderSphereTargets(parsed, first, affected.Select(b => b.BoneIndex).ToHashSet(), zeroSpheres, preserved);
        }
        AddExperimentalPlanningRootSpheres(parsed, model, rootSpheres, roots, preserved);
        AddExperimentalBlockPreservation(parsed, preserved);
        var seams = ResolveCoordinatedSeams(parsed, members, predicted);
        var sources = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        var target = new PlannedCoordinatedTransformTarget(request.Input.ContentHash, request.Input.ContentHash,
            "root_coordinated_fields_visual", 1, "retain_expand_boxes_preserve_runtime_coordinated", 1,
            request.Operation.RuntimeMetadataPolicy!, request.Operation.ZeroBoneBoxPolicy!, request.Operation.ZeroRenderSpherePolicy!,
            request.Operation.Selector, request.Operation.CoordinatedTransform!, math.Proof, buffers.Max(b => b.MaximumDisplacement), request.Operation.Limits.MaximumVertexDisplacement,
            buffers, boxes.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            zeros.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            zeroSpheres.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            preserved.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), sources, seams);
        target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
        CoordinatedContractValidator.ValidateTarget(target);
        return new([], [], buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Distinct().Order().Select(i => sources[i]).ToArray())
        { CoordinatedTransformTarget = target };
    }

    private static S2ModKitException CoordinatedUnsupported(string message) => Errors.Unsupported("COORDINATED_PROFILE_UNSUPPORTED", message,
        "Use complete exclusively owned ordinary buffers with one common field and explicit preservation policies; no partial result or legacy fallback.");
}

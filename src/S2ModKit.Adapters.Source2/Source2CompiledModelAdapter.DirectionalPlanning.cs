using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static CoordinatedResolvedBuffer[] ResolveDirectionalProfiles(TransformPlanningRequest request, ParsedModel parsed)
    {
        DirectionalContractValidator.ValidateRecipe(new() { SchemaVersion = 10, RecipeId = "directional", InputHash = request.Input.ContentHash, Operations = [request.Operation] });
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1))
            throw DirectionalFailure("EXPERIMENTAL_LAYOUT_UNSUPPORTED", "Incomplete, shared-LOD and raw MBUF source context is unsupported.");
        var declared = request.Operation.DirectionalTransform!;
        if (!parsed.Snapshot.Lods.Select(l => l.Level).Order().SequenceEqual(declared.Members[0].Lods.Select(l => l.Lod)))
            throw DirectionalFailure("LOD_COVERAGE_INCOMPLETE", "Member maps must cover every actual LOD.");
        var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in declared.Members)
        {
            var readerOperation = new TransformComponentOperation
            {
                Version = 5,
                Granularity = "draw_call_vertices",
                RuntimeMetadataPolicy = request.Operation.RuntimeMetadataPolicy,
                ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices),
            };
            foreach (var map in member.Lods)
            {
                var calls = request.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw DirectionalFailure("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Each member/LOD must resolve to exactly one complete ordinary buffer.");
                var profile = CreateRootBufferProfile(new(request.Input, request.Model, readerOperation, calls), parsed, calls,
                    preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true);
                ValidateExperimentalVertexStreams(profile);
                result.Add(new(member.MemberId, profile));
            }
        }
        if (result.Select(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)).Distinct().Count() != result.Count
            || result.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count)
            throw DirectionalFailure("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Member vertex blocks or buffer ownership overlap.");
        return result.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }

    private TransformPlanningResult PlanDirectionalTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var members = ResolveDirectionalProfiles(request, parsed);
        var profiles = members.Select(m => m.Profile).ToArray();
        // Retains whole-buffer procedural rejection before considering a pinned field or assertions.
        var (model, rootSpheres, roots) = ResolveExperimentalRootMetadata(parsed, profiles);
        var field = request.Operation.DirectionalTransform!.Field;
        var bounds = members.GroupBy(m => m.Profile.Mesh.Lod).Select(g => new AffineSelectionBoundsEvidence(g.Key,
            ToAffineBounds(Bounds3.FromPoints(g.SelectMany(m => m.Profile.SelectedVertices.Select(v => Source2GeometryAnalyzer.ReadPosition(m.Profile.Vertices, v))).ToArray())),
            MutationPlanJson.ComputeDirectionalFactsHash(g.Select(m => new { m.MemberId, m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal, m.Profile.VertexSetHash }).ToArray()))).ToArray();
        var boneEvidence = profiles.SelectMany(CreateBoneEvidence).ToArray();
        if (field.Pivot.Kind == "bone_origin")
        {
            // Resolve on every applicable member, then require exactly the same identity/matrix.
            var pivots = members.GroupBy(m => m.MemberId).Select(g => new AffineEvidenceResolver().ResolvePivot(new(field.Pivot,
                g.Select(m => m.Profile.Mesh.Lod).ToArray(), [], g.SelectMany(m => CreateBoneEvidence(m.Profile)).ToArray()))).ToArray();
            if (pivots.Select(JsonDefaults.Serialize).Distinct(StringComparer.Ordinal).Count() != 1)
                throw DirectionalFailure("AFFINE_PIVOT_DRIFT", "Named bone pivot evidence differs between members.");
            boneEvidence = profiles.DistinctBy(p => p.Mesh.Lod).SelectMany(CreateBoneEvidence).ToArray();
        }
        var resolved = new AffineEvidenceResolver().ResolvePivot(new(field.Pivot, parsed.Snapshot.Lods.Select(l => l.Level).ToArray(), bounds, boneEvidence));
        var pivot = new DirectionalPivotEvidence(resolved.Kind, resolved.Point, resolved.CoordinateSpace, resolved.SourceIdentity, resolved.SourceHash, resolved.ReferenceLod);
        var math = new DirectionalEllipsoidScale(ToAffinePoint(pivot.Point), ToAffinePoint(field.OuterRadii), field.CoreFraction, ToAffinePoint(field.Scale), request.Operation.Limits.MaximumVertexDisplacement);
        var calculations = new Dictionary<(int Mesh, int Buffer), DirectionalWordCalculation>();
        var buffers = new List<PlannedCoordinatedBuffer>(); var audits = new List<DirectionalWordAudit>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            var p = member.Profile;
            var c = CalculateDirectionalWords(p.Vertices.Decoded, p.Vertices.Snapshot.PositionLayout, p.PackedFrameLayout, p.Vertices.Snapshot.VertexCount, math);
            if (c.ChangedPositions.Length == 0) throw DirectionalFailure("DIRECTIONAL_EMPTY_LOD_EFFECT", $"Member {member.MemberId}, LOD {p.Mesh.Lod} has no stored position effect.");
            try { ValidateRegionTriangles(p, c.Points); }
            catch (ArgumentException e) { throw DirectionalFailure("DIRECTIONAL_TRIANGLE_INVALID", $"Member {member.MemberId}, LOD {p.Mesh.Lod}: {e.Message}"); }
            var encoded = EncodeDeterministically(codec, c.Bytes, p.Vertices.Snapshot, "directional planning");
            if (!codec.DecodeVertexBuffer(encoded, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(c.Bytes))
                throw DirectionalFailure("EXPERIMENTAL_CODEC_ROUNDTRIP_FAILED", "Directional words did not survive the native round trip.");
            calculations.Add((p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal), c);
            var facts = DirectionalBufferFacts(request.Input, member, c); buffers.Add(facts.Buffer); audits.Add(facts.Audit);
        }
        var context = ResolveDirectionalContext(request.Input, parsed, model, members, calculations);
        var protection = ResolveDirectionalProtection(request.Operation.DirectionalTransform.Protection, context,
            KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton")), model.Skeleton.Bones.Select(b => b.Name).ToArray());
        var coincidences = DirectionalCoincidences(context);
        var (boxes, closures, preserved) = PlanDirectionalMetadata(request.Input, parsed, members, calculations, context);
        AddExperimentalPlanningRootSpheres(parsed, model, rootSpheres, roots, preserved);
        AddExperimentalBlockPreservation(parsed, preserved);
        var sources = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        var target = new PlannedDirectionalTransformTarget(request.Input.ContentHash, request.Input.ContentHash,
            "root_protected_directional_ellipsoid_visual", 1, "retain_expand_boxes_preserve_runtime_directional", 1,
            request.Operation.RuntimeMetadataPolicy!, request.Operation.ZeroBoneBoxPolicy!, request.Operation.ZeroRenderSpherePolicy!,
            request.Operation.Selector, request.Operation.DirectionalTransform, pivot, DirectionalContractValidator.Certificate(field),
            buffers.Max(b => b.MaximumDisplacement), request.Operation.Limits.MaximumVertexDisplacement, buffers, audits, protection,
            context.Select(c => c.Facts).ToArray(), coincidences,
            boxes.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            closures.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            preserved.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), sources);
        target = target with { TargetFingerprint = MutationPlanJson.ComputeDirectionalTargetFingerprint(target) };
        DirectionalContractValidator.ValidateTarget(target);
        return new([], [], buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex }).Distinct().Order().Select(i => sources[i]).ToArray())
        { DirectionalTransformTarget = target };
    }
}

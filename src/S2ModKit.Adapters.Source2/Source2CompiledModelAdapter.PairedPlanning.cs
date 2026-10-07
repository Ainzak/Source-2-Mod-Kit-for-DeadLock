using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private TransformPlanningResult PlanPairedTransform(TransformPlanningRequest request, ParsedModel parsed)
    {
        var members = ResolvePairedProfiles(request, parsed);
        var intent = request.Operation.PairedTransform!;
        var math = PairedMath(intent, request.Operation.Limits.MaximumVertexDisplacement);
        var model = parsed.Resource.Blocks.OfType<Model>().Single();
        if (HasMorphData(model.Data) || model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds"))
            throw DirectionalFailure("EXPERIMENTAL_ROOT_METADATA_UNSUPPORTED", "Root bounds or morph data require a separate update rule.");
        var calculations = new Dictionary<(int Mesh, int Buffer), DirectionalWordCalculation>();
        var buffers = new List<PlannedCoordinatedBuffer>(); var audits = new List<DirectionalWordAudit>();
        var dispatch = new List<PairedBufferDispatch>(); var faces = new List<PairedSourceTriangleFacts>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            var p = member.Profile;
            var c = CalculatePairedWords(p.Vertices.Decoded, p.Vertices.Snapshot.PositionLayout, p.PackedFrameLayout,
                p.Vertices.Snapshot.VertexCount, math, intent.Fields.Select(f => f.FieldId).ToArray(), member.MemberId, p.Mesh.Lod);
            if (c.Combined.ChangedPositions.Length == 0) throw DirectionalFailure("PAIRED_EMPTY_LOD_EFFECT", "Every member/LOD must change stored positions.");
            var encoded = EncodeDeterministically(codec, c.Combined.Bytes, p.Vertices.Snapshot, "paired planning");
            if (!codec.DecodeVertexBuffer(encoded, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(c.Combined.Bytes))
                throw DirectionalFailure("EXPERIMENTAL_CODEC_ROUNDTRIP_FAILED", "Paired words did not survive the codec round trip.");
            calculations.Add((p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal), c.Combined);
            var facts = DirectionalBufferFacts(request.Input, member, c.Combined);
            buffers.Add(facts.Buffer); audits.Add(facts.Audit); dispatch.Add(c.Dispatch); faces.Add(PairedTriangles(member, c.Combined));
        }
        foreach (var lod in intent.Members[0].Lods)
            foreach (var field in intent.Fields)
                if (!dispatch.Where(d => d.Lod == lod.Lod).SelectMany(d => d.Fields).Any(f => f.FieldId == field.FieldId && f.ChangedPositionIndices.Count > 0))
                    throw DirectionalFailure("PAIRED_EMPTY_LOD_EFFECT", "Each partner must change a stored position in every LOD.");
        var context = ResolveDirectionalContext(request.Input, parsed, model, members, calculations, pairedPreservation: true);
        var skeleton = ExperimentalCollection(model.Data, "m_modelSkeleton");
        var protection = ResolveDirectionalProtection(intent.Protection, context, KvSemanticHasher.ComputeComplete(skeleton), model.Skeleton.Bones.Select(b => b.Name).ToArray());
        var procedural = ResolvePairedProcedural(parsed, model, context);
        var coincidences = DirectionalCoincidences(context);
        var (boxes, closures, preserved) = PlanDirectionalMetadata(request.Input, parsed, members, calculations, context, changedContributorsOnly: true);
        var roots = new HashSet<int>();
        foreach (var c in context.Where(c => c.Facts.Selected))
        {
            var changed = calculations[(c.Facts.MeshOrdinal, c.Facts.VertexBufferOrdinal)];
            var set = changed.ChangedPositions.Concat(changed.ChangedFrames).ToHashSet();
            foreach (var root in c.RootContributors.Where(r => r.Value.Any(set.Contains))) roots.Add(root.Key);
        }
        AddExperimentalPlanningRootSpheres(parsed, model, ExperimentalArray(skeleton, "m_boneSphere"), roots, preserved);
        AddExperimentalBlockPreservation(parsed, preserved);
        var fields = intent.Fields.Select(f =>
        {
            var pivot = new AffineEvidenceResolver().ResolvePivot(new(f.Field.Pivot, parsed.Snapshot.Lods.Select(l => l.Level).ToArray(), [], []));
            return new PlannedPairedField(f.FieldId, new(pivot.Kind, pivot.Point, pivot.CoordinateSpace, pivot.SourceIdentity, pivot.SourceHash, pivot.ReferenceLod),
                DirectionalContractValidator.Certificate(f.Field));
        }).ToArray();
        var sources = parsed.Envelope.Blocks.Select(b => new PlannedTargetBlock(b.Index, b.Type, ContentHash.Compute(b.Payload.Span))).ToArray();
        var target = new PlannedPairedTransformTarget(request.Input.ContentHash, request.Input.ContentHash,
            "root_paired_directional_preservation_visual", 1, "retain_expand_boxes_preserve_runtime_paired", 1,
            request.Operation.RuntimeMetadataPolicy!, request.Operation.ZeroBoneBoxPolicy!, request.Operation.ZeroRenderSpherePolicy!,
            request.Operation.SourceTrianglePolicy!, request.Operation.ProceduralInputPolicy!, request.Operation.Selector, intent,
            fields, PairedContractValidator.Separation(intent.Fields), buffers.Max(b => b.MaximumDisplacement), request.Operation.Limits.MaximumVertexDisplacement,
            buffers, dispatch, audits, protection, context.Select(c => c.Facts).ToArray(), coincidences, faces, procedural,
            boxes.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            closures.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            preserved.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), sources);
        target = target with { TargetFingerprint = MutationPlanJson.ComputePairedTargetFingerprint(target) };
        PairedContractValidator.ValidateTarget(target);
        return new([], [], buffers.SelectMany(b => new[] { b.ResourceBlockIndex, b.VertexResourceBlockIndex })
            .Concat(boxes.Select(b => b.ResourceBlockIndex)).Distinct().Order().Select(i => sources[i]).ToArray())
        { PairedTransformTarget = target };
    }
}

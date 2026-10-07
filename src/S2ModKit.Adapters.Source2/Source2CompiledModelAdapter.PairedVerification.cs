using S2ModKit.Application;
using S2ModKit.Domain;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<PairedTransformVerification> VerifyPairedTransformAsync(ArtifactContent input, ArtifactContent output,
        MutationPlan plan, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(VerifyPairedResource(input, output, plan, cancellationToken)); }
        catch (S2ModKitException e) when (e.Error.Code is "DIRECTIONAL_RESULT_DRIFT" or "EXPERIMENTAL_RESULT_DRIFT")
        { throw PairedDrift(e.Message); }
        catch (S2ModKitException) { throw; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw PairedDrift($"Independent paired reconstruction failed: {e.Message}"); }
    }

    private static S2ModKitException PairedDrift(string message) => Errors.Verification("PAIRED_RESULT_DRIFT", message,
        "Reject this output; incomplete or mismatched observations cannot authorize publication.");

    private static void SamePaired<T>(T observed, T expected, string fact)
    {
        if (JsonDefaults.Serialize(observed) != JsonDefaults.Serialize(expected)) throw PairedDrift($"Independent {fact} differs from the frozen target.");
    }

    private PairedTransformVerification VerifyPairedResource(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsPairedPlan(plan) || ContentHash.Compute(input.Bytes.Span) != input.ContentHash || input.ContentHash != plan.InputHash
            || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.LogicalPath != output.LogicalPath)
            throw PairedDrift("Immutable source/output identities disagree with the plan.");
        using var source = Parse(input, retainGeometryAnalysis: true);
        using var observed = Parse(output, retainGeometryAnalysis: true);
        var operation = plan.Operations.Single(); var target = operation.PairedTransformTarget!;
        var allowed = VerifyExperimentalEnvelope(source, observed.Envelope, output, target.SourceBlocks, operation.TargetBlocks);
        var members = DiscoverPairedVerificationMembers(input, source, operation);
        var outputMembers = DiscoverPairedVerificationMembers(output, observed, operation);
        if (members.Length != target.Buffers.Count || members.Length != outputMembers.Length
            || !allowed.SetEquals(members.SelectMany(m => new[] { m.Profile.Mesh.BlockIndex, m.Profile.Vertices.Snapshot.ResourceBlockIndex })))
            throw PairedDrift("Source/output ownership or complete mutation closure differs.");
        var actual = outputMembers.ToDictionary(m => (m.MemberId, m.Profile.Mesh.Lod));
        var model = source.Resource.Blocks.OfType<Model>().Single();
        if (HasMorphData(model.Data) || model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds"))
            throw PairedDrift("Root morph or derived bounds are outside the admitted profile.");
        var fields = target.PairedTransform.Fields.Select(f =>
        {
            var pivot = new AffineEvidenceResolver().ResolvePivot(new(f.Field.Pivot, source.Snapshot.Lods.Select(l => l.Level).ToArray(), [], []));
            return new PlannedPairedField(f.FieldId, new(pivot.Kind, pivot.Point, pivot.CoordinateSpace, pivot.SourceIdentity, pivot.SourceHash, pivot.ReferenceLod),
                DirectionalContractValidator.Certificate(f.Field));
        }).ToArray();
        SamePaired(fields, target.Fields, "source pivots and whole-field certificates");
        var separation = AuditPairSeparation(target.PairedTransform.Fields);
        SamePaired(separation, target.Separation, "exact first separating axis");
        var buffers = new List<CoordinatedBufferObservation>(); var audits = new List<DirectionalWordAudit>();
        var dispatch = new List<PairedBufferDispatch>(); var triangles = new List<PairedSourceTriangleFacts>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested(); var current = actual[(member.MemberId, member.Profile.Mesh.Lod)];
            var words = ReconstructPairedBuffer(input, member, current, target);
            SamePaired(words.Facts, target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == member.Profile.Mesh.Lod), "complete buffer words and effects");
            SamePaired(words.Audit, target.WordAudits.Single(b => b.MemberId == member.MemberId && b.Lod == member.Profile.Mesh.Lod), "pinned and immutable word audit");
            SamePaired(words.Dispatch, target.Dispatch.Single(b => b.MemberId == member.MemberId && b.Lod == member.Profile.Mesh.Lod), "original-source field dispatch");
            var bytes = current.Profile.Vertices.Decoded;
            var encoded = EncodeDeterministically(codec, bytes, current.Profile.Vertices.Snapshot, "independent paired verification");
            if (!codec.DecodeVertexBuffer(encoded, current.Profile.Vertices.Snapshot.VertexCount, current.Profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(bytes))
                throw PairedDrift("Observed buffer failed the native codec round trip.");
            var faces = AuditPairedTriangles(member, current);
            SamePaired(faces, target.SourceTriangles.Single(f => f.MemberId == member.MemberId && f.Lod == member.Profile.Mesh.Lod), "complete source/output face partitions");
            var b = words.Facts;
            buffers.Add(new(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal, b.ExpectedPositionHash, b.ExpectedPackedFrameHash,
                b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement));
            audits.Add(words.Audit); dispatch.Add(words.Dispatch); triangles.Add(faces);
        }
        foreach (var lod in members.Select(m => m.Profile.Mesh.Lod).Distinct())
            foreach (var f in target.PairedTransform.Fields)
                if (!dispatch.Where(d => d.Lod == lod).SelectMany(d => d.Fields).Any(row => row.FieldId == f.FieldId && row.ChangedPositionIndices.Count > 0))
                    throw PairedDrift("A partner has no stored position effect in an actual LOD.");
        if (buffers.Max(b => b.MaximumDisplacement) != target.MaximumDisplacement) throw PairedDrift("Combined stored displacement differs.");
        var context = ReadDirectionalVerificationContext(input, source, observed, model, members, pairedPreservation: true);
        SamePaired(context.Select(c => c.Facts).ToArray(), target.ContextBuffers, "complete source context and skinning");
        var protection = AuditDirectionalProtection(target.PairedTransform.Protection, context,
            KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton")), model.Skeleton.Bones.Select(b => b.Name).ToArray());
        SamePaired(protection, target.Protection, "source-derived protection and actual fixed words");
        var coincidences = AuditDirectionalCoincidences(context);
        SamePaired(coincidences, target.Coincidences, "all authored coincidence cohorts");
        var procedural = AuditPairedProcedural(source, model, context);
        SamePaired(procedural, target.ProceduralInputs, "procedural fixed words and complete consumer inventory");
        var (boxes, closures, preserved) = AuditPairedMetadata(source, observed, members, actual, context, target);
        SamePaired(closures, target.BoxClosures, "complete final-state box closure");
        token.ThrowIfCancellationRequested();
        var boundaries = PairedContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
            .Select(b => b.Status == "not_applicable" ? b with { Status = "passed", Summary = "Independently reconstructed from immutable source and reopened output." } : b).ToArray();
        return new(boundaries, new(buffers, dispatch, audits, protection, context.Select(c => c.Facts).ToArray(), coincidences, closures,
            fields, separation, triangles, procedural), boxes, preserved);
    }
}

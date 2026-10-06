using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<DirectionalTransformVerification> VerifyDirectionalTransformAsync(ArtifactContent input, ArtifactContent output,
        MutationPlan plan, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(VerifyDirectionalResource(input, output, plan, cancellationToken)); }
        catch (S2ModKitException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException)
        { throw DirectionalDrift($"Independent directional reconstruction failed: {exception.Message}"); }
    }

    private static S2ModKitException DirectionalDrift(string message) => Errors.Verification("DIRECTIONAL_RESULT_DRIFT", message,
        "Reject this output; no build or package may be published from incomplete or mismatched observations.");

    private DirectionalTransformVerification VerifyDirectionalResource(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsDirectionalPlan(plan) || ContentHash.Compute(input.Bytes.Span) != input.ContentHash || input.ContentHash != plan.InputHash
            || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.LogicalPath != output.LogicalPath)
            throw DirectionalDrift("Immutable source/output identities disagree with the plan.");
        using var source = Parse(input, retainGeometryAnalysis: true);
        using var observed = Parse(output, retainGeometryAnalysis: true);
        var operation = plan.Operations.Single(); var target = operation.DirectionalTransformTarget!;
        var allowed = VerifyExperimentalEnvelope(source, observed.Envelope, output, target.SourceBlocks, operation.TargetBlocks);
        var members = DiscoverDirectionalVerificationMembers(input, source, operation);
        var actualMembers = DiscoverDirectionalVerificationMembers(output, observed, operation);
        if (members.Length != target.Buffers.Count || members.Length != actualMembers.Length
            || !allowed.SetEquals(members.SelectMany(m => new[] { m.Profile.Mesh.BlockIndex, m.Profile.Vertices.Snapshot.ResourceBlockIndex })))
            throw DirectionalDrift("Source/output ownership or complete mutation closure differs.");
        var actual = actualMembers.ToDictionary(m => (m.MemberId, m.Profile.Mesh.Lod));
        var profiles = members.Select(m => m.Profile).ToArray();
        var (model, _, _) = ResolveExperimentalRootMetadata(source, profiles);
        _ = ResolveExperimentalRootMetadata(observed, actualMembers.Select(m => m.Profile).ToArray());
        var pivot = DiscoverDirectionalVerificationPivot(source, members, target.DirectionalTransform.Field.Pivot);
        SameDirectional(pivot, target.Pivot, "typed source pivot");
        var field = target.DirectionalTransform.Field;
        var reconstruction = new DirectionalFieldReconstruction(ToAffinePoint(pivot.Point), ToAffinePoint(field.OuterRadii),
            field.CoreFraction, ToAffinePoint(field.Scale), target.DisplacementLimit);
        var certificate = DirectionalContractValidator.Certificate(field);
        SameDirectional(certificate, target.Certificate, "whole-field certificate");
        var audits = new List<DirectionalWordAudit>(); var buffers = new List<CoordinatedBufferObservation>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            var current = actual[(member.MemberId, member.Profile.Mesh.Lod)];
            var (facts, audit) = ReconstructDirectionalBuffer(input, member, current, reconstruction);
            SameDirectional(facts, target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == member.Profile.Mesh.Lod), "complete buffer facts");
            SameDirectional(audit, target.WordAudits.Single(b => b.MemberId == member.MemberId && b.Lod == member.Profile.Mesh.Lod), "raw word audit");
            var bytes = current.Profile.Vertices.Decoded;
            var encoded = EncodeDeterministically(codec, bytes, current.Profile.Vertices.Snapshot, "independent directional verification");
            if (!codec.DecodeVertexBuffer(encoded, current.Profile.Vertices.Snapshot.VertexCount, current.Profile.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(bytes))
                throw DirectionalDrift("Observed complete buffer failed the native codec round trip.");
            audits.Add(audit);
            buffers.Add(new(facts.MemberId, facts.Lod, facts.MeshOrdinal, facts.VertexBufferOrdinal,
                facts.ExpectedPositionHash, facts.ExpectedPackedFrameHash, facts.ExpectedDecodedVertexBufferHash,
                facts.MaskHash, facts.WeightHash, facts.MaximumDisplacement));
        }
        if (buffers.Max(b => b.MaximumDisplacement) != target.MaximumDisplacement) throw DirectionalDrift("Combined stored displacement differs.");
        var context = ReadDirectionalVerificationContext(input, source, observed, model, members);
        SameDirectional(context.Select(c => c.Facts).ToArray(), target.ContextBuffers, "complete authored source context");
        var protection = AuditDirectionalProtection(target.DirectionalTransform.Protection, context,
            KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton")), model.Skeleton.Bones.Select(b => b.Name).ToArray());
        SameDirectional(protection, target.Protection, "source-derived protected assertions and union");
        var coincidences = AuditDirectionalCoincidences(context);
        SameDirectional(coincidences, target.Coincidences, "complete binary32 source coincidence inventory");
        var (boxes, closures, preservation) = AuditDirectionalMetadata(source, observed, members, actual, context, target);
        SameDirectional(closures, target.BoxClosures, "complete final-state box closure");
        var boundaries = DirectionalContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
            .Select(b => b.Status == "not_applicable" ? b with { Status = "passed", Summary = "Independently reconstructed from immutable source and reopened output words." } : b).ToArray();
        return new(boundaries, new(buffers, audits, protection, context.Select(c => c.Facts).ToArray(), coincidences, closures, certificate), boxes, preservation);
    }

    private static void SameDirectional<T>(T observed, T expected, string fact)
    {
        if (JsonDefaults.Serialize(observed) != JsonDefaults.Serialize(expected)) throw DirectionalDrift($"Independent {fact} differs from the frozen target.");
    }

    // Only the characterized ordinary-buffer reader is shared; no planner/member resolver is called.
    private static CoordinatedResolvedBuffer[] DiscoverDirectionalVerificationMembers(ArtifactContent input, ParsedModel parsed, PlannedOperation operation)
    {
        var intent = DirectionalContractValidator.Operation(operation);
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1))
            throw DirectionalDrift("Incomplete/excluded/shared-LOD or raw MBUF storage is outside this profile.");
        if (!parsed.Snapshot.Lods.Select(l => l.Level).Order().SequenceEqual(intent.DirectionalTransform!.Members[0].Lods.Select(l => l.Lod)))
            throw DirectionalDrift("Source/output all-present LOD coverage differs.");
        var selected = parsed.Snapshot.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls
            .Where(d => intent.Selector.DrawCallIds!.Contains(d.Id, StringComparer.Ordinal))
            .Select(d => new SelectedDrawCall(l.Level, m.ResourcePath, m.MeshOrdinal, m.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        SameDirectional(selected, operation.SelectedDrawCalls, "exact draw-call selection");
        var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in intent.DirectionalTransform.Members)
            foreach (var map in member.Lods)
            {
                var calls = selected.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw DirectionalDrift("Member mapping is incomplete or ambiguous.");
                var reader = new TransformComponentOperation
                {
                    Version = 5,
                    Granularity = "draw_call_vertices",
                    RuntimeMetadataPolicy = intent.RuntimeMetadataPolicy,
                    ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices)
                };
                var profile = CreateRootBufferProfile(new(input, parsed.Snapshot, reader, calls), parsed, calls, preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true);
                ValidateExperimentalVertexStreams(profile);
                result.Add(new(member.MemberId, profile));
            }
        if (result.Select(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)).Distinct().Count() != result.Count
            || result.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count)
            throw DirectionalDrift("Selected member storage aliases another member.");
        return result.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }

    private static DirectionalPivotEvidence DiscoverDirectionalVerificationPivot(ParsedModel parsed, CoordinatedResolvedBuffer[] members, TransformPivot intent)
    {
        var bounds = members.GroupBy(m => m.Profile.Mesh.Lod).Select(g => new AffineSelectionBoundsEvidence(g.Key,
            ToAffineBounds(Bounds3.FromPoints(g.SelectMany(m => Enumerable.Range(0, m.Profile.Vertices.Snapshot.VertexCount)
                .Select(v => Source2GeometryAnalyzer.ReadPosition(m.Profile.Vertices, v))).ToArray())),
            MutationPlanJson.ComputeDirectionalFactsHash(g.Select(m => new { m.MemberId, m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal, m.Profile.VertexSetHash }).ToArray()))).ToArray();
        var bones = members.SelectMany(m => CreateBoneEvidence(m.Profile)).ToArray();
        if (intent.Kind == "bone_origin")
        {
            var identities = members.GroupBy(m => m.MemberId).Select(g => new AffineEvidenceResolver().ResolvePivot(new(intent,
                g.Select(m => m.Profile.Mesh.Lod).ToArray(), [], g.SelectMany(m => CreateBoneEvidence(m.Profile)).ToArray()))).ToArray();
            if (identities.Select(JsonDefaults.Serialize).Distinct(StringComparer.Ordinal).Count() != 1) throw DirectionalDrift("Bone pivot identity differs across members.");
            bones = members.Select(m => m.Profile).DistinctBy(p => p.Mesh.Lod).SelectMany(CreateBoneEvidence).ToArray();
        }
        var resolved = new AffineEvidenceResolver().ResolvePivot(new(intent, parsed.Snapshot.Lods.Select(l => l.Level).ToArray(), bounds, bones));
        return new(resolved.Kind, resolved.Point, resolved.CoordinateSpace, resolved.SourceIdentity, resolved.SourceHash, resolved.ReferenceLod);
    }
}

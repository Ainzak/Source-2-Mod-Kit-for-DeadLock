using System.Buffers.Binary;
using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IPositionImportResourceAdapter
{
    public async Task<PlannedOperation> PlanPositionImportAsync(ArtifactContent input, ModelSnapshot snapshot, ImportPositionsOperation op, PositionImportAssets assets, CancellationToken token = default)
    {
        var selection = PositionImportContract.Selection(op, assets);
        var source = await ReadPositionAuthoringSourceAsync(input, selection.Members, token).ConfigureAwait(false);
        var validation = PositionReturnValidator.Validate(source, selection, assets.Bundle, op.PositionImport.Binding.BundleSha256, assets.Delta, EncodePositionAuthoringFrame);
        using var parsed = Parse(input, retainGeometryAnalysis: true); ValidateSnapshotAgreement(snapshot, parsed.Snapshot);
        var members = ReadPositionImportMembers(input, parsed, op.PositionImport);
        var calculations = new Dictionary<(int Mesh, int Buffer), DirectionalWordCalculation>(); var buffers = new List<PlannedPositionImportBuffer>(); var faces = new List<PairedSourceTriangleFacts>();
        var delta = PositionAuthoringJson.Read<PositionAuthoringDelta>(assets.Delta);
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested(); var p = member.Profile;
            var b = source.Buffers.Single(b => b.SelectedMemberId == member.MemberId && b.Facts.Lod == p.Mesh.Lod);
            var r = validation.Buffers.Single(r => r.MemberId == member.MemberId && r.Lod == p.Mesh.Lod);
            var returned = delta.Members.Single(d => d.MemberId == PositionAuthoringJson.BufferId(p.Mesh.Lod, p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal));
            var words = ApplyReturnedWords(p, returned, r);
            if (!words.Bytes.AsSpan().SequenceEqual(p.Vertices.Decoded))
            {
                var encoded = EncodeDeterministically(codec, words.Bytes, p.Vertices.Snapshot, "position import planning");
                if (!codec.DecodeVertexBuffer(encoded, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(words.Bytes)) throw ImportDrift("Codec round trip lost returned words.");
            }
            calculations.Add((p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal), words);
            buffers.Add(ImportBufferFacts(codec.Identity, member, b, r, words.Bytes, selection)); faces.Add(ImportTriangleFacts(member, b.Positions, words.Points, b.Triangles));
        }
        var model = parsed.Resource.Blocks.OfType<Model>().Single();
        var context = ResolveDirectionalContext(input, parsed, model, members, calculations, pairedPreservation: true);
        var edited = members.Where(m => calculations[(m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)].ChangedPositions.Length + calculations[(m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)].ChangedFrames.Length > 0).ToArray();
        var (boxes, closures, preserved) = PlanDirectionalMetadata(input, parsed, edited, calculations, context, changedContributorsOnly: true);
        ReadPairedVerificationRootPreservation(parsed, context, preserved);
        var contextFacts = context.Select(c => c.Facts).ToArray(); var coincidences = DirectionalCoincidences(context);
        var target = new PlannedPositionImportTarget(input.ContentHash, input.ContentHash, "root_returned_positions_preservation_visual", 1, "retain_expand_boxes_preserve_runtime_import", 1,
            op.PositionImport, buffers, PositionImportContract.FactsHash(contextFacts), PositionImportContract.FactsHash(source.SourceBlocks), PositionImportContract.FactsHash(coincidences), faces,
            ResolvePairedProcedural(parsed, model, context), boxes.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(),
            closures.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), preserved.OrderBy(b => b.ResourceBlockIndex).ThenBy(b => b.FieldPath, StringComparer.Ordinal).ToArray(), contextFacts, coincidences, source.SourceBlocks);
        target = target with { TargetFingerprint = PositionImportContract.TargetHash(target) };
        var indices = edited.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Concat(boxes.Where(b => !b.OriginalWords.SequenceEqual(b.ExpectedWords)).Select(b => b.ResourceBlockIndex)).Distinct().Order().ToArray();
        var selected = PositionImportDraws(parsed.Snapshot, op.PositionImport);
        return new(op.OperationId, "import_positions", 1, selected, indices.Select(i => source.SourceBlocks.Single(b => b.Index == i)).ToArray()) { PositionImportTarget = target };
    }

    private static SelectedDrawCall[] PositionImportDraws(ModelSnapshot s, PositionImportIntent intent)
    {
        var ids = intent.Members.SelectMany(m => m.Lods).SelectMany(l => l.DrawCallIds).ToHashSet(StringComparer.Ordinal);
        var result = s.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls.Where(d => ids.Contains(d.Id)).Select(d => new SelectedDrawCall(l.Level, m.ResourcePath, m.MeshOrdinal, m.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        if (result.Length != ids.Count) throw ImportDrift("Exact draw union is incomplete."); return result;
    }
    private static CoordinatedResolvedBuffer[] ReadPositionImportMembers(ArtifactContent input, ParsedModel parsed, PositionImportIntent intent)
    {
        var calls = PositionImportDraws(parsed.Snapshot, intent); var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in intent.Members)
            foreach (var map in member.Lods)
            {
                var selected = calls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (selected.Length != map.DrawCallIds.Count || selected.Any(c => c.Lod != map.Lod) || selected.Select(c => c.MeshOrdinal).Distinct().Count() != 1) throw ImportDrift("Member/LOD ownership differs.");
                var reader = new TransformComponentOperation { Version = 5, Granularity = "draw_call_vertices", RuntimeMetadataPolicy = new("preserve_unverified", 1), ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices) };
                var profile = CreateRootBufferProfile(new(input, parsed.Snapshot, reader, selected), parsed, selected, preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true, pairedPreservation: true);
                ValidateExperimentalVertexStreams(profile, pairedPreservation: true); result.Add(new(member.MemberId, profile));
            }
        if (result.Select(r => r.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count) throw ImportDrift("Member storage aliases another member.");
        return result.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }
    private static DirectionalWordCalculation ApplyReturnedWords(Source2AffineProfile p, PositionReturnedMember returned, PositionValidatedBuffer result)
    {
        var bytes = p.Vertices.Decoded.ToArray(); var position = p.Vertices.Snapshot.PositionLayout;
        for (var i = 0; i < returned.Positions.Count; i++) for (var axis = 0; axis < 3; axis++) Convert.FromHexString(returned.Positions[i][axis]).CopyTo(bytes, i * position.Stride + position.Offset + axis * 4);
        foreach (var f in result.Frames) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(f.VertexId * p.PackedFrameLayout.Stride + p.PackedFrameLayout.Offset, 4), f.ExpectedWord);
        var points = returned.Positions.Select(PositionAuthoringJson.Point).ToArray();
        var changed = points.Select((point, i) => (point, i)).Where(v => !PositionEditGuard.SameWords(Source2GeometryAnalyzer.ReadPosition(p.Vertices, v.i), v.point)).Select(v => v.i).ToArray();
        return new(bytes, points, p.Vertices.Snapshot.DecodedHash, p.Vertices.Snapshot.DecodedHash, 0, 0, 0, changed, result.Frames.Select(f => f.VertexId).ToArray(), [], result.MaximumDisplacement);
    }
    private static PlannedPositionImportBuffer ImportBufferFacts(GeometryCodecIdentity codec, CoordinatedResolvedBuffer member, PositionSourceBuffer source, PositionValidatedBuffer r, byte[] expected, PositionAuthoringSelection selection)
    {
        var p = member.Profile; var allowed = selection.BoundaryFramePolicy.AllowedBoundary?.SingleOrDefault(m => m.MemberId == member.MemberId && m.Lod == p.Mesh.Lod)?.VertexIds ?? [];
        var protectedIds = selection.Protection.Single(m => m.MemberId == member.MemberId && m.Lod == p.Mesh.Lod).VertexIds.Union(source.ProceduralIds).Order().ToArray();
        var frames = r.FrameClosure.Select(i => new PositionImportFrameWord(i, RawFrameWord(p.Vertices.Decoded, p.PackedFrameLayout, i), RawFrameWord(expected, p.PackedFrameLayout, i))).ToArray();
        return new(member.MemberId, p.Mesh.Lod, p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.DecodedHash,
            p.Vertices.Snapshot.EncodedHash, p.Indices.Snapshot.DecodedHash, PositionImportContract.FactsHash(new { Codec = codec, p.Vertices.Snapshot.PositionLayout, p.PackedFrameLayout, p.Vertices.Snapshot.Stride, p.Vertices.Snapshot.VertexCount }),
            r.ExpectedPositionHash, r.ExpectedFrameHash, ContentHash.Compute(expected), r.ChangedPositions, r.FrameClosure, frames,
            DirectionalPositionWords(p.Vertices.Decoded, p.Vertices.Snapshot.PositionLayout, protectedIds), ContentHash.Compute(protectedIds.Except(allowed).SelectMany(i => Convert.FromHexString(RawFrameWord(p.Vertices.Decoded, p.PackedFrameLayout, i))).ToArray()), r.BoundaryFrameChanges);
    }
    private static string RawFrameWord(byte[] bytes, PackedFrameLayout layout, int i) => Convert.ToHexStringLower(bytes.AsSpan(i * layout.Stride + layout.Offset, 4));
    private static PairedSourceTriangleFacts ImportTriangleFacts(CoordinatedResolvedBuffer member, IReadOnlyList<Point3> before, Point3[] after, IReadOnlyList<int> triangles)
    {
        var valid = PositionEditGuard.Validate(before, after, triangles); var partitions = new byte[valid.Length]; var touched = 0; var changedValid = 0;
        for (var i = 0; i < valid.Length; i++)
        {
            var a = triangles[3 * i]; var b = triangles[3 * i + 1]; var c = triangles[3 * i + 2];
            partitions[i] = (byte)((before[a] == before[b] ? 1 : 0) | (before[b] == before[c] ? 2 : 0) | (before[c] == before[a] ? 4 : 0));
            var changed = !PositionEditGuard.SameWords(before[a], after[a]) || !PositionEditGuard.SameWords(before[b], after[b]) || !PositionEditGuard.SameWords(before[c], after[c]);
            if (changed) { if (valid[i]) changedValid++; else touched++; }
        }
        var hash = ContentHash.Compute(partitions);
        return new(member.MemberId, member.Profile.Mesh.Lod, member.Profile.Indices.Snapshot.DecodedHash, triangles, DirectionalContractValidator.VertexSetHash(triangles), partitions, hash, hash, valid.Count(v => v), changedValid, valid.Count(v => !v), touched);
    }
    private static S2ModKitException ImportDrift(string message) => Errors.Verification("IMPORT_RESULT_DRIFT", message, "Reject candidate; no verified build may be published.");
}

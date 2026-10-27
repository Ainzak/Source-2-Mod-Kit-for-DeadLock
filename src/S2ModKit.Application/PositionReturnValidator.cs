using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static class PositionReturnValidator
{
    public static PositionAuthoringSelection ReadSelection(ReadOnlyMemory<byte> bytes)
    {
        var s = PositionAuthoringJson.Read<PositionAuthoringSelection>(bytes, 16 * 1024 * 1024);
        Require(s.SchemaVersion == 1, "IMPORT_SELECTION_VERSION", "Unsupported selection version."); PositionAuthoringJson.Hash(s.InputHash);
        Require(s.RuntimeMetadataPolicy == new PositionImportPolicy("preserve_unverified", 1)
            && s.ProceduralInputPolicy == new PositionImportPolicy("preserve_serialized_inputs_unverified", 1)
            && s.SourceTrianglePolicy == new PositionImportPolicy("preserve_source_coincidence", 1)
            && s.ZeroBoneBoxPolicy == new PositionImportPolicy("reject", 1) && s.ZeroRenderSpherePolicy == new PositionImportPolicy("reject", 1),
            "IMPORT_POLICY_INVALID", "Explicit characterized preservation/triangle and reject-zero policies are required.");
        Require(s.Members.Count is >= 1 and <= 16 && float.IsFinite(s.MaximumDisplacement) && s.MaximumDisplacement is > 0 and <= 64,
            "IMPORT_SELECTION_INVALID", "Require 1–16 members and a finite displacement cap in (0,64].");
        var ids = new HashSet<string>(StringComparer.Ordinal); var draws = new HashSet<string>(StringComparer.Ordinal); long records = 0;
        int[]? lods = null;
        foreach (var m in s.Members)
        {
            Require(m is not null && Regex.IsMatch(m.MemberId ?? "", "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant) && ids.Add(m.MemberId!), "IMPORT_MEMBER_INVALID", "Invalid/duplicate member ID.");
            Require(m!.Lods is { Count: >= 1 and <= 8 } && m.Lods.All(l => l is not null), "IMPORT_LOD_INCOMPLETE", "Incomplete LOD maps.");
            var current = m.Lods.Select(l => l.Lod).ToArray();
            Require(current.SequenceEqual(Enumerable.Range(0, current.Length)) && (lods is null || current.SequenceEqual(lods)), "IMPORT_LOD_INCOMPLETE", "Require contiguous identical all-present LOD maps."); lods = current;
            foreach (var l in m.Lods)
            {
                Require(l.ExpectedVertices > 0 && l.DrawCallIds is { Count: >= 1 and <= 64 } && l.DrawCallIds.SequenceEqual(l.DrawCallIds.Order(StringComparer.Ordinal)), "IMPORT_MEMBER_INVALID", "Invalid complete-buffer expectation/order.");
                foreach (var d in l.DrawCallIds) Require(d is not null && Regex.IsMatch(d, "^dc_[0-9a-f]{24}$", RegexOptions.CultureInvariant) && draws.Add(d), "IMPORT_MEMBER_INVALID", "Invalid/duplicate draw ID.");
                records += l.ExpectedVertices;
            }
        }
        Require(records <= 1_000_000, "IMPORT_RECORD_BUDGET", "Selected record budget exceeded.");
        CheckMaps(s.Protection, s, requireComplete: true);
        Require(s.BoundaryFramePolicy.Version == 1, "IMPORT_POLICY_INVALID", "Unsupported boundary policy version.");
        using var doc = JsonDocument.Parse(bytes);
        if (s.BoundaryFramePolicy.Kind == "keep_positions_and_frames")
            Require(!doc.RootElement.GetProperty("boundaryFramePolicy").TryGetProperty("allowedBoundary", out _), "IMPORT_POLICY_INVALID", "Strict protection forbids a boundary exception, including null.");
        else
        {
            Require(s.BoundaryFramePolicy.Kind == "keep_positions_allow_source_derived_boundary_frames" && s.BoundaryFramePolicy.AllowedBoundary is { Count: > 0 }, "IMPORT_POLICY_INVALID", "Explicit source-bound boundary maps are required.");
            CheckMaps(s.BoundaryFramePolicy.AllowedBoundary!, s, requireComplete: false);
            Require(s.BoundaryFramePolicy.AllowedBoundary!.Select(m => m.Lod).Distinct().Order().SequenceEqual(lods!), "IMPORT_LOD_INCOMPLETE", "Boundary declaration must cover all present LODs.");
        }
        return s;
    }
    private static void CheckMaps(IReadOnlyList<PositionRecordMap> maps, PositionAuthoringSelection s, bool requireComplete)
    {
        Require(maps is { Count: >= 1 and <= 128 } && maps.All(m => m is not null), "IMPORT_PROTECTION_INVALID", "Require bounded nonnull protection maps.");
        var keys = new HashSet<(string, int)>();
        foreach (var m in maps)
        {
            var member = s.Members.SingleOrDefault(x => x.MemberId == m.MemberId); var l = member?.Lods.SingleOrDefault(x => x.Lod == m.Lod);
            Require(l is not null && keys.Add((m.MemberId, m.Lod)), "IMPORT_PROTECTION_INVALID", "Unknown or duplicate protected member/LOD.");
            PositionAuthoringJson.Hash(m.SourceDecodedHash);
            Require(m.VertexIds is { Count: > 0 } && m.VertexIds.SequenceEqual(m.VertexIds.Distinct().Order()) && m.VertexIds.All(i => i >= 0 && i < l!.ExpectedVertices), "IMPORT_PROTECTION_INVALID", "Protected IDs must be sorted, unique, nonempty and in range.");
        }
        if (requireComplete) Require(keys.Count == s.Members.Sum(m => m.Lods.Count), "IMPORT_PROTECTION_INVALID", "Every selected member/LOD needs explicit protection.");
    }

    public static PositionAuthoringBundle CreateBundle(PositionAuthoringSource source, PositionAuthoringSelection selection, ContentHash snapshotHash)
    {
        CheckSource(source, selection);
        return new(1, "geometry_authoring_handoff", source.InputHash.Value, snapshotHash.Value, selection.Members[0].Lods.Select(l => l.Lod).ToArray(), selection.MaximumDisplacement,
            ["rig", "uv", "weights", "materials", "packedFrames"], source.Buffers.Select(b => new PositionBundleMember(
                PositionAuthoringJson.BufferId(b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal), b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal,
                b.Facts.DecodedBufferHash.Value, b.Facts.Selected, b.Positions.Select(PositionAuthoringJson.Words).ToArray(), b.Triangles,
                b.Facts.Selected ? Protected(b, selection).Union(b.ProceduralIds).Order().ToArray() : Enumerable.Range(0, b.Positions.Count).ToArray())).ToArray());
    }

    private static void CheckSource(PositionAuthoringSource source, PositionAuthoringSelection s)
    {
        Require(source.InputHash.Value == s.InputHash && PositionAuthoringJson.Canonical(source.Members).AsSpan().SequenceEqual(PositionAuthoringJson.Canonical(s.Members)), "IMPORT_SOURCE_DRIFT", "Source/member identities differ.");
        Require(source.Buffers.Count is >= 1 and <= 256 && source.Buffers.Sum(b => (long)b.Positions.Count) <= 2_000_000
            && source.Buffers.Where(b => b.Facts.Selected).Sum(b => (long)b.Triangles.Count) <= 3_000_000, "IMPORT_GEOMETRY_BUDGET", "Source/context geometry budget exceeded.");
        Require(source.Buffers.Select(b => (b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal)).Distinct().Count() == source.Buffers.Count, "IMPORT_SOURCE_DRIFT", "Duplicated source buffer.");
        Require(source.Buffers.Count(b => b.Facts.Selected) == s.Members.Sum(m => m.Lods.Count), "IMPORT_MEMBER_INVALID", "Selected source map differs.");
        Require(source.Buffers.Where(b => b.Facts.Selected).Select(b => (b.SelectedMemberId, b.Facts.Lod)).Distinct().Count() == s.Members.Sum(m => m.Lods.Count),
            "IMPORT_MEMBER_INVALID", "Each selected member/LOD must own exactly one source buffer.");
        foreach (var b in source.Buffers)
        {
            Require(b.Positions.Count == b.Facts.VertexCount && b.Positions.Count is > 0 and <= 500_000 && b.Triangles.Count is > 0 and <= 6_000_000 && b.Triangles.Count % 3 == 0,
                "IMPORT_GEOMETRY_BUDGET", "Incomplete source buffer tables.");
            if (!b.Facts.Selected) continue;
            var map = s.Members.SingleOrDefault(m => m.MemberId == b.SelectedMemberId)?.Lods.SingleOrDefault(l => l.Lod == b.Facts.Lod);
            Require(map is not null && map.ExpectedVertices == b.Positions.Count && b.Frames.Count == b.Positions.Count && b.FrameWords.Count == b.Positions.Count,
                "IMPORT_MEMBER_INVALID", "Source record/frame map differs.");
            var protection = s.Protection.Single(p => p.MemberId == b.SelectedMemberId && p.Lod == b.Facts.Lod);
            Require(protection.SourceDecodedHash == b.Facts.DecodedBufferHash.Value, "IMPORT_SOURCE_DRIFT", "Protected decoded source hash differs.");
        }
    }
    private static IReadOnlyList<int> Protected(PositionSourceBuffer b, PositionAuthoringSelection s) => s.Protection.Single(p => p.MemberId == b.SelectedMemberId && p.Lod == b.Facts.Lod).VertexIds;

    public static PositionValidationResult Validate(PositionAuthoringSource source, PositionAuthoringSelection s,
        ReadOnlyMemory<byte> bundleBytes, ContentHash expectedBundleHash, ReadOnlyMemory<byte> deltaBytes, Func<TangentFrame, uint> encode)
    {
        CheckSource(source, s);
        Require(ContentHash.Compute(bundleBytes.Span) == expectedBundleHash, "IMPORT_BUNDLE_HASH", "Trusted bundle hash differs.");
        var bundle = PositionAuthoringJson.Read<PositionAuthoringBundle>(bundleBytes);
        PositionAuthoringJson.Hash(bundle.SnapshotSha256);
        var expected = CreateBundle(source, s, new(bundle.SnapshotSha256));
        Require(bundle.SchemaVersion == 1 && bundle.Kind == expected.Kind && bundle.InputHash == expected.InputHash && bundle.MaximumDisplacement == expected.MaximumDisplacement
            && bundle.RequiredLods.SequenceEqual(expected.RequiredLods) && bundle.OmittedAttributes.SequenceEqual(expected.OmittedAttributes)
            && PositionAuthoringJson.Canonical(bundle.Members).AsSpan().SequenceEqual(PositionAuthoringJson.Canonical(expected.Members)), "IMPORT_BUNDLE_SOURCE_DRIFT", "Bundle source basis, IDs, topology, selection or protection differs from independent source decode.");
        var delta = PositionAuthoringJson.Read<PositionAuthoringDelta>(deltaBytes);
        Require(delta.SchemaVersion == 1 && delta.Kind == "geometry_authoring_delta" && delta.Operation.Name == "author_positions" && delta.Operation.Version == 1
            && delta.InputHash == source.InputHash.Value && delta.BundleSha256 == expectedBundleHash.Value && delta.TargetKey == "S2ModKit_Edit"
            && delta.BlenderVersion is { Length: > 0 and <= 128 } && delta.BlenderVersion.All(c => c is >= ' ' and <= '~'), "IMPORT_DELTA_INVALID", "Delta version/source/key differs.");
        using (var doc = JsonDocument.Parse(deltaBytes)) Require(PositionAuthoringJson.Canonical(doc.RootElement).AsSpan().SequenceEqual(deltaBytes.Span), "IMPORT_DELTA_NONCANONICAL", "Return must contain canonical JSON bytes.");
        var editable = bundle.Members.Where(b => b.Editable).ToArray();
        Require(delta.Members.Count == editable.Length && delta.Members.All(m => m is not null) && delta.Members.Select(m => m.MemberId).SequenceEqual(editable.Select(m => m.MemberId)), "IMPORT_DELTA_MEMBERS", "Return needs every member in immutable source order.");
        var after = new Dictionary<(int Lod, int Mesh, int Buffer), IReadOnlyList<Point3>>(); var result = new List<PositionValidatedBuffer>();
        foreach (var returned in delta.Members)
        {
            var b = source.Buffers.Single(b => PositionAuthoringJson.BufferId(b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal) == returned.MemberId);
            Require(returned.Positions.Count == b.Positions.Count, "IMPORT_DELTA_MEMBERS", "Return record count differs.");
            var points = returned.Positions.Select(PositionAuthoringJson.Point).ToArray();
            after.Add((b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal), points);
            var protection = Protected(b, s).Union(b.ProceduralIds).ToHashSet();
            Require(protection.All(i => PositionEditGuard.SameWords(b.Positions[i], points[i])), "IMPORT_PROTECTED_POSITION", "Protected/procedural position changed.");
            var maximum = points.Select((p, i) => ConservativePointDistance.RoundUp(b.Positions[i], p)).Max();
            Require(maximum <= s.MaximumDisplacement, "IMPORT_DISPLACEMENT", "Stored-point displacement exceeds cap.");
            IncidentFrameResult frames;
            try { frames = IncidentFrameTransport.Evaluate(b.Positions, points, b.Triangles, b.Frames); }
            catch (ArgumentException e) { throw new S2ModKitException(PositionAuthoringJson.Invalid("IMPORT_GEOMETRY_FRAME_INVALID", e.Message).Error, e); }
            var allowedMap = s.BoundaryFramePolicy.AllowedBoundary?.SingleOrDefault(m => m.MemberId == b.SelectedMemberId && m.Lod == b.Facts.Lod);
            var allowed = allowedMap?.VertexIds.ToHashSet() ?? [];
            if (allowedMap is not null)
                Require(allowedMap.SourceDecodedHash == b.Facts.DecodedBufferHash.Value && allowed.IsSubsetOf(protection) && allowed.IsSubsetOf(frames.Closure.ToHashSet())
                    && !allowed.Overlaps(b.ProceduralIds) && allowed.All(i => PositionEditGuard.SameWords(b.Positions[i], points[i])), "IMPORT_BOUNDARY_INVALID", "Boundary permission is stale, nonstationary, procedural or outside protected frame closure.");
            var words = b.FrameWords.ToArray(); var changes = new List<PositionFrameChange>(); var boundary = new List<int>();
            foreach (var i in frames.Closure)
            {
                try { words[i] = encode(frames.Frames[i]); }
                catch (Exception e) when (e is ArgumentException or InvalidDataException) { throw new S2ModKitException(PositionAuthoringJson.Invalid("IMPORT_FRAME_CODEC", "Frame does not satisfy native codec gates.").Error, e); }
                if (words[i] == b.FrameWords[i]) continue;
                Require(!b.ProceduralIds.Contains(i) && (!protection.Contains(i) || allowed.Contains(i)), "IMPORT_PROTECTED_FRAME", "Protected/procedural packed frame changed without explicit permission.");
                changes.Add(new(i, b.FrameWords[i], words[i])); if (protection.Contains(i)) boundary.Add(i);
            }
            var touched = points.Select((p, i) => (p, i)).Where(v => !PositionEditGuard.SameWords(b.Positions[v.i], v.p)).Select(v => v.i).Concat(changes.Select(c => c.VertexId)).ToHashSet();
            Require(b.BoundGates.All(g => g.PositiveBoxAndSpheres || !g.ContributorIds.Any(touched.Contains)), "IMPORT_BOX_UNSUPPORTED", "Affected source box/sphere fails positive admission.");
            result.Add(new(b.SelectedMemberId!, b.Facts.Lod, points.Where((p, i) => !PositionEditGuard.SameWords(b.Positions[i], p)).Count(), maximum, frames.Closure, changes, boundary, HashPositions(points), HashFrames(words)));
        }
        var pairs = new Dictionary<(int, Point3), Point3>(); var reverse = new Dictionary<(int, Point3), Point3>();
        foreach (var b in source.Buffers)
        {
            var output = after.GetValueOrDefault((b.Facts.Lod, b.Facts.MeshOrdinal, b.Facts.VertexBufferOrdinal)) ?? b.Positions;
            for (var i = 0; i < b.Positions.Count; i++)
            {
                var key = (b.Facts.Lod, b.Positions[i]); var target = (b.Facts.Lod, output[i]);
                Require(!pairs.TryGetValue(key, out var old) || old == output[i], "IMPORT_COINCIDENCE_SPLIT", "Original selected/context coincidence split.");
                Require(!reverse.TryGetValue(target, out var original) || original == b.Positions[i], "IMPORT_COINCIDENCE_MERGE", "Distinct source positions merged.");
                pairs[key] = output[i]; reverse[target] = b.Positions[i];
            }
        }
        return new("position_return_validated", source.InputHash, expectedBundleHash, ContentHash.Compute(deltaBytes.Span), ContentHash.Compute(PositionAuthoringJson.Canonical(s)), result, false, "untested");
    }
    public static ContentHash HashPositions(IReadOnlyList<Point3> points) => ContentHash.Compute(points.SelectMany(p => PositionAuthoringJson.Words(p).SelectMany(Convert.FromHexString)).ToArray());
    public static ContentHash HashFrames(IReadOnlyList<uint> words)
    {
        var bytes = new byte[words.Count * 4]; for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), words[i]); return ContentHash.Compute(bytes);
    }
    private static void Require(bool ok, string code, string message) { if (!ok) throw PositionAuthoringJson.Invalid(code, message); }
}

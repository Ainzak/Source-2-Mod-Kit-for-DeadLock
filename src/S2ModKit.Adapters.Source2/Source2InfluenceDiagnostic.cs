using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed record Source2InfluenceDiagnosticReport(
    int SchemaVersion, string Kind, string ResourcePath, ContentHash InputHash,
    GeometryCodecIdentity Codec, string AdapterVersion, bool MutationPermission,
    string DependencyClosureStatus, string FieldScope, ContentHash? FieldOptionsHash,
    IReadOnlyList<int> PresentLods, IReadOnlyList<Source2InfluenceRootBone> RootBones,
    IReadOnlyList<Source2InfluenceDependency> Dependencies, IReadOnlyList<Source2InfluenceMesh> Meshes);

public sealed record Source2InfluenceRootBone(int Index, string Name, bool IsProceduralCloth);
public sealed record Source2InfluenceDependency(string Family, int? ResourceBlockIndex, ContentHash? PayloadHash, string Status);
public sealed record Source2InfluenceMesh(
    int Lod, int MeshOrdinal, int ResourceBlockIndex, ulong LodMask, string Status, string? Failure,
    ContentHash? RemapHash, IReadOnlyList<int> Remap, IReadOnlyList<Source2InfluenceBuffer> Buffers)
{
    public string? SourceLabel { get; init; }
}
public sealed record Source2InfluenceBuffer(
    int BufferOrdinal, int VertexResourceBlockIndex, int VertexCount, ContentHash DecodedHash,
    GeometryBounds SourceBounds, IReadOnlyList<string> DrawCallIds, int IndexedVertices,
    bool CompleteIndexedCoverage, bool CharacterizedPackedFrames, int ProceduralVertices,
    IReadOnlyList<string> ObservedGateReasons, string AdmissionStatus,
    Source2InfluenceFieldEffects? FieldEffects, IReadOnlyList<Source2InfluenceBone> Bones)
{
    public IReadOnlyList<string> Materials { get; init; } = [];
    public IReadOnlyList<Source2InfluenceIndexBuffer> IndexBuffers { get; init; } = [];
}
public sealed record Source2InfluenceIndexBuffer(int Ordinal, int ResourceBlockIndex, int IndexCount, ContentHash DecodedHash);
public sealed record Source2InfluenceFieldEffects(
    int FullVertices, int TransitionVertices, int PinnedVertices, int ChangedPositionVertices,
    int UnchangedPositionVertices, int ChangedProceduralVertices, float MaximumDisplacement,
    string FrameEffectsStatus);
public sealed record Source2InfluenceBone(
    int RenderBoneIndex, string RenderBoneName, int RootBoneIndex, string RootBoneName,
    bool IsProceduralCloth, ContentHash InverseBindHash, int ContributorCount,
    ContentHash ContributorSetHash, int? ChangedPositionContributors, int? PinnedContributors,
    int? UnchangedPositionContributors);

/// <summary>Read-only facts. A position-only probe cannot establish dependency closure or writer admission.</summary>
internal static class Source2InfluenceDiagnostic
{
    internal static Source2InfluenceMesh BuildMesh(
        int lod, int meshOrdinal, int blockIndex, ulong lodMask, KVObject descriptor, KVObject meshData,
        Source2GeometryAnalysis geometry, IReadOnlyList<Source2InfluenceRootBone> roots, int[]? remap,
        CoordinatedFieldMath? field)
    {
        try
        {
            if (roots.Count == 0 || roots.Select((r, i) => r.Index == i).Any(valid => !valid)
                || roots.Any(r => string.IsNullOrWhiteSpace(r.Name))
                || roots.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != roots.Count)
                throw new InvalidDataException("Root bone identities are absent, noncanonical or ambiguous.");
            if (remap is null || remap.Any(i => i < 0 || i >= roots.Count))
                throw new InvalidDataException("A complete valid mesh-to-root remap is required; no identity fallback.");

            // The existing read-side inventory validates every nonzero weight, including unindexed
            // vertices. It supplies no procedural waiver, mutation plan or bounds qualification.
            var metadata = Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(
                descriptor, meshData, geometry, $"influence mesh {meshOrdinal} LOD {lod}");
            if (metadata.BoneBounds.Any(b => b.BoneIndex >= remap.Length))
                throw new InvalidDataException("An influenced render bone has no serialized root remap entry.");
            var rows = new List<Source2InfluenceBuffer>();
            var baseOffset = 0;
            foreach (var buffer in geometry.VertexBuffers)
            {
                var count = buffer.Snapshot.VertexCount;
                var before = Enumerable.Range(0, count).Select(i => Source2GeometryAnalyzer.ReadPosition(buffer, i)).ToArray();
                var contributions = metadata.BoneBounds.Select(b => (Bone: b, Vertices: b.InfluencedVertices
                    .Where(v => v >= baseOffset && v < baseOffset + count).Select(v => v - baseOffset).ToArray()))
                    .Where(b => b.Vertices.Length != 0).ToArray();
                var procedural = contributions.Where(b => roots[remap[b.Bone.BoneIndex]].IsProceduralCloth)
                    .SelectMany(b => b.Vertices).ToHashSet();
                var changed = new HashSet<int>();
                var pinned = new HashSet<int>();
                var full = 0;
                var transition = 0;
                var maximum = 0f;
                if (field is not null)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var result = field.Evaluate(before[i]);
                        if (result.Membership == CoordinatedMembership.Pinned) pinned.Add(i);
                        else if (result.Membership == CoordinatedMembership.Full) full++;
                        else transition++;
                        if (!SameWords(before[i], result.Position)) changed.Add(i);
                        maximum = Math.Max(maximum, result.MaximumDisplacement);
                    }
                }
                var calls = geometry.DrawCalls.Where(c => c.Snapshot.VertexBufferOrdinal == buffer.Snapshot.Ordinal).ToArray();
                var indexed = calls.SelectMany(c => c.VertexIndices).Distinct().Order().ToArray();
                var complete = indexed.Length == count && indexed.Select((v, i) => v == i).All(v => v);
                var reasons = new List<string>();
                if (!complete) reasons.Add("EXPERIMENTAL_COMPLETE_BUFFER_REQUIRED");
                if (BitOperations.PopCount(lodMask) != 1) reasons.Add("SHARED_LOD_READ_ONLY");
                if (buffer.PackedFrameLayout is null) reasons.Add("AFFINE_PACKED_FRAME_UNSUPPORTED");
                if (procedural.Count != 0) reasons.Add("EXPERIMENTAL_PROCEDURAL_UNSUPPORTED");
                if (contributions.Any(b => b.Bone.LocalBoundsSize.X <= 0 || b.Bone.LocalBoundsSize.Y <= 0 || b.Bone.LocalBoundsSize.Z <= 0))
                    reasons.Add("EXPERIMENTAL_DEGENERATE_BOX_UNSUPPORTED");
                var bones = contributions.Select(b =>
                {
                    var root = roots[remap[b.Bone.BoneIndex]];
                    return new Source2InfluenceBone(b.Bone.BoneIndex, b.Bone.BoneName, root.Index, root.Name,
                        root.IsProceduralCloth, b.Bone.InverseBindPoseHash, b.Vertices.Length,
                        new ContentHash(VertexSetHash.Compute(b.Vertices)),
                        field is null ? null : b.Vertices.Count(changed.Contains),
                        field is null ? null : b.Vertices.Count(pinned.Contains),
                        field is null ? null : b.Vertices.Count(v => !changed.Contains(v)));
                }).ToArray();
                rows.Add(new Source2InfluenceBuffer(buffer.Snapshot.Ordinal, buffer.Snapshot.ResourceBlockIndex, count,
                    buffer.Snapshot.DecodedHash, Bounds(before), calls.Select(c => c.Snapshot.DrawCallId).Order(StringComparer.Ordinal).ToArray(),
                    indexed.Length, complete, buffer.PackedFrameLayout is not null, procedural.Count,
                    reasons.Order(StringComparer.Ordinal).ToArray(), "not_assessed_full_planner_required",
                    field is null ? null : new(full, transition, pinned.Count, changed.Count, count - changed.Count,
                        changed.Count(procedural.Contains), maximum, "not_assessed"), bones)
                {
                    IndexBuffers = calls.Select(c => c.Snapshot.IndexBufferOrdinal).Distinct().Order()
                        .Select(i => geometry.IndexBuffers.Single(b => b.Snapshot.Ordinal == i).Snapshot)
                        .Select(i => new Source2InfluenceIndexBuffer(i.Ordinal, i.ResourceBlockIndex, i.IndexCount, i.DecodedHash)).ToArray(),
                });
                baseOffset = checked(baseOffset + count);
            }
            return new(lod, meshOrdinal, blockIndex, lodMask, "reported", null,
                ContentHash.Compute(remap.SelectMany(BitConverter.GetBytes).ToArray()), remap, rows);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            // Do not retain a prefix of a partially assessed mesh or invent missing root identities.
            return new(lod, meshOrdinal, blockIndex, lodMask, "unsupported", ex.Message, null, [], []);
        }
    }

    private static bool SameWords(Point3 a, Point3 b) => BitConverter.SingleToUInt32Bits(a.X) == BitConverter.SingleToUInt32Bits(b.X)
        && BitConverter.SingleToUInt32Bits(a.Y) == BitConverter.SingleToUInt32Bits(b.Y)
        && BitConverter.SingleToUInt32Bits(a.Z) == BitConverter.SingleToUInt32Bits(b.Z);
    private static GeometryBounds Bounds(Point3[] points) => new(
        new() { X = points.Min(p => p.X), Y = points.Min(p => p.Y), Z = points.Min(p => p.Z) },
        new() { X = points.Max(p => p.X), Y = points.Max(p => p.Y), Z = points.Max(p => p.Z) });
}

public sealed partial class Source2CompiledModelAdapter
{
    public Source2InfluenceDiagnosticReport DiagnoseInfluences(ArtifactContent artifact, CoordinatedScaffoldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (ContentHash.Compute(artifact.Bytes.Span) != artifact.ContentHash)
            throw Errors.Input("INFLUENCE_INPUT_HASH_MISMATCH", "Input bytes differ from the declared hash.", "Use the immutable source identity.");
        CoordinatedFieldMath? field = null;
        if (options is not null)
        {
            CoordinatedSelection.ValidateOptions(options);
            field = new(options.Field, options.MaximumDisplacement);
        }
        if (GeometryCodecCapability.Status != "ready")
            throw Errors.Unsupported("INFLUENCE_CODEC_UNAVAILABLE", GeometryCodecCapability.Summary, "Configure the qualified meshoptimizer library.");
        using var parsed = Parse(artifact, retainGeometryAnalysis: true);
        var model = parsed.Resource.Blocks.OfType<ValveResourceFormat.ResourceTypes.Model>().Single();
        var names = ExperimentalArray(ExperimentalCollection(model.Data, "m_modelSkeleton"), "m_boneName");
        if (names.Count is < 1 or > 4096 || names.Count != model.Skeleton.Bones.Length)
            throw Errors.Unsupported("INFLUENCE_ROOT_IDENTITY_UNSUPPORTED", "Serialized and decoded root skeleton counts differ.", "Characterize the source layout.");
        var roots = new List<Source2InfluenceRootBone>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (names[i].ValueType != KVValueType.String || string.IsNullOrWhiteSpace(name)
                || name != model.Skeleton.Bones[i].Name || roots.Any(r => r.Name == name))
                throw Errors.Unsupported("INFLUENCE_ROOT_IDENTITY_UNSUPPORTED", "Root bone names are untyped, duplicated or inconsistent.", "Characterize the source layout.");
            roots.Add(new(i, name, model.Skeleton.Bones[i].IsProceduralCloth));
        }
        var dependencies = parsed.Envelope.Blocks.Where(b => b.Type is "PHYS" or "DSTF" or "MBUF")
            .Select(b => new Source2InfluenceDependency(b.Type, b.Index, ContentHash.Compute(b.Payload.Span), "present_semantics_not_qualified")).ToList();
        dependencies.Add(new("root_morph", null, null, HasMorphData(model.Data) ? "present_unsupported" : "absent"));
        dependencies.Add(new("root_aabb", null, null, model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds") ? "present_unsupported" : "absent"));
        dependencies.Add(new("procedural_bones", null, null, roots.Any(r => r.IsProceduralCloth) ? "present_simulation_family_not_characterized" : "absent_in_decoded_flags"));
        dependencies.Add(new("render_boxes_spheres_attachments", null, null, "reported_influences_only_consumer_semantics_unverified"));
        var rows = new List<Source2InfluenceMesh>();
        foreach (var mesh in parsed.MeshesByOrdinal.Values.OrderBy(m => m.MeshOrdinal))
        {
            foreach (var lod in ExpandRootLodMask(mesh.LodMask, parsed.Snapshot.Lods.Count, mesh.MeshOrdinal))
            {
                var row = mesh.GeometryAnalysis is { } geometry
                    ? Source2InfluenceDiagnostic.BuildMesh(lod, mesh.MeshOrdinal, mesh.BlockIndex, mesh.LodMask,
                        mesh.Descriptor, mesh.Block.Data, geometry, roots, model.GetRemapTable(mesh.MeshOrdinal), field)
                    : new(lod, mesh.MeshOrdinal, mesh.BlockIndex, mesh.LodMask, "unsupported", mesh.Geometry.Summary, null, [], []);
                var snapshot = parsed.Snapshot.Lods.Single(l => l.Level == lod).Meshes.Single(m => m.MeshOrdinal == mesh.MeshOrdinal);
                rows.Add(row with
                {
                    SourceLabel = snapshot.MechanicalLineage?.SourceLabel,
                    Buffers = row.Buffers.Select(b => b with
                    {
                        Materials = snapshot.DrawCalls.Where(c => b.DrawCallIds.Contains(c.Id, StringComparer.Ordinal))
                            .Select(c => c.MaterialPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    }).ToArray(),
                });
                if (rows.Sum(r => r.Buffers.Sum(b => b.Bones.Count)) > 131072)
                    throw Errors.Unsupported("INFLUENCE_REPORT_LIMIT", "Influence summary exceeds the bounded record limit.", "Use a smaller characterized resource.");
            }
        }
        return new(1, "source2_influence_diagnostic", artifact.LogicalPath, artifact.ContentHash,
            GeometryCodecCapability.Identity!, AdapterVersion, false, "not_verified",
            options is null ? "no_field" : "hypothetical_all_reported_buffers_positions_only",
            options is null ? null : ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(options))),
            parsed.Snapshot.Lods.Select(l => l.Level).ToArray(), roots, dependencies, rows);
    }
}

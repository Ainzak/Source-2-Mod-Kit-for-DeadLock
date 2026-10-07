using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class PairedContractValidator
{
    public static ContentHash FieldMembershipHash(int vertexCount, PairedFieldDispatch field)
    {
        var tags = new byte[vertexCount];
        foreach (var index in field.CoreIndices) tags[index] = 2;
        foreach (var index in field.TransitionIndices) tags[index] = 1;
        return ContentHash.Compute(tags);
    }
    public static ContentHash DispatchMembershipHash(int vertexCount, IReadOnlyList<PairedFieldDispatch> fields)
    {
        var tags = new byte[vertexCount];
        for (var i = 0; i < fields.Count; i++)
        {
            foreach (var index in fields[i].CoreIndices) tags[index] = checked((byte)(1 + 2 * i));
            foreach (var index in fields[i].TransitionIndices) tags[index] = checked((byte)(2 + 2 * i));
        }
        return ContentHash.Compute(tags);
    }
    public static ContentHash DispatchWeightHash(IReadOnlyList<PairedFieldDispatch> fields) => MutationPlanJson.ComputeDirectionalFactsHash(
        fields.Select(f => new { f.FieldId, f.WeightHash }).ToArray());

    private static void ValidateDispatch(PlannedPairedTransformTarget t)
    {
        if (t.Dispatch is null || t.Dispatch.Count != t.Buffers.Count || t.Dispatch.Any(d => d is null)) throw Invalid("Missing complete pair dispatch.");
        var fieldIds = t.PairedTransform.Fields.Select(f => f.FieldId).ToArray();
        for (var i = 0; i < t.Buffers.Count; i++)
        {
            var b = t.Buffers[i]; var d = t.Dispatch[i]; var w = t.WordAudits[i];
            if (d.MemberId != b.MemberId || d.Lod != b.Lod || d.Fields is not { Count: 2 } || d.Fields.Any(f => f is null)
                || !d.Fields.Select(f => f.FieldId).SequenceEqual(fieldIds)) throw Invalid("Dispatch field/member/LOD identity drift.");
            var active = new HashSet<int>();
            for (var fi = 0; fi < 2; fi++)
            {
                var f = d.Fields[fi];
                foreach (var indices in new[] { f.CoreIndices, f.TransitionIndices, f.ChangedPositionIndices, f.ChangedFrameIndices })
                    ValidateIndices(indices, b.VertexCount);
                foreach (var index in f.CoreIndices.Concat(f.TransitionIndices))
                    if (!active.Add(index)) throw Invalid("A source record belongs to multiple interior categories or fields.");
                var support = f.CoreIndices.Concat(f.TransitionIndices).ToHashSet();
                var scale = t.PairedTransform.Fields[fi].Field.Scale;
                var frameSupport = scale.X == scale.Y && scale.Y == scale.Z ? f.TransitionIndices.ToHashSet() : support;
                if (f.ChangedPositionIndices.Any(v => !support.Contains(v)) || f.ChangedFrameIndices.Any(v => !frameSupport.Contains(v))
                    || !Hash(f.WeightHash) || f.MembershipHash != FieldMembershipHash(b.VertexCount, f)) throw Invalid("Field effects escape their source membership or identity.");
            }
            ValidateIndices(d.PinnedIndices, b.VertexCount);
            if (!d.PinnedIndices.SequenceEqual(Enumerable.Range(0, b.VertexCount).Where(v => !active.Contains(v)))
                || !d.PinnedIndices.SequenceEqual(w.PinnedIndices)
                || !d.Fields.SelectMany(f => f.ChangedPositionIndices).Order().SequenceEqual(w.ChangedPositionIndices)
                || !d.Fields.SelectMany(f => f.ChangedFrameIndices).Order().SequenceEqual(w.ChangedFrameIndices)
                || d.Fields.Sum(f => (long)f.CoreIndices.Count) != b.FullVertexCount
                || d.Fields.Sum(f => (long)f.TransitionIndices.Count) != b.TransitionVertexCount
                || d.MembershipHash != DispatchMembershipHash(b.VertexCount, d.Fields) || d.MembershipHash != b.MaskHash
                || d.WeightHash != DispatchWeightHash(d.Fields) || d.WeightHash != b.WeightHash) throw Invalid("Combined dispatch differs from buffer/word inventories.");
            var fixedSet = t.Protection.Union.Single(s => Key(s) == (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal)).VertexIndices;
            if (fixedSet.Intersect(w.ChangedPositionIndices.Concat(w.ChangedFrameIndices)).Any()) throw Invalid("A declared protected record has a changed word.");
        }
        foreach (var lod in t.PairedTransform.Members[0].Lods)
            foreach (var id in fieldIds)
                if (!t.Dispatch.Where(d => d.Lod == lod.Lod).SelectMany(d => d.Fields).Any(f => f.FieldId == id && f.ChangedPositionIndices.Count > 0))
                    throw Invalid("Each partner must change a stored position in every LOD.");
    }

    private static void ValidateTriangles(PlannedPairedTransformTarget t)
    {
        if (t.SourceTriangles is null || t.SourceTriangles.Count != t.Buffers.Count || t.SourceTriangles.Any(f => f is null)) throw Invalid("Missing source triangle inventory.");
        for (var i = 0; i < t.Buffers.Count; i++)
        {
            var b = t.Buffers[i]; var f = t.SourceTriangles[i];
            if (f.MemberId != b.MemberId || f.Lod != b.Lod || f.IndexHash != b.DecodedIndexBufferHash
                || f.TriangleIndices is not { Count: > 0 } || f.TriangleIndices.Count % 3 != 0
                || f.TriangleIndices.Any(v => v < 0 || v >= b.VertexCount)
                || f.TriangleSetHash != DirectionalContractValidator.VertexSetHash(f.TriangleIndices)
                || f.SourcePartitions is null || f.SourcePartitions.Count != f.TriangleIndices.Count / 3
                || f.SourcePartitions.Any(p => p is not (0 or 1 or 2 or 4 or 7))
                || f.SourcePartitionHash != ContentHash.Compute(f.SourcePartitions.ToArray()) || f.ExpectedPartitionHash != f.SourcePartitionHash
                || f.ValidTriangleCount != f.SourcePartitions.Count(p => p == 0) || f.CollapsedTriangleCount != f.SourcePartitions.Count(p => p != 0)
                || f.ChangedValidTriangleCount < 0 || f.ChangedValidTriangleCount > f.ValidTriangleCount
                || f.TouchedCollapsedTriangleCount < 0 || f.TouchedCollapsedTriangleCount > f.CollapsedTriangleCount) throw Invalid("Source face categories, partitions or preservation counts drifted.");
            for (var j = 0; j < f.TriangleIndices.Count; j += 3)
                if (f.TriangleIndices[j] == f.TriangleIndices[j + 1] || f.TriangleIndices[j + 1] == f.TriangleIndices[j + 2] || f.TriangleIndices[j] == f.TriangleIndices[j + 2])
                    throw Invalid("Repeated triangle corner indices reject.");
        }
    }

    private static void ValidateIndices(IReadOnlyList<int>? indices, int count)
    {
        if (indices is null || indices.Any(v => v < 0 || v >= count) || !indices.SequenceEqual(indices.Distinct().Order()))
            throw Invalid("Vertex inventories must be complete, sorted, unique and in range.");
    }
    private static (int Lod, int Mesh, int Buffer) Key(DirectionalProtectedSet s) => (s.Lod, s.MeshOrdinal, s.VertexBufferOrdinal);
    private static (int Lod, int Mesh, int Buffer) Key(DirectionalContextBuffer s) => (s.Lod, s.MeshOrdinal, s.VertexBufferOrdinal);
    private static (int Lod, int Mesh, int Buffer) Key(PairedSkinningBufferFacts s) => (s.Lod, s.MeshOrdinal, s.VertexBufferOrdinal);
}

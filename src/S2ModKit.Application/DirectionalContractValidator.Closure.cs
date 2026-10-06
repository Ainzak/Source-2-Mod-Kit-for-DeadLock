using System.Buffers.Binary;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class DirectionalContractValidator
{
    private static (int Lod, int Mesh, int Buffer) Key(DirectionalContextBuffer b) => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal);
    private static (int Lod, int Mesh, int Buffer) Key(DirectionalProtectedSet s) => (s.Lod, s.MeshOrdinal, s.VertexBufferOrdinal);

    private static void ValidateContext(PlannedDirectionalTransformTarget t, Dictionary<int, PlannedTargetBlock> sources)
    {
        if (t.ContextBuffers is not { Count: > 0 } || t.ContextBuffers.Any(c => c is null)
            || !t.ContextBuffers.Select(Key).SequenceEqual(t.ContextBuffers.Select(Key).Distinct().Order())
            || t.Coincidences is null || t.Coincidences.Any(c => c is null)
            || t.ContextBuffers.Select(c => c.ResourcePath).Distinct(StringComparer.Ordinal).Count() != 1
            || t.ContextBuffers.GroupBy(c => c.ResourceBlockIndex).Any(g => g.Select(c => (c.Lod, c.MeshOrdinal)).Distinct().Count() != 1)
            || t.ContextBuffers.GroupBy(c => c.VertexResourceBlockIndex).Any(g => g.Any(c => c.Selected) && g.Count() != 1)
            || t.ContextBuffers.Sum(c => (long)c.VertexCount) > int.MaxValue) throw Invalid("Missing or noncanonical complete source context.");
        var selected = t.Buffers.ToDictionary(b => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal));
        var lods = t.DirectionalTransform.Members[0].Lods.Select(l => l.Lod).ToArray();
        foreach (var c in t.ContextBuffers)
        {
            if (!lods.Contains(c.Lod) || c.MeshOrdinal < 0 || c.VertexBufferOrdinal < 0 || c.VertexCount <= 0 || !Portable(c.ResourcePath)
                || !new[] { c.DecodedBufferHash, c.PositionHash, c.PackedFrameHash, c.IndexHash, c.SkinningHash, c.RootRenderRemapHash }.All(Hash)
                || !sources.TryGetValue(c.ResourceBlockIndex, out var m) || m.Type != "MDAT"
                || !sources.TryGetValue(c.VertexResourceBlockIndex, out var v) || v.Type != "MVTX"
                || !sources.TryGetValue(c.IndexResourceBlockIndex, out var i) || i.Type != "MIDX") throw Invalid("Invalid context buffer or source-block identity.");
            var isSelected = selected.TryGetValue(Key(c), out var b);
            if (c.Selected != isSelected || (isSelected && (c.VertexCount != b!.VertexCount || c.DecodedBufferHash != b.InputDecodedVertexBufferHash
                || c.PositionHash != b.InputPositionHash || c.PackedFrameHash != b.InputPackedFrameHash || c.IndexHash != b.DecodedIndexBufferHash
                || c.ResourceBlockIndex != b.ResourceBlockIndex || c.VertexResourceBlockIndex != b.VertexResourceBlockIndex
                || c.IndexResourceBlockIndex != b.IndexResourceBlockIndex || c.ResourcePath != b.ResourcePath))) throw Invalid("Selected/context facts disagree.");
        }
        if (!t.ContextBuffers.Where(c => c.Selected).Select(Key).ToHashSet().SetEquals(selected.Keys)
            || !t.Coincidences.Select(c => c.Lod).SequenceEqual(lods)) throw Invalid("Context/coincidence LOD coverage is incomplete.");
        foreach (var c in t.Coincidences)
        {
            var count = t.ContextBuffers.Where(b => b.Lod == c.Lod).Sum(b => (long)b.VertexCount);
            if (!Hash(c.CompleteSourcePositionHash) || !Hash(c.PairSetHash) || c.RecordCount != count || c.PairCount < 0 || c.PairCount > count * (count - 1) / 2
                || c.MovingSelectedRecordCount != t.Buffers.Where(b => b.Lod == c.Lod).Sum(b => (long)b.ChangedPositionCount)
                || c.ExcludedMovingMates != 0) throw Invalid("Invalid complete coincidence identity/counts or moving excluded mates.");
        }
    }

    private static void ValidateProtection(PlannedDirectionalTransformTarget t)
    {
        var p = t.Protection;
        if (p is null || p.Assertions is null || p.Assertions.Any(a => a is null) || p.Union is null
            || !p.Assertions.Select(a => a.AssertionId).SequenceEqual(t.DirectionalTransform.Protection.Assertions.Select(a => a.AssertionId)))
            throw ProtectionDrift("Resolved assertion inventory drift.");
        var context = t.ContextBuffers.ToDictionary(Key);
        for (var ai = 0; ai < p.Assertions.Count; ai++)
        {
            var resolved = p.Assertions[ai];
            var intent = t.DirectionalTransform.Protection.Assertions[ai];
            ValidateProtectedSets(resolved.Sets, context);
            var rows = intent is DirectionalBoneAssertion ? t.ContextBuffers : t.ContextBuffers.Where(c => c.Selected).ToArray();
            if (!resolved.Sets.Select(Key).SequenceEqual(rows.Select(Key)) || resolved.ContributorSetHash != ContributorSetHash(resolved.Sets))
                throw ProtectionDrift("Resolved contributor closure is missing, stale or noncanonical.");
            switch (intent)
            {
                case DirectionalVertexAssertion vertex:
                    foreach (var declared in vertex.Sets)
                    {
                        var b = t.Buffers.Single(x => x.MemberId == declared.MemberId && x.Lod == declared.Lod);
                        var s = resolved.Sets.Single(x => x.Lod == b.Lod && x.MeshOrdinal == b.MeshOrdinal && x.VertexBufferOrdinal == b.VertexBufferOrdinal);
                        if (s.SourceDecodedBufferHash != declared.SourceDecodedBufferHash || s.VertexSetHash != declared.VertexSetHash
                            || s.VertexCount != declared.VertexCount || !s.VertexIndices.SequenceEqual(declared.VertexIndices)) throw ProtectionDrift("Declared/resolved vertex assertion drift.");
                    }
                    break;
                case DirectionalBoneAssertion bone:
                    foreach (var lod in bone.Lods)
                    {
                        var sets = resolved.Sets.Where(s => s.Lod == lod.Lod).ToArray();
                        if (sets.Sum(s => (long)s.VertexCount) != lod.ContributorCount || ContributorSetHash(sets) != lod.ContributorSetHash)
                            throw ProtectionDrift("Complete root-bone contributor expectations drifted.");
                    }
                    break;
            }
        }
        ValidateProtectedSets(p.Union, context);
        if (!p.Union.Select(Key).SequenceEqual(t.ContextBuffers.Select(Key)) || p.UnionHash != MutationPlanJson.ComputeDirectionalFactsHash(p.Union))
            throw ProtectionDrift("Protected union inventory or hash drift.");
        foreach (var s in p.Union)
        {
            var asserted = p.Assertions.SelectMany(a => a.Sets).Where(x => Key(x) == Key(s)).ToArray();
            var indices = asserted.SelectMany(x => x.VertexIndices).Distinct().Order().ToArray();
            if (!s.VertexIndices.SequenceEqual(indices) || (indices.Length == 0 && (s.SourcePositionHash != ContentHash.Compute([]) || s.SourcePackedFrameHash != ContentHash.Compute([]))))
                throw ProtectionDrift("Protected union differs from exact deduplicated assertions.");
            foreach (var a in asserted.Where(a => a.VertexIndices.SequenceEqual(indices)))
                if (s.SourcePositionHash != a.SourcePositionHash || s.SourcePackedFrameHash != a.SourcePackedFrameHash) throw Invalid("Equal protected sets have inconsistent word identities.");
        }
        foreach (var lod in t.DirectionalTransform.Members[0].Lods)
            if (p.Union.Where(s => s.Lod == lod.Lod && context[Key(s)].Selected).Sum(s => (long)s.VertexCount) == 0)
                throw Invalid("The selected protected union must be nonempty in every LOD.");
    }

    private static void ValidateProtectedSets(IReadOnlyList<DirectionalProtectedSet>? sets, Dictionary<(int Lod, int Mesh, int Buffer), DirectionalContextBuffer> context)
    {
        if (sets is null || sets.Any(s => s is null) || !sets.Select(Key).SequenceEqual(sets.Select(Key).Distinct().Order())) throw Invalid("Protected records must be complete, unique and sorted.");
        foreach (var s in sets)
        {
            if (s.SourcePositionHash != s.ExpectedPositionHash || s.SourcePackedFrameHash != s.ExpectedPackedFrameHash)
                throw Errors.Verification("DIRECTIONAL_PROTECTED_WORD_CHANGED", "A protected position or packed-frame word would change.", "Adjust the explicitly authored field or assertion; never replace output words or subtract a protection mask.");
            if (!context.TryGetValue(Key(s), out var c) || s.SourceDecodedBufferHash != c.DecodedBufferHash || s.VertexIndices is null || s.VertexCount != s.VertexIndices.Count
                || !s.VertexIndices.SequenceEqual(s.VertexIndices.Distinct().Order()) || s.VertexIndices.Any(i => i < 0 || i >= c.VertexCount)
                || s.VertexSetHash != VertexSetHash(s.VertexIndices) || !new[] { s.SourcePositionHash, s.ExpectedPositionHash, s.SourcePackedFrameHash, s.ExpectedPackedFrameHash }.All(Hash)
                || s.SourcePositionHash != s.ExpectedPositionHash || s.SourcePackedFrameHash != s.ExpectedPackedFrameHash
                || (s.VertexCount == 0 && (s.SourcePositionHash != ContentHash.Compute([]) || s.SourcePackedFrameHash != ContentHash.Compute([]))))
                throw ProtectionDrift("Protected source/count/hash drift.");
        }
    }

    private static void ValidateBoxes(PlannedDirectionalTransformTarget t, IReadOnlyDictionary<int, PlannedTargetBlock> sources)
    {
        MutationPlanJson.ValidateMetadataFacts(t.BoxTargets, t.PreservationTargets, sources);
        static (int, string) Box(PlannedExperimentalBoxTarget b) => (b.ResourceBlockIndex, b.FieldPath);
        if (!t.BoxTargets.Select(Box).SequenceEqual(t.BoxTargets.Select(Box).Distinct().OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal))
            || !t.PreservationTargets.Select(p => (p.ResourceBlockIndex, p.FieldPath)).SequenceEqual(t.PreservationTargets.Select(p => (p.ResourceBlockIndex, p.FieldPath)).Distinct().OrderBy(x => x.ResourceBlockIndex).ThenBy(x => x.FieldPath, StringComparer.Ordinal))
            || t.BoxClosures is null || t.BoxClosures.Any(c => c is null)
            || !t.BoxClosures.Select(c => (c.ResourceBlockIndex, c.FieldPath)).SequenceEqual(t.BoxTargets.Select(Box))) throw Invalid("Unique ordered box/preservation/contributor inventories are required.");
        var buffers = t.Buffers.ToDictionary(b => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal));
        for (var i = 0; i < t.BoxTargets.Count; i++)
        {
            var box = t.BoxTargets[i]; var closure = t.BoxClosures[i];
            var rows = t.ContextBuffers.Where(c => c.Lod == closure.Lod && c.MeshOrdinal == closure.MeshOrdinal).ToArray();
            if (rows.Length == 0 || closure.Contributors is null || closure.Contributors.Any(c => c is null)
                || !closure.Contributors.Select(c => (c.Lod, c.MeshOrdinal, c.VertexBufferOrdinal)).SequenceEqual(rows.Select(Key))
                || closure.Contributors.Sum(c => (long)c.ContributorCount) != box.ContributorCount
                || closure.ClosureHash != MutationPlanJson.ComputeDirectionalFactsHash(closure.Contributors)
                || box.ResourceBlockIndex != rows[0].ResourceBlockIndex || box.CoordinateMatrixHash != WordHash(box.CoordinateMatrixWords)
                || box.CoordinateSpace is not ("model" or "render_inverse_bind")
                || box.Growth is not { Count: > 0 } || box.Growth.Select(g => g.Measure).Distinct(StringComparer.Ordinal).Count() != box.Growth.Count)
                throw Invalid("Box contributor or coordinate closure drift.");
            for (var axis = 0; axis < 3; axis++)
            {
                var beforeCenter = (double)BitConverter.UInt32BitsToSingle(box.OriginalWords[axis]);
                var beforeSecond = (double)BitConverter.UInt32BitsToSingle(box.OriginalWords[axis + 3]);
                var afterCenter = (double)BitConverter.UInt32BitsToSingle(box.ExpectedWords[axis]);
                var afterSecond = (double)BitConverter.UInt32BitsToSingle(box.ExpectedWords[axis + 3]);
                var beforeMin = box.Storage == "min_max" ? beforeCenter : beforeCenter - beforeSecond;
                var beforeMax = box.Storage == "min_max" ? beforeSecond : beforeCenter + beforeSecond;
                var afterMin = box.Storage == "min_max" ? afterCenter : afterCenter - afterSecond;
                var afterMax = box.Storage == "min_max" ? afterSecond : afterCenter + afterSecond;
                if (afterMin > beforeMin || afterMax < beforeMax) throw Invalid("Directional boxes must retain authored padding without shrinking.");
            }
            for (var j = 0; j < rows.Length; j++)
            {
                var c = closure.Contributors[j]; var row = rows[j];
                var output = buffers.TryGetValue(Key(row), out var b) ? b.ExpectedPositionHash : row.PositionHash;
                if (c.ContributorCount < 0 || c.ContributorCount > row.VertexCount || !Hash(c.ContributorSetHash)
                    || c.SourcePositionHash != row.PositionHash || c.ExpectedPositionHash != output) throw Invalid("Box contribution positions or counts drifted.");
            }
        }
        // ValidateMetadataFacts requires positive affected spheres. Unaffected resource spheres
        // retain the existing nonnegative preservation rule; they are not zero-field waivers.
    }

    private static ContentHash WordHash(IReadOnlyList<uint> words)
    {
        var bytes = new byte[checked(words.Count * 4)];
        for (var i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return ContentHash.Compute(bytes);
    }
}

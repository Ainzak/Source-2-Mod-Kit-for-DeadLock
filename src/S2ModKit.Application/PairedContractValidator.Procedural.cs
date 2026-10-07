using S2ModKit.Domain;

namespace S2ModKit.Application;

public static partial class PairedContractValidator
{
    public static IReadOnlyList<string> ConsumerCategories { get; } = Array.AsReadOnly(new[]
    { "attachment", "fe_colliders", "fe_controls", "fe_nodes", "fe_rest_drivers", "model_constraint", "physics", "render_skeleton", "root_skeleton", "skin_remap_weights" });

    private static void ValidateProceduralInputs(PlannedPairedTransformTarget t)
    {
        var p = t.ProceduralInputs;
        var source = t.SourceBlocks.ToDictionary(b => b.Index);
        if (p is null || !Hash(p.RootSkeletonHash) || !source.TryGetValue(p.RootDataBlockIndex, out var data) || data.Type != "DATA" || data.InputHash != p.RootPayloadHash
            || p.RootBones is not { Count: > 0 and <= 65536 } || p.RootBones.Any(b => b is null || !Text(b.Name) || !Hash(b.BindHash))
            || !p.RootBones.Select(b => b.Index).SequenceEqual(Enumerable.Range(0, p.RootBones.Count))
            || p.RootBones.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != p.RootBones.Count
            || p.RootFactsHash != MutationPlanJson.ComputeDirectionalFactsHash(p.RootBones)) throw Invalid("Incomplete immutable root skeleton facts.");
        var states = new byte[p.RootBones.Count];
        foreach (var bone in p.RootBones)
        {
            var chain = new List<int>(); var index = bone.Index;
            while (index != -1)
            {
                if (index < 0 || index >= p.RootBones.Count || states[index] == 1) throw Invalid("Root hierarchy is cyclic or out of range.");
                if (states[index] == 2) break;
                states[index] = 1; chain.Add(index);
                index = p.RootBones[index].ParentIndex;
            }
            foreach (var visited in chain) states[visited] = 2;
        }
        var context = t.ContextBuffers.ToDictionary(Key);
        ValidateSkinning(t, p, context);
        foreach (var assertion in t.PairedTransform.Protection.Assertions.OfType<DirectionalBoneAssertion>())
        {
            if (assertion.BoneIndex < 0 || assertion.BoneIndex >= p.RootBones.Count || assertion.RootSkeletonHash != p.RootSkeletonHash
                || assertion.BoneName != p.RootBones[assertion.BoneIndex].Name) throw Invalid("Declared protected root identity differs from the complete root inventory.");
            var resolved = t.Protection.Assertions.Single(a => a.AssertionId == assertion.AssertionId);
            for (var i = 0; i < resolved.Sets.Count; i++)
                if (!resolved.Sets[i].VertexIndices.SequenceEqual(p.SkinningBuffers[i].RootContributors[assertion.BoneIndex].VertexIndices))
                    throw Invalid("Declared protected root omits a nonzero selected/excluded contributor.");
        }
        if (p.BoneContributors is null || p.BoneContributors.Any(b => b is null)
            || !p.BoneContributors.Select(b => b.RootBoneIndex).SequenceEqual(p.RootBones.Where(b => b.Procedural).Select(b => b.Index)))
            throw Invalid("Every flagged procedural root requires complete contributors.");
        foreach (var bone in p.BoneContributors)
        {
            if (bone.RootBoneName != p.RootBones[bone.RootBoneIndex].Name) throw Invalid("Procedural root identity drift.");
            DirectionalContractValidator.ValidateProtectedSets(bone.Sets, context);
            if (!bone.Sets.Select(Key).SequenceEqual(t.ContextBuffers.Select(Key)) || bone.ContributorSetHash != DirectionalContractValidator.ContributorSetHash(bone.Sets))
                throw Invalid("Procedural closure must include all selected/excluded buffers and explicit zero rows.");
            for (var i = 0; i < bone.Sets.Count; i++)
                if (!bone.Sets[i].VertexIndices.SequenceEqual(p.SkinningBuffers[i].RootContributors[bone.RootBoneIndex].VertexIndices))
                    throw Invalid("Procedural contributors differ from complete nonzero skinning contributors.");
        }
        DirectionalContractValidator.ValidateProtectedSets(p.Union, context);
        if (!p.Union.Select(Key).SequenceEqual(t.ContextBuffers.Select(Key)) || p.UnionHash != MutationPlanJson.ComputeDirectionalFactsHash(p.Union))
            throw Invalid("Procedural contributor union is incomplete or stale.");
        foreach (var row in p.Union)
        {
            var contributors = p.BoneContributors.SelectMany(b => b.Sets).Where(s => Key(s) == Key(row)).ToArray();
            if (!row.VertexIndices.SequenceEqual(contributors.SelectMany(s => s.VertexIndices).Distinct().Order())) throw Invalid("Procedural union differs from its complete roots.");
            foreach (var same in contributors.Where(s => s.VertexIndices.SequenceEqual(row.VertexIndices)))
                if (same.SourcePositionHash != row.SourcePositionHash || same.SourcePackedFrameHash != row.SourcePackedFrameHash) throw Invalid("Equal procedural sets have different source words.");
            var buffer = t.Buffers.SingleOrDefault(b => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal) == Key(row));
            if (buffer is not null)
            {
                var audit = t.WordAudits.Single(w => w.MemberId == buffer.MemberId && w.Lod == buffer.Lod);
                if (row.VertexIndices.Intersect(audit.ChangedPositionIndices.Concat(audit.ChangedFrameIndices)).Any())
                    throw Invalid("A procedural contributor would change a position or packed-frame word.");
            }
        }
        ValidateConsumers(t, p, source);
    }

    private static void ValidateSkinning(PlannedPairedTransformTarget t, PlannedPairedProceduralInputs p,
        Dictionary<(int Lod, int Mesh, int Buffer), DirectionalContextBuffer> context)
    {
        if (p.SkinningBuffers is null || p.SkinningBuffers.Any(s => s is null) || !p.SkinningBuffers.Select(Key).SequenceEqual(t.ContextBuffers.Select(Key)))
            throw Invalid("Skinning facts must cover complete selected/excluded context.");
        foreach (var s in p.SkinningBuffers)
        {
            var c = context[Key(s)];
            var slots = s.IndexFormat switch { 30u or 12u or 14u => 4, 4u => 8, _ => 0 };
            var width = s.IndexFormat switch { 30u => 4, 12u or 14u => 8, 4u => 16, _ => 0 };
            var rigid = s.IndexFormat == 30 && s.WeightFormat == 0 && s.DeclaredInfluenceCount == 1 && s.WeightOffset == -1 && s.WeightWidth == 0;
            var weighted = ((s.IndexFormat is 30u or 14u && s.WeightFormat == 28 && s.WeightWidth == 4)
                || (s.IndexFormat is 12u or 4u && s.WeightFormat == 11 && s.WeightWidth == 8)) && s.WeightOffset >= 0;
            if (slots == 0 || s.IndexWidth != width || s.DeclaredInfluenceCount < 1 || s.DeclaredInfluenceCount > slots || !(rigid || weighted)
                || s.SourceSkinningHash != c.SkinningHash || s.ExpectedSkinningHash != s.SourceSkinningHash || s.RootRenderRemapHash != c.RootRenderRemapHash
                || s.RootContributors is null || s.RootContributors.Any(r => r is null)
                || !s.RootContributors.Select(r => r.RootBoneIndex).SequenceEqual(Enumerable.Range(0, p.RootBones.Count))) throw Invalid("Skin layout or complete root/remap contributor identity drift.");
            var ranges = new List<(int Offset, int Width)> { (s.PositionOffset, 12), (s.PackedFrameOffset, 4), (s.IndexOffset, s.IndexWidth) };
            if (!rigid) ranges.Add((s.WeightOffset, s.WeightWidth));
            if (s.Stride <= 0 || ranges.Any(r => r.Offset < 0 || (long)r.Offset + r.Width > s.Stride)
                || ranges.SelectMany((a, i) => ranges.Skip(i + 1).Select(b => a.Offset < (long)b.Offset + b.Width && b.Offset < (long)a.Offset + a.Width)).Any(overlap => overlap))
                throw Invalid("Complete vertex layout ranges overlap or exceed the stride.");
            var selected = t.Buffers.SingleOrDefault(b => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal) == Key(s));
            if (selected is not null && (s.Stride != selected.PositionLayout.Stride || s.PositionOffset != selected.PositionLayout.Offset
                || s.PackedFrameOffset != selected.PackedFrameLayout.Offset)) throw Invalid("Selected layout and skinning facts disagree.");
            var counts = new int[c.VertexCount];
            foreach (var root in s.RootContributors)
            {
                ValidateIndices(root.VertexIndices, c.VertexCount);
                if (root.VertexSetHash != DirectionalContractValidator.VertexSetHash(root.VertexIndices)) throw Invalid("Nonzero root contributor set hash drift.");
                foreach (var vertex in root.VertexIndices) counts[vertex]++;
            }
            if (counts.Any(n => n < 1 || n > s.DeclaredInfluenceCount) || s.ContributorHash != MutationPlanJson.ComputeDirectionalFactsHash(s.RootContributors))
                throw Invalid("Every vertex record must have a bounded complete nonzero contributor inventory.");
        }
    }

    private static void ValidateConsumers(PlannedPairedTransformTarget t, PlannedPairedProceduralInputs p, Dictionary<int, PlannedTargetBlock> source)
    {
        if (p.ConsumerFamilies is null || p.ConsumerFamilies.Any(f => f is null)
            || !p.ConsumerFamilies.Select(f => f.Category).SequenceEqual(ConsumerCategories)
            || p.ConsumerInventoryHash != MutationPlanJson.ComputeDirectionalFactsHash(p.ConsumerFamilies)) throw Invalid("All named serialized consumer families require an explicit presence/absence inventory.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in p.ConsumerFamilies)
        {
            if (family.Records is null || family.Records.Count > 65536 || family.Present != (family.Records.Count > 0)
                || family.Records.Any(r => r is null) || !family.Records.Select(r => r.ConsumerId).SequenceEqual(family.Records.Select(r => r.ConsumerId).Order(StringComparer.Ordinal)))
                throw Invalid("Consumer presence, inventory or canonical order drift.");
            if (family.Records.Select(r => (r.ResourceBlockIndex, r.FieldPath)).Distinct().Count() != family.Records.Count)
                throw Invalid("A named consumer family contains duplicate serialized input fields.");
            foreach (var record in family.Records)
            {
                var expectedType = family.Category switch
                {
                    "root_skeleton" or "model_constraint" => "DATA",
                    "attachment" => null,
                    "render_skeleton" => "MDAT",
                    "skin_remap_weights" => null,
                    _ => "PHYS"
                };
                if (!Identifier(record.ConsumerId) || !ids.Add(record.ConsumerId) || !Text(record.FieldPath) || record.FieldPath.Contains(':')
                    || record.FieldPath.Contains('\\') || record.FieldPath.StartsWith('/') || record.Disposition != "preserve_unverified"
                    || !source.TryGetValue(record.ResourceBlockIndex, out var block) || (expectedType is not null ? block.Type != expectedType : family.Category == "attachment" ? block.Type is not ("DATA" or "MDAT") : block.Type is not ("MDAT" or "MVTX"))
                    || !Hash(record.SourcePayloadHash) || record.ExpectedPayloadHash != record.SourcePayloadHash
                    || (record.FieldPath == "$payload" && record.SourcePayloadHash != block.InputHash)
                    || record.References is null || record.References.Any(r => r is null)
                    || record.ReferenceHash != MutationPlanJson.ComputeDirectionalFactsHash(record.References)
                    || t.BoxTargets.Any(b => b.ResourceBlockIndex == record.ResourceBlockIndex && OverlapPath(b.FieldPath, record.FieldPath))) throw Invalid("Unknown, changed, ambiguous or overlapping serialized consumer inputs.");
                var referenceIds = record.References.Select(r => (r.Kind, r.ScopeId, r.SourceIndex)).ToArray();
                if (!referenceIds.SequenceEqual(referenceIds.Distinct().OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.ScopeId, StringComparer.Ordinal).ThenBy(r => r.SourceIndex)))
                    throw Invalid("Consumer references must be unique and canonically ordered.");
                foreach (var reference in record.References)
                {
                    if (reference.Kind is not ("root_bone" or "render_bone" or "attachment" or "fe_control" or "fe_node")
                        || !Identifier(reference.ScopeId) || reference.SourceIndex < 0 || !Text(reference.SourceName) || !Hash(reference.IdentityHash) || !Hash(reference.RootIdentityHash)
                        || reference.Disposition != "preserve_unverified" || reference.RootBoneIndex < -1 || reference.RootBoneIndex >= p.RootBones.Count
                        || (reference.Kind is "root_bone" or "render_bone" && reference.RootBoneIndex < 0)
                        || (reference.RootBoneIndex >= 0 && (reference.RootBoneName != p.RootBones[reference.RootBoneIndex].Name
                            || reference.RootIdentityHash != MutationPlanJson.ComputeDirectionalFactsHash(p.RootBones[reference.RootBoneIndex])))
                        || (reference.RootBoneIndex == -1 && (reference.RootBoneName != "" || reference.RootIdentityHash != ContentHash.Compute([])))
                        || (reference.Kind is "root_bone" or "render_bone" && reference.SourceName != reference.RootBoneName)
                        || (reference.Kind == "root_bone" && (reference.SourceIndex != reference.RootBoneIndex || reference.IdentityHash != reference.RootIdentityHash))) throw Invalid("Consumer token/root identity is unresolved or ambiguous outside the named policy.");
                }
            }
        }
        var roots = p.ConsumerFamilies.Single(f => f.Category == "root_skeleton").Records;
        if (roots.Count != 1 || roots[0].ResourceBlockIndex != p.RootDataBlockIndex || roots[0].FieldPath != "$payload" || roots[0].SourcePayloadHash != p.RootPayloadHash)
            throw Invalid("Complete root DATA must be preserved exactly.");
        var rendered = p.ConsumerFamilies.Single(f => f.Category == "render_skeleton").Records.Select(r => r.ResourceBlockIndex).ToHashSet();
        var skins = p.ConsumerFamilies.Single(f => f.Category == "skin_remap_weights").Records.Select(r => r.ResourceBlockIndex).ToHashSet();
        if (!rendered.SetEquals(t.ContextBuffers.Select(c => c.ResourceBlockIndex))
            || !skins.SetEquals(t.ContextBuffers.SelectMany(c => new[] { c.ResourceBlockIndex, c.VertexResourceBlockIndex })))
            throw Invalid("Render skeleton and skin/remap inputs must cover every context mesh and buffer.");
        var physics = p.ConsumerFamilies.Single(f => f.Category == "physics").Records;
        if (!physics.Select(r => r.ResourceBlockIndex).SequenceEqual(source.Values.Where(b => b.Type == "PHYS").Select(b => b.Index).Order())
            || physics.Any(r => r.FieldPath != "$payload" || !t.PreservationTargets.Any(target => target.ResourceBlockIndex == r.ResourceBlockIndex
                && target.Category == "collision_payload" && target.FieldPath == "$payload" && target.SourcePayloadHash == r.SourcePayloadHash)))
            throw Invalid("Complete PHYS payload inventory/preservation is required.");
    }

    private static bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    private static bool Identifier(string? text) => text is { Length: >= 1 and <= 64 } && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.')
        && text[0] is >= 'a' and <= 'z' or >= '0' and <= '9';
    private static bool OverlapPath(string a, string b) => a == "$payload" || b == "$payload" || a == b
        || a.StartsWith(b + ".", StringComparison.Ordinal) || a.StartsWith(b + "[", StringComparison.Ordinal)
        || b.StartsWith(a + ".", StringComparison.Ordinal) || b.StartsWith(a + "[", StringComparison.Ordinal);
}

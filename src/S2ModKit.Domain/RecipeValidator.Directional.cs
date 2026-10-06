namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    private static void ValidateDirectionalTransform(TransformComponentOperation operation)
    {
        if (operation.Transform is not null || operation.Region is not null || operation.LocalTransform is not null
            || operation.CoordinatedTransform is not null || operation.PhysicsPolicy is not null || operation.ConnectedComponentIdsByLod is not null
            || operation.Limits is null || operation.Limits.MaximumCollisionDisplacement is not null
            || operation.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || operation.ZeroBoneBoxPolicy is not { Kind: "reject", Version: 1 }
            || operation.ZeroRenderSpherePolicy is not { Kind: "reject", Version: 1 })
            throw DirectionalInvalid("Require explicit preservation/rejection policies and isolated directional intent.");
        ValidateDirectionalIntent(operation.DirectionalTransform, operation.Limits.MaximumVertexDisplacement);
        var intent = operation.DirectionalTransform!;
        ValidatePivot(operation, intent.Field.Pivot);
        var maps = intent.Members.SelectMany(m => m.Lods).ToArray();
        var ids = maps.SelectMany(l => l.DrawCallIds).Order(StringComparer.Ordinal);
        if (operation.Selector is not { Kind: "draw_call_ids", DrawCallIds: not null, MaterialPath: null }
            || !operation.Selector.DrawCallIds.SequenceEqual(ids, StringComparer.Ordinal)
            || operation.ExpectedMatchesByLod.Count != intent.Members[0].Lods.Count)
            throw DirectionalInvalid("Selector and expectations must equal the complete member/LOD union.");
        foreach (var group in maps.GroupBy(l => l.Lod))
        {
            var key = group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!operation.ExpectedMatchesByLod.TryGetValue(key, out var matches) || matches != group.Sum(l => l.DrawCallIds.Count)
                || !operation.ExpectedVerticesByLod.TryGetValue(key, out var vertices) || vertices != group.Sum(l => (long)l.ExpectedVertices))
                throw DirectionalInvalid("Per-LOD expectations differ from complete member sums.");
        }
    }

    public static void ValidateDirectionalIntent(DirectionalVisualTransform? intent, float limit)
    {
        if (intent?.Members is not { Count: >= 1 and <= 16 } members || members.Any(m => m is null)
            || !members.Select(m => m.MemberId).SequenceEqual(members.Select(m => m.MemberId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw DirectionalInvalid("Require 1–16 sorted unique ordinary members.");
        var drawIds = new HashSet<string>(StringComparer.Ordinal);
        int[]? expectedLods = null;
        foreach (var member in members)
        {
            if (member.MemberId is null || !IdentifierRegex().IsMatch(member.MemberId) || member.Lods is not { Count: >= 1 and <= 8 }
                || member.Lods.Any(l => l is null)) throw DirectionalInvalid("Malformed member/LOD mapping.");
            var lods = member.Lods.Select(l => l.Lod).ToArray();
            if (!lods.SequenceEqual(Enumerable.Range(0, lods.Length)) || (expectedLods is not null && !lods.SequenceEqual(expectedLods)))
                throw DirectionalInvalid("All members require the same contiguous ascending LODs starting at zero.");
            expectedLods = lods;
            foreach (var map in member.Lods)
                if (map.ExpectedVertices <= 0 || map.DrawCallIds is not { Count: >= 1 and <= 64 }
                    || map.DrawCallIds.Any(id => id is null || !DrawCallIdRegex().IsMatch(id) || !drawIds.Add(id))
                    || !map.DrawCallIds.SequenceEqual(map.DrawCallIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    throw DirectionalInvalid("Require positive buffer counts and sorted globally unique exact draw-call IDs.");
        }
        if (members.SelectMany(m => m.Lods).GroupBy(l => l.Lod).Any(g => g.Sum(l => (long)l.ExpectedVertices) > int.MaxValue))
            throw DirectionalInvalid("Per-LOD vertex totals exceed the supported cardinality.");
        var f = intent.Field;
        if (!float.IsFinite(limit) || limit is <= 0 or > 64 || f is not { Kind: "directional_ellipsoid", Version: 1, CoordinateSpace: "model" }
            || f.NumericalPolicy is not { Kind: "directional_ellipsoid_numeric", Version: 1 } || f.Pivot is null
            || f.OuterRadii is not { } radii || f.Scale is not { } scale
            || !new[] { radii.X, radii.Y, radii.Z, scale.X, scale.Y, scale.Z, f.CoreFraction }.All(float.IsFinite)
            || radii.X <= 0 || radii.Y <= 0 || radii.Z <= 0
            || Math.Max(radii.X, Math.Max(radii.Y, radii.Z)) > 8d * Math.Min(radii.X, Math.Min(radii.Y, radii.Z))
            || f.CoreFraction is <= 0 or >= 1 || new[] { scale.X, scale.Y, scale.Z }.Any(s => s is < 0.5f or > 2)
            || (scale.X == 1 && scale.Y == 1 && scale.Z == 1)) throw DirectionalInvalid("Invalid directional field domain or numerical policy.");
        var p = intent.Protection;
        if (p is not { Kind: "keep_fixed", Version: 1, Assertions.Count: >= 1 and <= 64 }
            || p.Assertions.Any(a => a is null || a.Version != 1 || a.AssertionId is null || !IdentifierRegex().IsMatch(a.AssertionId))
            || !p.Assertions.Select(a => a.AssertionId).SequenceEqual(p.Assertions.Select(a => a.AssertionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw ProtectionInvalid("Require 1–64 sorted unique keep-fixed assertions.");
        var memberMaps = members.SelectMany(m => m.Lods.Select(l => (m.MemberId, l.Lod, l.ExpectedVertices))).OrderBy(x => x.MemberId, StringComparer.Ordinal).ThenBy(x => x.Lod).ToArray();
        foreach (var a in p.Assertions)
            switch (a)
            {
                case DirectionalVertexAssertion vertex:
                    if (vertex.Sets is null || vertex.Sets.Any(s => s is null)
                        || !vertex.Sets.Select(s => (s.MemberId, s.Lod)).SequenceEqual(memberMaps.Select(m => (m.MemberId, m.Lod))))
                        throw ProtectionInvalid("Every vertex assertion requires an explicit row for every member/LOD, including empty sets.");
                    for (var i = 0; i < vertex.Sets.Count; i++)
                    {
                        var s = vertex.Sets[i];
                        if (s.VertexIndices is null || s.VertexCount != s.VertexIndices.Count || s.VertexIndices.Any(v => v < 0 || v >= memberMaps[i].ExpectedVertices)
                            || !s.VertexIndices.SequenceEqual(s.VertexIndices.Distinct().Order()) || !DirectionalHash(s.SourceDecodedBufferHash) || !DirectionalHash(s.VertexSetHash))
                            throw ProtectionInvalid("Invalid sorted vertex indices, source identity or count.");
                    }
                    if (vertex.Sets.Sum(s => (long)s.VertexCount) == 0) throw ProtectionInvalid("An assertion cannot be empty across all LODs.");
                    break;
                case DirectionalBoneAssertion bone:
                    if (string.IsNullOrWhiteSpace(bone.BoneName) || bone.BoneName.Any(char.IsControl) || bone.BoneName.Contains('*') || bone.BoneName.Contains('?')
                        || bone.BoneIndex < 0 || !DirectionalHash(bone.RootSkeletonHash) || bone.Lods is null || bone.Lods.Any(l => l is null)
                        || !bone.Lods.Select(l => l.Lod).SequenceEqual(expectedLods!) || bone.Lods.Any(l => l.ContributorCount < 0 || !DirectionalHash(l.ContributorSetHash))
                        || bone.Lods.Sum(l => (long)l.ContributorCount) == 0) throw ProtectionInvalid("Invalid exact root-bone identity or complete contributor expectations.");
                    break;
                default: throw ProtectionInvalid("Unknown protection assertion variant.");
            }
        foreach (var lod in expectedLods!)
            if (!p.Assertions.Any(a => a is DirectionalVertexAssertion vertex && vertex.Sets.Any(s => s.Lod == lod && s.VertexCount > 0)
                || a is DirectionalBoneAssertion bone && bone.Lods.Any(l => l.Lod == lod && l.ContributorCount > 0)))
                throw ProtectionInvalid("Protection must have a nonempty declared union in every LOD.");
    }

    private static bool DirectionalHash(ContentHash h) => h.Value is { Length: 64 };
    private static S2ModKitException DirectionalInvalid(string message) => Errors.InvalidRecipe("DIRECTIONAL_INTENT_INVALID", message,
        "Use one centered versioned directional field, exact complete member mappings and explicit keep-fixed assertions.");
    private static S2ModKitException ProtectionInvalid(string message) => Errors.InvalidRecipe("DIRECTIONAL_PROTECTION_INVALID", message,
        "Resolve exact source-bound vertex or root-bone contributor assertions across every present LOD.");
}

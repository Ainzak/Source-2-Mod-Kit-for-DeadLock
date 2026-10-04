namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    private static void ValidateCoordinatedTransform(TransformComponentOperation operation)
    {
        if (operation.Transform is not null || operation.Region is not null || operation.LocalTransform is not null
            || operation.ConnectedComponentIdsByLod is not null || operation.PhysicsPolicy is not null
            || operation.Limits is null || operation.Limits.MaximumCollisionDisplacement is not null
            || operation.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || operation.ZeroBoneBoxPolicy is not { Kind: "reject" or "preserve_all_zero_single_contributor_unverified", Version: 1 }
            || operation.ZeroRenderSpherePolicy is not { Kind: "reject" or "preserve_zero_render_sphere_with_zero_box_unverified", Version: 1 }
            || (operation.ZeroRenderSpherePolicy.Kind != "reject" && operation.ZeroBoneBoxPolicy.Kind == "reject"))
            throw CoordinatedInvalid("Require explicit preservation/rejection policies and no legacy, region or collision intent.");
        ValidateCoordinatedIntent(operation.CoordinatedTransform, operation.Limits.MaximumVertexDisplacement);
        var maps = operation.CoordinatedTransform!.Members.SelectMany(member => member.Lods).ToArray();
        var ids = maps.SelectMany(map => map.DrawCallIds).Order(StringComparer.Ordinal).ToArray();
        if (operation.Selector is not { Kind: "draw_call_ids", DrawCallIds: not null, MaterialPath: null }
            || !operation.Selector.DrawCallIds.SequenceEqual(ids, StringComparer.Ordinal))
            throw CoordinatedInvalid("The exact sorted union selector must equal all member/LOD draw-call mappings.");
        if (maps.Select(map => map.Lod).Distinct().Count() != operation.ExpectedMatchesByLod.Count)
            throw CoordinatedInvalid("Operation and member LOD coverage disagree.");
        foreach (var group in maps.GroupBy(map => map.Lod))
        {
            var key = group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!operation.ExpectedMatchesByLod.TryGetValue(key, out var matches) || matches != group.Sum(map => map.DrawCallIds.Count)
                || !operation.ExpectedVerticesByLod.TryGetValue(key, out var vertices) || vertices != group.Sum(map => (long)map.ExpectedVertices))
                throw CoordinatedInvalid("Per-LOD union match/vertex expectations must equal the exact member sums.");
        }
    }
    /// <summary>Pure intent checks. Actual all-present LOD/ownership/closure admission belongs to source planning.</summary>
    public static void ValidateCoordinatedIntent(CoordinatedVisualTransform? intent, float displacementLimit)
    {
        if (intent?.Members is not { Count: >= 2 and <= 16 } members || intent.Field is not { Version: 1, CoordinateSpace: "model" })
            throw CoordinatedInvalid("Require 2–16 explicit ordinary members and one version-1 model-space field.");
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var allDrawIds = new HashSet<string>(StringComparer.Ordinal);
        int[]? declaredLods = null;
        string? previousMember = null;
        foreach (var member in members)
        {
            if (member is null || member.MemberId is null || !IdentifierRegex().IsMatch(member.MemberId)
                || !memberIds.Add(member.MemberId) || (previousMember is not null && string.CompareOrdinal(previousMember, member.MemberId) >= 0)
                || member.Lods is not { Count: >= 1 and <= 8 })
                throw CoordinatedInvalid("Members need sorted unique identifiers and 1–8 explicit LOD mappings.");
            previousMember = member.MemberId;
            var lods = member.Lods.Select(lod => lod?.Lod ?? -1).ToArray();
            if (!lods.SequenceEqual(Enumerable.Range(0, member.Lods.Count)) || (declaredLods is not null && !declaredLods.SequenceEqual(lods)))
                throw CoordinatedInvalid("Each member must name the same complete ascending LOD set beginning at zero.");
            declaredLods = lods;
            foreach (var lod in member.Lods)
            {
                if (lod.ExpectedVertices <= 0 || lod.DrawCallIds is not { Count: >= 1 and <= 64 }
                    || lod.DrawCallIds.Any(id => id is null || !DrawCallIdRegex().IsMatch(id))
                    || !lod.DrawCallIds.SequenceEqual(lod.DrawCallIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                    || lod.DrawCallIds.Any(id => !allDrawIds.Add(id)))
                    throw CoordinatedInvalid("Every member/LOD needs a positive vertex count and sorted globally unique exact draw-call IDs.");
            }
        }
        if (!float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw CoordinatedInvalid("The finite displacement cap must be in (0,64].");
        ValidateCoordinatedField(intent.Field, displacementLimit);
    }

    public static void ValidateCoordinatedField(CoordinatedField? field, float displacementLimit)
    {
        if (field is not { Version: 1, CoordinateSpace: "model" }
            || !float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw CoordinatedInvalid("Require one version-1 model-space field and a finite cap in (0,64].");
        switch (field)
        {
            case CoordinatedEllipsoidField ellipsoid:
                if (ellipsoid.Intent?.Field is not { Kind: "ellipsoid", MirrorPlane: null }) throw CoordinatedInvalid("Coordinated fields retain single ellipsoid semantics; mirrored intent is version 7 only.");
                ValidateEllipsoidIntent(ellipsoid.Intent, displacementLimit);
                break;
            case CoordinatedAxisRampField axis:
                if (axis.Axis is not ("x" or "y" or "z")) throw CoordinatedInvalid("The axis ramp needs an explicit model axis.");
                ValidateCoordinatedRamp(axis.PinnedThrough, axis.FullFrom, axis.UniformScale, axis.Pivot);
                break;
            case CoordinatedTiltedRampField tilted:
                if (tilted.FirstAxis is not ("x" or "y" or "z") || tilted.SecondAxis is not ("x" or "y" or "z")
                    || string.CompareOrdinal(tilted.FirstAxis, tilted.SecondAxis) >= 0
                    || tilted.FirstSign is not (-1 or 1) || tilted.SecondSign is not (-1 or 1)
                    || tilted.NumericalPolicy is not { Kind: "tilted_ramp_numeric", Version: 1 })
                    throw CoordinatedInvalid("The tilted ramp needs sorted distinct axes, explicit signs and tilted_ramp_numeric@1.");
                ValidateCoordinatedRamp(tilted.PinnedThrough, tilted.FullFrom, tilted.UniformScale, tilted.Pivot);
                break;
            default:
                throw CoordinatedInvalid("The deformation field variant is unsupported.");
        }
    }

    private static void ValidateCoordinatedRamp(float pin, float full, float scale, TransformVector3? pivot)
    {
        if (!float.IsFinite(pin) || !float.IsFinite(full) || pin >= full || !float.IsFinite(scale)
            || scale is < 0.5f or > 2f || scale == 1 || pivot is null
            || !float.IsFinite(pivot.X) || !float.IsFinite(pivot.Y) || !float.IsFinite(pivot.Z))
            throw CoordinatedInvalid("A ramp needs finite ordered thresholds, a finite explicit pivot and nonidentity scale in [0.5,2].");
    }

    private static S2ModKitException CoordinatedInvalid(string message) => Errors.InvalidRecipe("COORDINATED_INTENT_INVALID", message,
        "Use explicit sorted all-LOD complete-buffer mappings and one acknowledged common field; no inferred anatomy or independent-plan composition.");
}

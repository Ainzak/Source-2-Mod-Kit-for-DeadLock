namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    private static void ValidateEllipsoidTransform(TransformComponentOperation operation)
    {
        if (operation.Transform is not null || operation.Region is not null || operation.ConnectedComponentIdsByLod is not null
            || operation.PhysicsPolicy is not null || operation.Limits is null || operation.Limits.MaximumCollisionDisplacement is not null)
            throw EllipsoidInvalid("Local fields cannot mix legacy transform, region, component or collision intent.");
        if (operation.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 })
            throw EllipsoidInvalid("Explicit preserve_unverified@1 acknowledgement is required.");
        ValidateEllipsoidIntent(operation.LocalTransform, operation.Limits.MaximumVertexDisplacement);
    }

    public static void ValidateEllipsoidIntent(EllipsoidVisualTransform? intent, float displacementLimit)
    {
        if (intent?.Field is not { Version: 1, CoordinateSpace: "model", Center: not null, OuterRadii: not null } field
            || field.Kind is not ("ellipsoid" or "mirrored_ellipsoids")
            || (field.Kind == "ellipsoid" ? field.MirrorPlane is not null : field.MirrorPlane is not { Axis: "x" or "y" or "z" } plane || !float.IsFinite(plane.Coordinate))
            || intent.NumericalPolicy is not { Kind: "ellipsoid_numeric", Version: 1 })
            throw EllipsoidInvalid("Require ellipsoid@1 in model space and ellipsoid_numeric@1.");
        var radii = new[] { field.OuterRadii.X, field.OuterRadii.Y, field.OuterRadii.Z };
        if (!float.IsFinite(field.Center.X) || !float.IsFinite(field.Center.Y) || !float.IsFinite(field.Center.Z)
            || radii.Any(r => !float.IsFinite(r) || r <= 0) || (double)radii.Max() > (double)radii.Min() * 8
            || !float.IsFinite(field.CoreFraction) || field.CoreFraction is <= 0 or >= 1
            || !float.IsFinite(intent.UniformScale) || intent.UniformScale is < 0.5f or > 2 || intent.UniformScale == 1
            || !float.IsFinite(displacementLimit) || displacementLimit is <= 0 or > 64)
            throw EllipsoidInvalid("Finite center, positive radii (aspect <=8), 0<h<1, nonidentity scale [0.5,2] and limit (0,64] are required.");
    }

    private static S2ModKitException EllipsoidInvalid(string message) => Errors.InvalidRecipe("ELLIPSOID_INTENT_INVALID", message,
        "Use one explicit version-7 local field with its complete numerical and preservation policy.");
}

using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static class EllipsoidFieldMath
{
    public static IEllipsoidScale Create(EllipsoidVisualTransform intent, float limit)
    {
        RecipeValidator.ValidateEllipsoidIntent(intent, limit);
        var f = intent.Field;
        try
        {
            return f.MirrorPlane is { } plane
                ? new MirroredEllipsoidScale(Point(f.Center), Point(f.OuterRadii), f.CoreFraction, intent.UniformScale, limit,
                    plane.Axis switch { "x" => 0, "y" => 1, _ => 2 }, plane.Coordinate)
                : new EllipsoidScale(Point(f.Center), Point(f.OuterRadii), f.CoreFraction, intent.UniformScale, limit);
        }
        catch (EllipsoidMirrorException ex) { throw Errors.Unsupported(ex.Code, ex.Message, "Use exactly representable reflected centers and disjoint supports."); }
        catch (ArgumentException ex) { throw Errors.Unsupported("ELLIPSOID_JACOBIAN_UNPROVEN", ex.Message, "Choose an admitted field and regenerate the plan."); }
    }

    public static PlannedEllipsoidMirror? Mirror(IEllipsoidScale field) => field is MirroredEllipsoidScale pair
        ? new(new() { X = pair.Reflected.Center.X, Y = pair.Reflected.Center.Y, Z = pair.Reflected.Center.Z }, EllipsoidContractValidator.Certificate(pair.Reflected.Certificate)) : null;
    private static Point3 Point(TransformVector3 p) => new(p.X, p.Y, p.Z);
}

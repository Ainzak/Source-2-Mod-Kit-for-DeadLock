using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public enum CoordinatedMembership { Full, Transition, Pinned }
public readonly record struct CoordinatedPointResult(Point3 Position, CoordinatedMembership Membership, double Weight, float MaximumDisplacement);

/// <summary>Reviewed pure fields only; this supplies no source ownership or cached mutation proof.</summary>
public sealed class CoordinatedFieldMath
{
    private readonly EllipsoidScale? ellipsoid;
    private readonly AxisRampScale? axis;
    private readonly TiltedRampScale? tilted;
    private readonly float limit;
    public PlannedCoordinatedFieldProof Proof { get; }

    public CoordinatedFieldMath(CoordinatedField field, float displacementLimit)
    {
        RecipeValidator.ValidateCoordinatedField(field, displacementLimit);
        limit = displacementLimit;
        try
        {
            switch (field)
            {
                case CoordinatedEllipsoidField e:
                    var intent = e.Intent;
                    ellipsoid = new(Point(intent.Field.Center), Point(intent.Field.OuterRadii), intent.Field.CoreFraction, intent.UniformScale, limit);
                    Proof = new("ellipsoid", 1, EllipsoidContractValidator.Certificate(ellipsoid.Certificate), null);
                    break;
                case CoordinatedAxisRampField a:
                    axis = new(Axis(a.Axis), a.PinnedThrough, a.FullFrom, a.UniformScale, Point(a.Pivot));
                    Proof = new("axis_ramp", 1, null, null);
                    break;
                case CoordinatedTiltedRampField t:
                    tilted = new(Axis(t.FirstAxis), t.FirstSign, Axis(t.SecondAxis), t.SecondSign,
                        t.PinnedThrough, t.FullFrom, t.UniformScale, Point(t.Pivot), limit);
                    Proof = new("tilted_ramp", 1, null, new("signed_two_axis_adverse_bound", 1, tilted.Certificate.Numerator, tilted.Certificate.Denominator));
                    break;
                default: throw new ArgumentException("Unknown common field.");
            }
        }
        catch (ArgumentException error)
        {
            throw Errors.Unsupported("COORDINATED_JACOBIAN_UNPROVEN", error.Message, "Choose a field whose global numerical certificate is admitted.");
        }
    }

    public CoordinatedPointResult Evaluate(Point3 point)
    {
        if (ellipsoid is not null)
        {
            var result = ellipsoid.Evaluate(point);
            return new(result.Position, (CoordinatedMembership)result.Membership, result.Weight, result.MaximumDisplacement);
        }
        if (tilted is not null)
        {
            var result = tilted.Evaluate(point);
            return new(result.Position, result.Membership switch
            { TiltedRampMembership.Full => CoordinatedMembership.Full, TiltedRampMembership.Transition => CoordinatedMembership.Transition, _ => CoordinatedMembership.Pinned }, result.Weight, result.MaximumDisplacement);
        }
        var axisResult = axis!.Evaluate(point);
        var distance = ConservativePointDistance.RoundUp(point, axisResult.Position);
        if (distance > limit) throw new ArgumentException("Stored-point displacement exceeds the common field's cap.");
        // Preserve the version-6 strength decisions, including rounded endpoint weights.
        return new(axisResult.Position, axisResult.Weight == 0 ? CoordinatedMembership.Pinned : axisResult.Weight == 1
            ? CoordinatedMembership.Full : CoordinatedMembership.Transition, axisResult.Weight, distance);
    }

    public TangentFrame TransformFrame(Point3 point, TangentFrame frame) => ellipsoid is not null ? ellipsoid.TransformFrame(point, frame)
        : tilted is not null ? tilted.TransformFrame(point, frame) : axis!.TransformFrame(point, frame);

    private static int Axis(string value) => value switch { "x" => 0, "y" => 1, "z" => 2, _ => throw new ArgumentException("Unknown axis.") };
    private static Point3 Point(TransformVector3 value) => new(value.X, value.Y, value.Z);
}

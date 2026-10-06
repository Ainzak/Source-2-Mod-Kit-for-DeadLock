namespace S2ModKit.Geometry;

/// <summary>Read-side reconstruction of the prescribed arithmetic, independent of the deformation evaluator.</summary>
public sealed class DirectionalFieldReconstruction
{
    private readonly Point3 center;
    private readonly Point3 radii;
    private readonly float core;
    private readonly Point3 scale;
    private readonly float limit;
    public DirectionalEllipsoidCertificate Certificate { get; }

    public DirectionalFieldReconstruction(Point3 center, Point3 radii, float core, Point3 scale, float limit)
    {
        if (!float.IsFinite(limit) || limit is <= 0 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        this.center = center; this.radii = radii; this.core = core; this.scale = scale; this.limit = limit;
        Certificate = DirectionalEllipsoidCertificate.Create(core, scale, radii);
    }

    public EllipsoidPointResult ReconstructPosition(Point3 source)
    {
        var state = Classify(source);
        if (state.Region == EllipsoidMembership.Pinned) return new(source, state.Region, 0, 0);
        var factors = AxisFactors(state);
        float Axis(float original, float origin, float multiplier, double factor) => multiplier == 1
            ? original : Store((double)origin + (factor * ((double)original - origin)));
        var expected = new Point3(Axis(source.X, center.X, scale.X, factors[0]),
            Axis(source.Y, center.Y, scale.Y, factors[1]), Axis(source.Z, center.Z, scale.Z, factors[2]));
        var distance = ConservativePointDistance.RoundUp(source, expected);
        if (distance > limit) throw new ArgumentException("Reconstructed stored displacement exceeds the cap.", nameof(source));
        return new(expected, state.Region, state.Weight, distance);
    }

    public TangentFrame ReconstructFrame(Point3 source, TangentFrame input)
    {
        Validate(input);
        var state = Classify(source);
        if (state.Region == EllipsoidMembership.Pinned || (state.Region == EllipsoidMembership.Core && scale.X == scale.Y && scale.Y == scale.Z)) return input;
        var axes = AxisFactors(state);
        double[] displacement = [(double)source.X - center.X, (double)source.Y - center.Y, (double)source.Z - center.Z];
        double[] changes = [(double)scale.X - 1, (double)scale.Y - 1, (double)scale.Z - 1];
        double[] radius = [radii.X, radii.Y, radii.Z];
        var column = new double[3]; var row = new double[3];
        if (state.Region == EllipsoidMembership.Transition)
        {
            var derivative = ((-6 * state.T) * (1 - state.T)) / (1 - (double)core);
            for (var axis = 0; axis < 3; axis++)
            {
                column[axis] = changes[axis] * displacement[axis];
                row[axis] = (((derivative * displacement[axis]) / radius[axis]) / radius[axis]) / state.Rho;
                if (!double.IsFinite(column[axis]) || !double.IsFinite(row[axis])) throw new ArgumentException("Nonfinite reconstructed differential.");
            }
        }
        double[] dividedColumn = [column[0] / axes[0], column[1] / axes[1], column[2] / axes[2]];
        var denominator = 1 + Product(row, dividedColumn);
        if (!double.IsFinite(denominator) || EllipsoidRational.FromDouble(denominator).Compare(Certificate.BetaLowerBound.Exact) < 0)
            throw new ArgumentException("Reconstructed differential cannot prove its beta bound.");
        double[] normal = [input.Normal.X / axes[0], input.Normal.Y / axes[1], input.Normal.Z / axes[2]];
        var normalProjection = Product(column, normal) / denominator;
        for (var axis = 0; axis < 3; axis++) normal[axis] -= (row[axis] / axes[axis]) * normalProjection;
        Unit(normal);
        double[] tangent = [input.Tangent.X, input.Tangent.Y, input.Tangent.Z];
        var tangentProjection = Product(row, tangent);
        for (var axis = 0; axis < 3; axis++) tangent[axis] = (axes[axis] * tangent[axis]) + (column[axis] * tangentProjection);
        tangentProjection = Product(normal, tangent);
        for (var axis = 0; axis < 3; axis++) tangent[axis] -= normal[axis] * tangentProjection;
        Unit(tangent);
        var result = new TangentFrame(new(Store(normal[0]), Store(normal[1]), Store(normal[2])),
            new(Store(tangent[0]), Store(tangent[1]), Store(tangent[2])), input.Handedness);
        Validate(result);
        return result;
    }

    private readonly record struct State(EllipsoidMembership Region, double Weight, double T, double Rho);
    private State Classify(Point3 source)
    {
        EllipsoidRational Term(float position, float origin, float radius)
        {
            var quotient = (EllipsoidRational.FromDouble(position) - EllipsoidRational.FromDouble(origin)) / EllipsoidRational.FromDouble(radius);
            return quotient * quotient;
        }
        var squared = (Term(source.X, center.X, radii.X) + Term(source.Y, center.Y, radii.Y)) + Term(source.Z, center.Z, radii.Z);
        var threshold = EllipsoidRational.FromDouble(core);
        if (squared.Compare(threshold * threshold) <= 0) return new(EllipsoidMembership.Core, 1, 0, 0);
        if (squared.Compare(EllipsoidRational.One) >= 0) return new(EllipsoidMembership.Pinned, 0, 1, 1);
        var radiusValue = squared.SqrtNearest();
        var parameter = (radiusValue - core) / (1 - (double)core);
        var complement = 1 - parameter;
        var weight = (complement * complement) * (1 + (2 * parameter));
        if (!double.IsFinite(parameter) || parameter is < 0 or > 1 || !double.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentException("Reconstructed transition is not representable.");
        return new(EllipsoidMembership.Transition, weight, parameter, radiusValue);
    }

    private double[] AxisFactors(State state)
    {
        double[] result = [scale.X, scale.Y, scale.Z];
        if (state.Region == EllipsoidMembership.Transition)
            for (var axis = 0; axis < 3; axis++) result[axis] = 1 + ((result[axis] - 1) * state.Weight);
        return result;
    }
    private static double Product(double[] first, double[] second) => ((first[0] * second[0]) + (first[1] * second[1])) + (first[2] * second[2]);
    private static float Store(double value) => float.IsFinite((float)value) ? (float)value : throw new ArgumentException("Reconstructed word is nonfinite.");
    private static void Unit(double[] components)
    {
        var length = Math.Sqrt(Product(components, components));
        if (!double.IsFinite(length) || length <= 0) throw new ArgumentException("Reconstructed frame is degenerate.");
        for (var axis = 0; axis < 3; axis++) components[axis] /= length;
    }
    private static void Validate(TangentFrame frame)
    {
        double[] normal = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
        double[] tangent = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
        if (frame.Handedness is not (-1f or 1f) || !normal.Concat(tangent).All(double.IsFinite)
            || Math.Abs(Product(normal, normal) - 1) > 1e-4 || Math.Abs(Product(tangent, tangent) - 1) > 1e-4 || Math.Abs(Product(normal, tangent)) > 1e-4)
            throw new ArgumentException("Reconstructed frame must be finite, unit and orthogonal.");
    }
}

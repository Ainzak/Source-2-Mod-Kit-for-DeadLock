namespace S2ModKit.Geometry;

public sealed record DirectionalFieldParameters(Point3 Center, Point3 OuterRadii,
    float CoreFraction, Point3 Scale, float DisplacementLimit);

/// <summary>Independent read-side pair dispatch, with no calls to the pair deformation evaluator.</summary>
public sealed class PairedDirectionalFieldReconstruction
{
    private readonly DirectionalFieldReconstruction first;
    private readonly DirectionalFieldReconstruction second;

    public PairedDirectionalFieldReconstruction(DirectionalFieldParameters first, DirectionalFieldParameters second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        this.first = new(first.Center, first.OuterRadii, first.CoreFraction, first.Scale, first.DisplacementLimit);
        this.second = new(second.Center, second.OuterRadii, second.CoreFraction, second.Scale, second.DisplacementLimit);
        // Reviewed exact-rational primitives may be shared; pair separation/dispatch is rediscovered.
        float[] centersA = [first.Center.X, first.Center.Y, first.Center.Z];
        float[] centersB = [second.Center.X, second.Center.Y, second.Center.Z];
        float[] radiiA = [first.OuterRadii.X, first.OuterRadii.Y, first.OuterRadii.Z];
        float[] radiiB = [second.OuterRadii.X, second.OuterRadii.Y, second.OuterRadii.Z];
        var separated = false;
        for (var axis = 0; axis < 3; axis++)
        {
            var difference = EllipsoidRational.FromDouble(centersA[axis]) - EllipsoidRational.FromDouble(centersB[axis]);
            var width = EllipsoidRational.FromDouble(radiiA[axis]) + EllipsoidRational.FromDouble(radiiB[axis]);
            if (new EllipsoidRational(System.Numerics.BigInteger.Abs(difference.Numerator), difference.Denominator).Compare(width) >= 0)
                separated = true;
        }
        if (!separated) throw new ArgumentException("Reconstructed pair has no exact separating projection.", nameof(second));
    }

    public PairedDirectionalPointResult ReconstructPosition(Point3 source)
    {
        var a = first.ReconstructPosition(source);
        var b = second.ReconstructPosition(source);
        if (a.Membership != EllipsoidMembership.Pinned)
        {
            if (b.Membership != EllipsoidMembership.Pinned) throw new ArgumentException("Reconstructed interiors overlap.");
            return new(a.Position, 0, a.Membership, a.Weight, a.MaximumDisplacement);
        }
        return new(b.Position, b.Membership == EllipsoidMembership.Pinned ? null : 1,
            b.Membership, b.Weight, b.MaximumDisplacement);
    }

    public TangentFrame ReconstructFrame(Point3 source, TangentFrame frame) => ReconstructPosition(source).ActiveFieldIndex == 1
        ? second.ReconstructFrame(source, frame) : first.ReconstructFrame(source, frame);
}

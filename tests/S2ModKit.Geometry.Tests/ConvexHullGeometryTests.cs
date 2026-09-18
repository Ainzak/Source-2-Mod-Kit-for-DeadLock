using S2ModKit.Geometry;

namespace S2ModKit.Geometry.Tests;

public sealed class ConvexHullGeometryTests
{
    [Fact]
    public void DeriveReproducesIndependentUnitTetrahedron()
    {
        var result = ConvexHullGeometry.Derive(Tetrahedron(), TetrahedronFaces());

        Assert.Equal(new Bounds3(new Point3(0f, 0f, 0f), new Point3(1f, 1f, 1f)), result.Bounds);
        Assert.Equal(new Point3(0.25f, 0.25f, 0.25f), result.VertexCentroid);
        AssertClose(0.16666667f, result.Volume);
        AssertClose(2.3660254f, result.SurfaceArea);
        AssertClose(0.16666667f, result.MassProperties.Mass);
        Assert.Equal(new Point3(0.25f, 0.25f, 0.25f), result.MassProperties.CenterOfMass);
        AssertClose(0.0125f, result.MassProperties.Inertia.XX);
        AssertClose(1f / 480f, result.MassProperties.Inertia.XY);
        AssertClose(0.0125f, result.MassProperties.Inertia.ZZ);
        Assert.Equal(4, result.FacePlanes.Length);
        Assert.All(result.FacePlanes, plane => AssertClose(1f, Length(plane.Normal)));
    }

    [Fact]
    public void DeriveReproducesIndependentRectangularBox()
    {
        var result = ConvexHullGeometry.Derive(Box(), BoxFaces());

        Assert.Equal(new Bounds3(new Point3(-1f, -2f, -3f), new Point3(1f, 2f, 3f)), result.Bounds);
        Assert.Equal(new Point3(0f, 0f, 0f), result.VertexCentroid);
        AssertClose((float)Math.Sqrt(14d), result.MaximumAngularRadius);
        AssertClose(48f, result.Volume);
        AssertClose(88f, result.SurfaceArea);
        Assert.Equal(new Point3(0f, 0f, 0f), result.MassProperties.CenterOfMass);
        AssertClose(208f, result.MassProperties.Inertia.XX);
        AssertClose(160f, result.MassProperties.Inertia.YY);
        AssertClose(80f, result.MassProperties.Inertia.ZZ);
        AssertClose(0f, result.MassProperties.Inertia.XY);
    }

    [Fact]
    public void TransformRecomputesGeometryAndCrossChecksScaleLaws()
    {
        var transform = new UniformTransform(new Point3(1f, 0f, 0f), 2f, new Point3(0f, 0f, 0f));
        var regionPlanes = new[]
        {
            new Plane3(new Point3(1f, 0f, 0f), 1f),
            new Plane3(new Point3(0f, 1f, 0f), 2f),
        };

        var result = ConvexHullGeometry.Transform(Box(), BoxFaces(), regionPlanes, transform);

        Assert.Equal(new Bounds3(new Point3(-3f, -4f, -6f), new Point3(1f, 4f, 6f)), result.After.Bounds);
        Assert.Equal(new Point3(-1f, 0f, 0f), result.After.VertexCentroid);
        Assert.Equal(new Point3(-1f, 0f, 0f), result.After.MassProperties.CenterOfMass);
        AssertClose(384f, result.After.Volume);
        AssertClose(352f, result.After.SurfaceArea);
        AssertClose(6656f, result.After.MassProperties.Inertia.XX);
        AssertClose(5120f, result.After.MassProperties.Inertia.YY);
        AssertClose(2560f, result.After.MassProperties.Inertia.ZZ);
        Assert.Equal(new Plane3(new Point3(1f, 0f, 0f), 1f), result.RegionPlanes[0]);
        Assert.Equal(new Plane3(new Point3(0f, 1f, 0f), 4f), result.RegionPlanes[1]);
        AssertClose((float)Math.Sqrt(17d), result.PositionSummary.MaximumDisplacement);
    }

    [Fact]
    public void TransformIsDeterministicAndDoesNotMutateInputs()
    {
        var positions = Box();
        var snapshot = positions.ToArray();
        var faces = BoxFaces();
        var transform = new UniformTransform(new Point3(0.5f, -1f, 2f), 0.5f, new Point3(0f, 0f, 0f));

        var first = ConvexHullGeometry.Transform(positions, faces, [], transform);
        var second = ConvexHullGeometry.Transform(positions, faces, [], transform);

        Assert.Equal(snapshot, positions);
        Assert.Equal(first.Positions.ToArray(), second.Positions.ToArray());
        Assert.Equal(first.PositionSummary, second.PositionSummary);
        Assert.Equal(first.Before.Bounds, second.Before.Bounds);
        Assert.Equal(first.After.Bounds, second.After.Bounds);
        Assert.Equal(first.After.MassProperties, second.After.MassProperties);
        Assert.Equal(first.After.FacePlanes.ToArray(), second.After.FacePlanes.ToArray());
    }

    [Fact]
    public void DeriveAcceptsReversedFaceWindingByNormalizingOutwardPlanes()
    {
        var reversed = TetrahedronFaces()
            .Select(face => new ConvexFace(face.VertexIndices.Reverse()))
            .ToArray();

        var result = ConvexHullGeometry.Derive(Tetrahedron(), reversed);

        AssertClose(1f / 6f, result.Volume);
        AssertClose(2.3660254f, result.SurfaceArea);
    }

    [Fact]
    public void DeriveRejectsInvalidOrDegenerateFaces()
    {
        var positions = Tetrahedron();
        Assert.Throws<ArgumentException>(() => ConvexHullGeometry.Derive(
            positions,
            [new ConvexFace([0, 1, 4]), .. TetrahedronFaces().Skip(1)]));
        Assert.Throws<InvalidDataException>(() => ConvexHullGeometry.Derive(
            positions,
            [new ConvexFace([0, 1, 2, 3]), .. TetrahedronFaces().Skip(1)]));
    }

    [Fact]
    public void DeriveFailsClosedWhenResultsExceedFloatRange()
    {
        var maximum = float.MaxValue;
        var positions = new[]
        {
            new Point3(0f, 0f, 0f),
            new Point3(maximum, 0f, 0f),
            new Point3(0f, maximum, 0f),
            new Point3(0f, 0f, maximum),
        };

        Assert.Throws<OverflowException>(() => ConvexHullGeometry.Derive(positions, TetrahedronFaces()));
    }

    private static Point3[] Tetrahedron() =>
    [
        new Point3(0f, 0f, 0f),
        new Point3(1f, 0f, 0f),
        new Point3(0f, 1f, 0f),
        new Point3(0f, 0f, 1f),
    ];

    private static ConvexFace[] TetrahedronFaces() =>
    [
        new ConvexFace([0, 2, 1]),
        new ConvexFace([0, 1, 3]),
        new ConvexFace([0, 3, 2]),
        new ConvexFace([1, 2, 3]),
    ];

    private static Point3[] Box() =>
    [
        new Point3(-1f, -2f, -3f),
        new Point3(1f, -2f, -3f),
        new Point3(1f, 2f, -3f),
        new Point3(-1f, 2f, -3f),
        new Point3(-1f, -2f, 3f),
        new Point3(1f, -2f, 3f),
        new Point3(1f, 2f, 3f),
        new Point3(-1f, 2f, 3f),
    ];

    private static ConvexFace[] BoxFaces() =>
    [
        new ConvexFace([0, 3, 2, 1]),
        new ConvexFace([4, 5, 6, 7]),
        new ConvexFace([0, 1, 5, 4]),
        new ConvexFace([1, 2, 6, 5]),
        new ConvexFace([2, 3, 7, 6]),
        new ConvexFace([3, 0, 4, 7]),
    ];

    private static float Length(Point3 value) =>
        MathF.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - (1e-4f * MathF.Max(1f, MathF.Abs(expected))), expected + (1e-4f * MathF.Max(1f, MathF.Abs(expected))));
}

using System.Collections.Immutable;

namespace S2ModKit.Geometry;

/// <summary>A unit plane stored as <c>normal dot point - offset = 0</c>.</summary>
public readonly record struct Plane3
{
    public Plane3(Point3 normal, float offset)
    {
        ScalarValidation.RequireFinite(offset, nameof(offset));
        Normal = normal;
        Offset = offset;
    }

    public Point3 Normal { get; }

    public float Offset { get; }
}

/// <summary>One ordered polygonal face in a closed convex hull.</summary>
public sealed record ConvexFace
{
    public ConvexFace(IEnumerable<int> vertexIndices)
    {
        ArgumentNullException.ThrowIfNull(vertexIndices);
        VertexIndices = [.. vertexIndices];
        if (VertexIndices.Length < 3)
        {
            throw new ArgumentException("A convex face requires at least three vertices.", nameof(vertexIndices));
        }
    }

    public ImmutableArray<int> VertexIndices { get; }
}

/// <summary>A symmetric inertia tensor about the center of mass.</summary>
public readonly record struct SymmetricInertia3(
    float XX,
    float XY,
    float XZ,
    float YY,
    float YZ,
    float ZZ);

/// <summary>Unit-density mass properties for a closed convex hull.</summary>
public sealed record ConvexMassProperties(
    float Mass,
    Point3 CenterOfMass,
    SymmetricInertia3 Inertia);

/// <summary>Values independently derived from convex positions and face topology.</summary>
public sealed record ConvexHullDerivedValues(
    Bounds3 Bounds,
    Point3 VertexCentroid,
    float MaximumAngularRadius,
    float Volume,
    float SurfaceArea,
    ConvexMassProperties MassProperties,
    ImmutableArray<Plane3> FacePlanes);

/// <summary>The pure result of uniformly transforming a convex hull and optional region planes.</summary>
public sealed record ConvexHullTransformResult(
    ImmutableArray<Point3> Positions,
    TransformSummary PositionSummary,
    ConvexHullDerivedValues Before,
    ConvexHullDerivedValues After,
    ImmutableArray<Plane3> RegionPlanes);

/// <summary>Pure deterministic calculations for a closed convex hull.</summary>
public static class ConvexHullGeometry
{
    private const double RelativeGeometryTolerance = 1e-5;
    private const double RelativeReproductionTolerance = 2e-4;

    public static ConvexHullDerivedValues Derive(
        IReadOnlyList<Point3> positions,
        IReadOnlyList<ConvexFace> faces)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(faces);
        if (positions.Count < 4)
        {
            throw new ArgumentException("A three-dimensional convex hull requires at least four positions.", nameof(positions));
        }

        if (faces.Count < 4)
        {
            throw new ArgumentException("A three-dimensional convex hull requires at least four faces.", nameof(faces));
        }

        var bounds = Bounds3.FromPoints(positions);
        var extent = Math.Max(
            Math.Max((double)bounds.Max.X - bounds.Min.X, (double)bounds.Max.Y - bounds.Min.Y),
            (double)bounds.Max.Z - bounds.Min.Z);
        if (!(extent > 0d) || !double.IsFinite(extent))
        {
            throw new InvalidDataException("Convex hull extent must be finite and positive.");
        }

        var planeTolerance = RelativeGeometryTolerance * Math.Max(1d, extent);
        var areaTolerance = RelativeGeometryTolerance * Math.Max(1d, extent * extent);
        var facePlanes = ImmutableArray.CreateBuilder<Plane3>(faces.Count);
        double surfaceArea = 0d;
        double volume = 0d;
        double firstX = 0d;
        double firstY = 0d;
        double firstZ = 0d;
        var second = new double[3, 3];

        for (var faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            var face = faces[faceIndex] ?? throw new ArgumentException($"Face {faceIndex} is null.", nameof(faces));
            ValidateFaceIndices(face, positions.Count, faceIndex);
            var first = positions[face.VertexIndices[0]];
            var normal = PolygonNormal(positions, face.VertexIndices, faceIndex);
            var offset = Dot(normal, first);
            var maximumSignedDistance = double.NegativeInfinity;
            var minimumSignedDistance = double.PositiveInfinity;
            foreach (var point in positions)
            {
                var signedDistance = Dot(normal, point) - offset;
                maximumSignedDistance = Math.Max(maximumSignedDistance, signedDistance);
                minimumSignedDistance = Math.Min(minimumSignedDistance, signedDistance);
            }

            if (maximumSignedDistance > planeTolerance && minimumSignedDistance < -planeTolerance)
            {
                throw new InvalidDataException($"Face {faceIndex} does not define a supporting hull plane.");
            }

            if (maximumSignedDistance > planeTolerance)
            {
                normal = (-normal.X, -normal.Y, -normal.Z);
                offset = -offset;
            }

            foreach (var vertexIndex in face.VertexIndices)
            {
                if (Math.Abs(Dot(normal, positions[vertexIndex]) - offset) > planeTolerance)
                {
                    throw new InvalidDataException($"Face {faceIndex} is not planar within the accepted tolerance.");
                }
            }

            double faceArea = 0d;
            for (var triangle = 1; triangle < face.VertexIndices.Length - 1; triangle++)
            {
                var a = first;
                var b = positions[face.VertexIndices[triangle]];
                var c = positions[face.VertexIndices[triangle + 1]];
                var cross = Cross(Subtract(b, a), Subtract(c, a));
                var twiceArea = Length(cross);
                if (!(twiceArea > areaTolerance) || !double.IsFinite(twiceArea))
                {
                    throw new InvalidDataException($"Face {faceIndex} contains a degenerate triangle.");
                }

                if (Dot(cross, normal) < 0d)
                {
                    (b, c) = (c, b);
                }

                faceArea += twiceArea * 0.5d;
                AccumulateTetrahedron(a, b, c, ref volume, ref firstX, ref firstY, ref firstZ, second);
            }

            surfaceArea += faceArea;
            facePlanes.Add(new Plane3(
                new Point3(ToFiniteSingle(normal.X, "face normal X"), ToFiniteSingle(normal.Y, "face normal Y"), ToFiniteSingle(normal.Z, "face normal Z")),
                ToFiniteSingle(offset, "face plane offset")));
        }

        var minimumVolume = RelativeGeometryTolerance * Math.Max(1d, extent * extent * extent);
        if (!(volume > minimumVolume) || !double.IsFinite(volume))
        {
            throw new InvalidDataException("Convex hull volume is degenerate, negative, or non-finite.");
        }

        var centerOfMass = (X: firstX / volume, Y: firstY / volume, Z: firstZ / volume);
        var inertiaOriginXX = second[1, 1] + second[2, 2];
        var inertiaOriginYY = second[0, 0] + second[2, 2];
        var inertiaOriginZZ = second[0, 0] + second[1, 1];
        var inertiaOriginXY = -second[0, 1];
        var inertiaOriginXZ = -second[0, 2];
        var inertiaOriginYZ = -second[1, 2];
        var inertia = new SymmetricInertia3(
            ToFiniteSingle(inertiaOriginXX - (volume * ((centerOfMass.Y * centerOfMass.Y) + (centerOfMass.Z * centerOfMass.Z))), "inertia XX"),
            ToFiniteSingle(inertiaOriginXY + (volume * centerOfMass.X * centerOfMass.Y), "inertia XY"),
            ToFiniteSingle(inertiaOriginXZ + (volume * centerOfMass.X * centerOfMass.Z), "inertia XZ"),
            ToFiniteSingle(inertiaOriginYY - (volume * ((centerOfMass.X * centerOfMass.X) + (centerOfMass.Z * centerOfMass.Z))), "inertia YY"),
            ToFiniteSingle(inertiaOriginYZ + (volume * centerOfMass.Y * centerOfMass.Z), "inertia YZ"),
            ToFiniteSingle(inertiaOriginZZ - (volume * ((centerOfMass.X * centerOfMass.X) + (centerOfMass.Y * centerOfMass.Y))), "inertia ZZ"));

        var vertexCentroid = Mean(positions);
        var maximumAngularRadius = MaximumDistance(positions, vertexCentroid);
        return new ConvexHullDerivedValues(
            bounds,
            vertexCentroid,
            maximumAngularRadius,
            ToFiniteSingle(volume, "volume"),
            ToFiniteSingle(surfaceArea, "surface area"),
            new ConvexMassProperties(
                ToFiniteSingle(volume, "unit-density mass"),
                new Point3(
                    ToFiniteSingle(centerOfMass.X, "center of mass X"),
                    ToFiniteSingle(centerOfMass.Y, "center of mass Y"),
                    ToFiniteSingle(centerOfMass.Z, "center of mass Z")),
                inertia),
            facePlanes.MoveToImmutable());
    }

    public static ConvexHullTransformResult Transform(
        IReadOnlyList<Point3> positions,
        IReadOnlyList<ConvexFace> faces,
        IReadOnlyList<Plane3> regionPlanes,
        UniformTransform transform)
    {
        ArgumentNullException.ThrowIfNull(regionPlanes);
        ArgumentNullException.ThrowIfNull(transform);
        var before = Derive(positions, faces);
        var transformedPositions = new Point3[positions.Count];
        var summary = transform.ApplyAndSummarize(positions, transformedPositions);
        var after = Derive(transformedPositions, faces);
        var transformedRegionPlanes = regionPlanes.Select(plane => TransformPlane(plane, transform)).ToImmutableArray();

        RequireReproduced(after.VertexCentroid, transform.Apply(before.VertexCentroid), "vertex centroid");
        RequireReproduced(after.MaximumAngularRadius, before.MaximumAngularRadius * transform.Scale, "maximum angular radius");
        RequireReproduced(after.Volume, before.Volume * Cube(transform.Scale), "volume");
        RequireReproduced(after.SurfaceArea, before.SurfaceArea * Square(transform.Scale), "surface area");
        RequireReproduced(after.MassProperties.CenterOfMass, transform.Apply(before.MassProperties.CenterOfMass), "center of mass");
        RequireReproduced(after.MassProperties.Mass, before.MassProperties.Mass * Cube(transform.Scale), "mass");
        var inertiaScale = FifthPower(transform.Scale);
        RequireReproduced(after.MassProperties.Inertia, Scale(before.MassProperties.Inertia, inertiaScale), "inertia");
        for (var index = 0; index < before.FacePlanes.Length; index++)
        {
            RequireReproduced(after.FacePlanes[index], TransformPlane(before.FacePlanes[index], transform), $"face plane {index}");
        }

        return new ConvexHullTransformResult(
            [.. transformedPositions],
            summary,
            before,
            after,
            transformedRegionPlanes);
    }

    public static Plane3 TransformPlane(Plane3 plane, UniformTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var pivotDotNormal = Dot(ToDouble(plane.Normal), transform.Pivot);
        var translationDotNormal = Dot(ToDouble(plane.Normal), transform.Translation);
        var offset = ((double)transform.Scale * plane.Offset)
            + ((1d - transform.Scale) * pivotDotNormal)
            + translationDotNormal;
        return new Plane3(plane.Normal, ToFiniteSingle(offset, "transformed plane offset"));
    }

    private static void ValidateFaceIndices(ConvexFace face, int positionCount, int faceIndex)
    {
        var seen = new HashSet<int>();
        foreach (var vertexIndex in face.VertexIndices)
        {
            if ((uint)vertexIndex >= (uint)positionCount || !seen.Add(vertexIndex))
            {
                throw new ArgumentException($"Face {faceIndex} contains an out-of-range or repeated vertex index.", nameof(face));
            }
        }
    }

    private static (double X, double Y, double Z) PolygonNormal(
        IReadOnlyList<Point3> positions,
        ImmutableArray<int> indices,
        int faceIndex)
    {
        double x = 0d;
        double y = 0d;
        double z = 0d;
        for (var index = 0; index < indices.Length; index++)
        {
            var current = positions[indices[index]];
            var next = positions[indices[(index + 1) % indices.Length]];
            x += ((double)current.Y - next.Y) * ((double)current.Z + next.Z);
            y += ((double)current.Z - next.Z) * ((double)current.X + next.X);
            z += ((double)current.X - next.X) * ((double)current.Y + next.Y);
        }

        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        if (!(length > 0d) || !double.IsFinite(length))
        {
            throw new InvalidDataException($"Face {faceIndex} has no finite normal.");
        }

        return (x / length, y / length, z / length);
    }

    private static void AccumulateTetrahedron(
        Point3 a,
        Point3 b,
        Point3 c,
        ref double volume,
        ref double firstX,
        ref double firstY,
        ref double firstZ,
        double[,] second)
    {
        var signedVolume = Dot(ToDouble(a), Cross(ToDouble(b), ToDouble(c))) / 6d;
        volume += signedVolume;
        firstX += signedVolume * ((double)a.X + b.X + c.X) / 4d;
        firstY += signedVolume * ((double)a.Y + b.Y + c.Y) / 4d;
        firstZ += signedVolume * ((double)a.Z + b.Z + c.Z) / 4d;
        var vertices = new[] { ToDouble(a), ToDouble(b), ToDouble(c) };
        var sums = new[]
        {
            (double)a.X + b.X + c.X,
            (double)a.Y + b.Y + c.Y,
            (double)a.Z + b.Z + c.Z,
        };
        for (var row = 0; row < 3; row++)
        {
            for (var column = row; column < 3; column++)
            {
                var diagonalSum = vertices.Sum(vertex => Component(vertex, row) * Component(vertex, column));
                var integral = signedVolume * ((sums[row] * sums[column]) + diagonalSum) / 20d;
                second[row, column] += integral;
                second[column, row] = second[row, column];
            }
        }
    }

    private static Point3 Mean(IReadOnlyList<Point3> positions)
    {
        double x = 0d;
        double y = 0d;
        double z = 0d;
        foreach (var point in positions)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }

        return new Point3(
            ToFiniteSingle(x / positions.Count, "vertex centroid X"),
            ToFiniteSingle(y / positions.Count, "vertex centroid Y"),
            ToFiniteSingle(z / positions.Count, "vertex centroid Z"));
    }

    private static float MaximumDistance(IReadOnlyList<Point3> positions, Point3 center)
    {
        double maximumSquared = 0d;
        foreach (var point in positions)
        {
            var x = (double)point.X - center.X;
            var y = (double)point.Y - center.Y;
            var z = (double)point.Z - center.Z;
            maximumSquared = Math.Max(maximumSquared, (x * x) + (y * y) + (z * z));
        }

        return ToFiniteSingle(Math.Sqrt(maximumSquared), "maximum angular radius");
    }

    private static void RequireReproduced(float actual, float expected, string field)
    {
        var difference = Math.Abs((double)actual - expected);
        var tolerance = RelativeReproductionTolerance * Math.Max(1d, Math.Abs(expected));
        if (!float.IsFinite(expected) || difference > tolerance)
        {
            throw new InvalidDataException($"Uniform transform did not reproduce {field} within tolerance.");
        }
    }

    private static void RequireReproduced(Point3 actual, Point3 expected, string field)
    {
        RequireReproduced(actual.X, expected.X, $"{field} X");
        RequireReproduced(actual.Y, expected.Y, $"{field} Y");
        RequireReproduced(actual.Z, expected.Z, $"{field} Z");
    }

    private static void RequireReproduced(Plane3 actual, Plane3 expected, string field)
    {
        RequireReproduced(actual.Normal, expected.Normal, $"{field} normal");
        RequireReproduced(actual.Offset, expected.Offset, $"{field} offset");
    }

    private static void RequireReproduced(SymmetricInertia3 actual, SymmetricInertia3 expected, string field)
    {
        RequireReproduced(actual.XX, expected.XX, $"{field} XX");
        RequireReproduced(actual.XY, expected.XY, $"{field} XY");
        RequireReproduced(actual.XZ, expected.XZ, $"{field} XZ");
        RequireReproduced(actual.YY, expected.YY, $"{field} YY");
        RequireReproduced(actual.YZ, expected.YZ, $"{field} YZ");
        RequireReproduced(actual.ZZ, expected.ZZ, $"{field} ZZ");
    }

    private static SymmetricInertia3 Scale(SymmetricInertia3 value, float scale) => new(
        value.XX * scale,
        value.XY * scale,
        value.XZ * scale,
        value.YY * scale,
        value.YZ * scale,
        value.ZZ * scale);

    private static float Square(float value) => value * value;

    private static float Cube(float value) => value * value * value;

    private static float FifthPower(float value) => Cube(value) * Square(value);

    private static (double X, double Y, double Z) ToDouble(Point3 point) => (point.X, point.Y, point.Z);

    private static (double X, double Y, double Z) Subtract(Point3 left, Point3 right) =>
        ((double)left.X - right.X, (double)left.Y - right.Y, (double)left.Z - right.Z);

    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        ((left.Y * right.Z) - (left.Z * right.Y),
         (left.Z * right.X) - (left.X * right.Z),
         (left.X * right.Y) - (left.Y * right.X));

    private static double Dot((double X, double Y, double Z) left, Point3 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static double Dot(
        (double X, double Y, double Z) left,
        (double X, double Y, double Z) right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static double Length((double X, double Y, double Z) value) =>
        Math.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));

    private static double Component((double X, double Y, double Z) value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private static float ToFiniteSingle(double value, string field)
    {
        var result = (float)value;
        if (!double.IsFinite(value) || !float.IsFinite(result))
        {
            throw new OverflowException($"Convex {field} is not representable as a finite float.");
        }

        return result;
    }
}

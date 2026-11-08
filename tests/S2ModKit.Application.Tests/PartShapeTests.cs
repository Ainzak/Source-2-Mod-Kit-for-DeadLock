using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Geometry;

namespace S2ModKit.Application.Tests;

public sealed class PartShapeTests
{
    private static readonly Vector3 Min = new(-1, -2, 0);
    private static readonly Vector3 Max = new(1, 2, 10);

    private static Vector3 Apply(PartShape shape, Vector3 point) => Vector3.Transform(point, shape.Matrix(Min, Max));

    [Fact]
    public void TheIdentityShapeChangesNothing()
    {
        Assert.True(PartShape.Identity.IsIdentity);
        Assert.Equal(new Vector3(1, 2, 3), Apply(PartShape.Identity, new Vector3(1, 2, 3)));
    }

    [Fact]
    public void ScaleKeepsThePivotInPlace()
    {
        var bigger = new PartShape(new Vector3(2, 2, 2), Vector3.Zero, Vector3.Zero);
        Assert.Equal(new Vector3(0, 0, 5), Apply(bigger, new Vector3(0, 0, 5)));                    // the box centre stays
        Assert.Equal(new Vector3(0, 0, 15), Apply(bigger, new Vector3(0, 0, 10)));                  // the top moves out

        var fromBase = bigger with { Pivot = ShapePivot.Base };
        Assert.Equal(new Vector3(0, 0, 0), Apply(fromBase, new Vector3(0, 0, 0)));                  // the base stays on the ground
        Assert.Equal(new Vector3(0, 0, 20), Apply(fromBase, new Vector3(0, 0, 10)));

        var fromTop = bigger with { Pivot = ShapePivot.Top };
        Assert.Equal(new Vector3(0, 0, 10), Apply(fromTop, new Vector3(0, 0, 10)));
    }

    [Fact]
    public void PerAxisScaleRotationAndMoveApplyInThatOrder()
    {
        var wider = new PartShape(new Vector3(1, 3, 1), Vector3.Zero, Vector3.Zero);
        Assert.Equal(new Vector3(0, 6, 5), Apply(wider, new Vector3(0, 2, 5)));                     // only Y (width) grows

        var turned = new PartShape(Vector3.One, new Vector3(0, 0, 90), Vector3.Zero);               // turn: about Z (up)
        var p = Apply(turned, new Vector3(1, 0, 5));
        Assert.Equal(0, p.X, 4);
        Assert.Equal(1, p.Y, 4);                                                                    // forward turns to left
        Assert.Equal(5, p.Z, 4);

        var all = new PartShape(new Vector3(2, 2, 2), new Vector3(0, 0, 90), new Vector3(0, 0, 3));
        var q = Apply(all, new Vector3(1, 0, 5));
        Assert.Equal(new Vector3(0, 2, 8), new Vector3(MathF.Round(q.X, 4), MathF.Round(q.Y, 4), MathF.Round(q.Z, 4)));
    }

    [Theory]
    [InlineData(1.4f, 1.4f, 1.4f, 0f, 0f, 25f, 0f, 0f, 4f, 0)]
    [InlineData(1f, 2.5f, 0.6f, 30f, -45f, 110f, 3f, -2f, 1f, 1)]
    [InlineData(0.5f, 0.5f, 3f, -170f, 10f, 0f, 0f, 7f, 0f, 2)]
    [InlineData(1.2f, 1f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0)]
    public void ThePreviewMatchesTheCliAffineTransformExactly(float sx, float sy, float sz, float roll, float tilt, float turn, float mx, float my, float mz, int pivot)
    {
        var shape = new PartShape(new Vector3(sx, sy, sz), new Vector3(roll, tilt, turn), new Vector3(mx, my, mz), (ShapePivot)pivot);
        var at = shape.PivotPoint(Min, Max);
        var rotation = shape.AxisAngle() is { } r ? new AxisAngleRotation(new Point3(r.Axis.X, r.Axis.Y, r.Axis.Z), r.Degrees) : AxisAngleRotation.Identity;
        var cli = new AffineTransform(new Point3(at.X, at.Y, at.Z), new AffineScale(sx, sy, sz), rotation, RigidFrame.Model, new Point3(mx, my, mz));
        foreach (var point in new[] { new Vector3(1, 2, 3), new Vector3(-1, -2, 0), new Vector3(0.5f, 1.5f, 9), new Vector3(1, -2, 10) })
        {
            var expected = cli.Apply(new Point3(point.X, point.Y, point.Z));
            var actual = Apply(shape, point);
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
            Assert.Equal(expected.Z, actual.Z, 3);
        }
    }
}

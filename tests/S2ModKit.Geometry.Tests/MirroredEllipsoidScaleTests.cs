namespace S2ModKit.Geometry.Tests;

public sealed class MirroredEllipsoidScaleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactReflectionAndTouchingBoundaryRetainWords(int axis)
    {
        var center = axis switch { 0 => new Point3(4, -0f, 0), 1 => new(0, 4, -0f), _ => new(0, -0f, 4) };
        var field = new MirroredEllipsoidScale(center, new(4, 4, 4), 1f / 16, 2, 64, axis, 0);
        var pinned = field.Evaluate(new(-0f, -0f, -0f));
        Assert.Equal(EllipsoidMembership.Pinned, pinned.Membership);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(pinned.Position.X));
        var random = new Random(12013 + axis);
        for (var i = 0; i < 4000; i++)
        {
            var p = new Point3((float)(random.NextDouble() * 16 - 8), (float)(random.NextDouble() * 16 - 8), (float)(random.NextDouble() * 16 - 8));
            var mate = axis switch { 0 => new Point3(-p.X, p.Y, p.Z), 1 => new(p.X, -p.Y, p.Z), _ => new(p.X, p.Y, -p.Z) };
            var first = field.Evaluate(p); var second = field.Evaluate(mate);
            Assert.Equal(first.Membership, second.Membership);
            Assert.Equal(first.Weight, second.Weight);
            var reflectedOutput = axis switch { 0 => new Point3(-first.Position.X, first.Position.Y, first.Position.Z), 1 => new(first.Position.X, -first.Position.Y, first.Position.Z), _ => new(first.Position.X, first.Position.Y, -first.Position.Z) };
            Assert.Equal(reflectedOutput, second.Position);
        }
    }

    [Fact]
    public void OverlapAndInexactSubnormalReflectionRejectBeforeEvaluation()
    {
        Assert.Equal("ELLIPSOID_MIRROR_OVERLAP", Assert.Throws<EllipsoidMirrorException>(() =>
            new MirroredEllipsoidScale(new(float.BitDecrement(4), 0, 0), new(4, 4, 4), 1f / 16, 2, 64, 0, 0)).Code);
        Assert.Equal("ELLIPSOID_REFLECTION_UNREPRESENTABLE", Assert.Throws<EllipsoidMirrorException>(() =>
            new MirroredEllipsoidScale(new(1, 0, 0), new(.5f, .5f, .5f), 1f / 16, 2, 64, 0, float.Epsilon)).Code);
        var zero = new MirroredEllipsoidScale(new(2, -0f, 0), new(1, 1, 1), 1f / 16, 2, 64, 0, 1);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(zero.Reflected.Center.X));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(zero.Reflected.Center.Y));
    }
}

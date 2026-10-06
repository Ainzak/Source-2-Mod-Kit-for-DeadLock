namespace S2ModKit.Geometry.Tests;

public sealed class DirectionalFieldReconstructionTests
{
    [Fact]
    public void ExactAndAdjacentBoundariesKeepMembershipSeparateFromRoundedWeights()
    {
        var reader = new DirectionalFieldReconstruction(default, new(8, 4, 2), 1f / 16, new(2, 1, 1), 64);
        Assert.Equal(EllipsoidMembership.Core, reader.ReconstructPosition(new(.5f, 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Transition, reader.ReconstructPosition(new(float.BitIncrement(.5f), 0, 0)).Membership);
        var endpoint = reader.ReconstructPosition(new(.5f, float.Epsilon, 0));
        Assert.Equal(EllipsoidMembership.Transition, endpoint.Membership); Assert.Equal(1, endpoint.Weight);
        Assert.Equal(EllipsoidMembership.Transition, reader.ReconstructPosition(new(float.BitDecrement(8), 0, 0)).Membership);
        Assert.Equal(EllipsoidMembership.Pinned, reader.ReconstructPosition(new(8, 0, 0)).Membership);
    }

    [Fact]
    public void ReconstructionRetainsSignedZeroAndTransportsAnisotropicPivotFrames()
    {
        var reader = new DirectionalFieldReconstruction(default, new(10, 8, 4), .4f, new(1.5f, 1.25f, 1), 64);
        var negative = BitConverter.UInt32BitsToSingle(0x80000000);
        Assert.Equal(0x80000000U, BitConverter.SingleToUInt32Bits(reader.ReconstructPosition(new(1, 1, negative)).Position.Z));
        var frame = new TangentFrame(new Point3(1, 1, 1) * (1 / MathF.Sqrt(3)), new Point3(1, -1, 0) * (1 / MathF.Sqrt(2)), -1);
        var output = reader.ReconstructFrame(default, frame);
        Assert.NotEqual(frame, output); Assert.Equal(-1, output.Handedness);
        Assert.Equal(default, reader.ReconstructPosition(default).Position);
        Assert.Same(frame, reader.ReconstructFrame(new(10, 0, 0), frame));
    }

    [Fact]
    public void ReconstructionRejectsUnrepresentableFieldsFramesAndStoredDisplacement()
    {
        Assert.ThrowsAny<ArgumentException>(() => new DirectionalFieldReconstruction(default, new(2, 2, 2), .25f, new(2, 1, 1), 64));
        var reader = new DirectionalFieldReconstruction(default, new(10, 8, 4), .4f, new(1.5f, 1.25f, 1), .1f);
        Assert.ThrowsAny<ArgumentException>(() => reader.ReconstructPosition(new(1, 0, 0)));
        Assert.ThrowsAny<ArgumentException>(() => reader.ReconstructFrame(default, new(default, new(1, 0, 0), 1)));
    }
}

using System.Buffers.Binary;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class Source2CoupledRewriteComposerTests
{
    [Fact]
    public void ComposesExactlyThreeTargetsAndPreservesUnrelatedBlocks()
    {
        var bytes = ResourceBytes();
        var immutableInput = bytes.ToArray();
        var envelope = ResourceEnvelopeReader.Read(bytes);
        var plan = Plan(envelope);
        var verified = false;

        var candidate = Source2CoupledRewriteComposer.Compose(
            envelope,
            plan,
            () => new Dictionary<int, ReadOnlyMemory<byte>>
            {
                [1] = "mdat-after"u8.ToArray(),
                [2] = "mbuf-after"u8.ToArray(),
            },
            () => "phys-after"u8.ToArray(),
            content =>
            {
                var reopened = ResourceEnvelopeReader.Read(content);
                Assert.Equal("mdat-after"u8.ToArray(), reopened.Blocks[1].Payload.ToArray());
                Assert.Equal("mbuf-after"u8.ToArray(), reopened.Blocks[2].Payload.ToArray());
                Assert.Equal("phys-after"u8.ToArray(), reopened.Blocks[3].Payload.ToArray());
                verified = true;
            });

        var result = ResourceEnvelopeReader.Read(candidate);
        Assert.True(verified);
        Assert.Equal(envelope.Blocks[0].Payload.ToArray(), result.Blocks[0].Payload.ToArray());
        Assert.Equal(envelope.Blocks[4].Payload.ToArray(), result.Blocks[4].Payload.ToArray());
        Assert.True(bytes.AsSpan().SequenceEqual(immutableInput));
    }

    [Theory]
    [InlineData((int)CoupledRewriteFailurePoint.AfterVisualRewrite)]
    [InlineData((int)CoupledRewriteFailurePoint.AfterCollisionRewrite)]
    [InlineData((int)CoupledRewriteFailurePoint.AfterEnvelopeRebuild)]
    [InlineData((int)CoupledRewriteFailurePoint.AfterReopenVerification)]
    public void InjectedFailureNeverReturnsAPublishableCandidate(int failurePointValue)
    {
        var failurePoint = (CoupledRewriteFailurePoint)failurePointValue;
        var bytes = ResourceBytes();
        var immutableInput = bytes.ToArray();
        var envelope = ResourceEnvelopeReader.Read(bytes);
        var published = false;

        var exception = Assert.Throws<S2ModKitException>(() =>
        {
            _ = Source2CoupledRewriteComposer.Compose(
                envelope,
                Plan(envelope),
                () => new Dictionary<int, ReadOnlyMemory<byte>>
                {
                    [1] = "mdat-after"u8.ToArray(),
                    [2] = "mbuf-after"u8.ToArray(),
                },
                () => "phys-after"u8.ToArray(),
                content => _ = ResourceEnvelopeReader.Read(content),
                failurePoint);
            published = true;
        });

        Assert.Equal("COUPLED_TRANSFORM_INCOMPLETE", exception.Error.Code);
        Assert.False(published);
        Assert.True(bytes.AsSpan().SequenceEqual(immutableInput));
    }

    [Fact]
    public void RejectsIncompleteVisualReplacementSetBeforeCollisionRewrite()
    {
        var envelope = ResourceEnvelopeReader.Read(ResourceBytes());
        var collisionCalled = false;

        var exception = Assert.Throws<S2ModKitException>(() => Source2CoupledRewriteComposer.Compose(
            envelope,
            Plan(envelope),
            () => new Dictionary<int, ReadOnlyMemory<byte>> { [2] = "mbuf-after"u8.ToArray() },
            () =>
            {
                collisionCalled = true;
                return "phys-after"u8.ToArray();
            },
            _ => { }));

        Assert.Equal("COUPLED_TRANSFORM_INCOMPLETE", exception.Error.Code);
        Assert.False(collisionCalled);
    }

    private static PlannedCoupledTransformTarget Plan(Source2ResourceEnvelope envelope)
    {
        var zero = new TransformVector3();
        var bounds = new GeometryBounds(zero, zero);
        var visual = new PlannedRawMbufTransformTarget(
            1,
            2,
            ContentHash.Compute(envelope.Blocks[1].Payload.Span),
            ContentHash.Compute(envelope.Blocks[2].Payload.Span),
            ContentHash.Compute("mbuf-after"u8),
            ContentHash.Compute([1]),
            ContentHash.Compute([2]),
            ContentHash.Compute([3]),
            ContentHash.Compute([4]),
            "draw-call",
            "materials/example.vmat",
            "root",
            1,
            1,
            new PositionLayout("R32G32B32_FLOAT", 0, 24),
            bounds,
            bounds,
            zero,
            2f,
            1f,
            2f,
            [],
            []);
        var derived = new PlannedConvexDerivedValues(bounds, zero, 1f, 1f, 1f, zero, []);
        var collision = new PlannedConvexPhysTransformTarget(
            3,
            ContentHash.Compute(envelope.Blocks[3].Payload.Span),
            ContentHash.Compute([5]),
            ContentHash.Compute([6]),
            4,
            zero,
            2f,
            1f,
            2f,
            derived,
            derived,
            [],
            [],
            ContentHash.Compute([7]),
            ContentHash.Compute([8]),
            ContentHash.Compute([9]),
            ContentHash.Compute([10]),
            ContentHash.Compute([11]),
            ContentHash.Compute([12]),
            12,
            4,
            1,
            zero,
            "default",
            []);
        return new PlannedCoupledTransformTarget(
            visual,
            collision,
            [
                new PlannedTargetBlock(1, "MDAT", visual.MeshBlockInputHash),
                new PlannedTargetBlock(2, "MBUF", visual.MbufBlockInputHash),
                new PlannedTargetBlock(3, "PHYS", collision.PayloadInputHash),
            ]);
    }

    private static byte[] ResourceBytes()
    {
        var blocks = new[]
        {
            ("DATA", "data-before"u8.ToArray()),
            ("MDAT", "mdat-before"u8.ToArray()),
            ("MBUF", "mbuf-before"u8.ToArray()),
            ("PHYS", "phys-before"u8.ToArray()),
            ("RERL", "rerl-before"u8.ToArray()),
        };
        const int tableStart = 16;
        var payloadStart = Align(tableStart + (blocks.Length * 12), 16);
        var offsets = new int[blocks.Length];
        var position = payloadStart;
        for (var index = 0; index < blocks.Length; index++)
        {
            position = Align(position, 16);
            offsets[index] = position;
            position += blocks[index].Item2.Length;
        }

        var bytes = new byte[position];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), tableStart - 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), blocks.Length);
        for (var index = 0; index < blocks.Length; index++)
        {
            var entry = tableStart + (index * 12);
            Encoding.ASCII.GetBytes(blocks[index].Item1).CopyTo(bytes, entry);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), offsets[index] - (entry + 4));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 8), blocks[index].Item2.Length);
            blocks[index].Item2.CopyTo(bytes, offsets[index]);
        }

        return bytes;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & -alignment;
}

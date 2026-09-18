using System.Buffers.Binary;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class ResourceEnvelopeTests
{
    [Fact]
    public void AdapterReportsPinnedSemanticDependencyVersions()
    {
        var versions = new Source2CompiledModelAdapter().ComponentVersions;

        Assert.Equal("2", versions["s2modkit.adapter.source2"]);
        Assert.Contains("20.0.6980", versions["nuget.valveresourceformat"], StringComparison.Ordinal);
        Assert.NotEmpty(versions["nuget.valvekeyvalue"]);
    }

    [Fact]
    public void RebuildReplacesOnlyTargetBlockAndUpdatesFollowingOffset()
    {
        var input = CreateResource(("RERL", "refs"u8.ToArray()), ("DATA", "old-data"u8.ToArray()), ("VBIB", [1, 2, 3, 4, 5]));
        var original = ResourceEnvelopeReader.Read(input);
        var replacement = Encoding.UTF8.GetBytes(new string('x', 41));

        var output = ResourceEnvelopeWriter.Rebuild(original, new Dictionary<int, ReadOnlyMemory<byte>> { [1] = replacement });
        var rebuilt = ResourceEnvelopeReader.Read(output);

        Assert.Equal(replacement, rebuilt.Blocks[1].Payload.ToArray());
        Assert.Equal(original.Blocks[0].Payload.ToArray(), rebuilt.Blocks[0].Payload.ToArray());
        Assert.Equal(original.Blocks[2].Payload.ToArray(), rebuilt.Blocks[2].Payload.ToArray());
        Assert.True(rebuilt.Blocks[2].Offset > original.Blocks[2].Offset);
        Assert.Equal(0, rebuilt.Blocks[2].Offset % 16);
        Assert.Equal((uint)output.Length, BinaryPrimitives.ReadUInt32LittleEndian(output));
    }

    [Fact]
    public void RebuildWithoutReplacementsIsByteIdenticalForCanonicalInput()
    {
        var input = CreateResource(("DATA", "one"u8.ToArray()), ("VBIB", "two"u8.ToArray()));
        var envelope = ResourceEnvelopeReader.Read(input);

        var output = ResourceEnvelopeWriter.Rebuild(envelope, new Dictionary<int, ReadOnlyMemory<byte>>());

        Assert.Equal(input, output);
    }

    [Fact]
    public void RebuildPreservesOpaqueInterBlockAndTrailingPadding()
    {
        var input = CreateResource(("DATA", "old"u8.ToArray()), ("VBIB", "vertices"u8.ToArray()));
        var initial = ResourceEnvelopeReader.Read(input);
        input.AsSpan(
            initial.Blocks[0].Offset + initial.Blocks[0].Payload.Length,
            initial.Blocks[0].PaddingAfter.Length).Fill(0xa5);
        Array.Resize(ref input, input.Length + 3);
        new byte[] { 0x11, 0x22, 0x33 }.CopyTo(input, input.Length - 3);
        BinaryPrimitives.WriteUInt32LittleEndian(input, checked((uint)input.Length));
        var original = ResourceEnvelopeReader.Read(input);
        var replacement = "replacement"u8.ToArray();

        var output = ResourceEnvelopeWriter.Rebuild(
            original,
            new Dictionary<int, ReadOnlyMemory<byte>> { [0] = replacement });
        var rebuilt = ResourceEnvelopeReader.Read(output);

        Assert.Equal(original.Blocks[0].PaddingAfter.ToArray(), rebuilt.Blocks[0].PaddingAfter[..original.Blocks[0].PaddingAfter.Length].ToArray());
        Assert.Equal(original.Blocks[1].PaddingAfter.ToArray(), rebuilt.Blocks[1].PaddingAfter.ToArray());
        Assert.Equal(original.Blocks[1].Payload.ToArray(), rebuilt.Blocks[1].Payload.ToArray());
    }

    [Fact]
    public void ReadRejectsTruncatedBlockRange()
    {
        var input = CreateResource(("DATA", "payload"u8.ToArray()));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(16 + 8, 4), int.MaxValue);

        var exception = Assert.Throws<S2ModKitException>(() => ResourceEnvelopeReader.Read(input));

        Assert.Equal("RESOURCE_BLOCK_RANGE_INVALID", exception.Error.Code);
    }

    [Fact]
    public void ReadRejectsDeclaredSizeDrift()
    {
        var input = CreateResource(("DATA", "payload"u8.ToArray()));
        BinaryPrimitives.WriteUInt32LittleEndian(input, checked((uint)input.Length + 1));

        var exception = Assert.Throws<S2ModKitException>(() => ResourceEnvelopeReader.Read(input));

        Assert.Equal("RESOURCE_SIZE_MISMATCH", exception.Error.Code);
    }

    [Fact]
    public void ReadAcceptsFourByteAlignedNonFirstBlock()
    {
        var input = CreateResource(4, ("CTRL", "four"u8.ToArray()), ("RERL", "unaligned-at-sixteen"u8.ToArray()));

        var envelope = ResourceEnvelopeReader.Read(input);

        Assert.Equal(0, envelope.Blocks[1].Offset % 4);
        Assert.NotEqual(0, envelope.Blocks[1].Offset % 16);
        Assert.Equal("unaligned-at-sixteen"u8.ToArray(), envelope.Blocks[1].Payload.ToArray());
    }

    [Fact]
    public void ReadAcceptsFourByteAlignedFirstBlockImmediatelyAfterTable()
    {
        var input = CreateResource(4, 4, ("RERL", "dependency-table"u8.ToArray()));

        var envelope = ResourceEnvelopeReader.Read(input);

        Assert.Equal(28, envelope.PayloadStart);
        Assert.Equal(28, envelope.Blocks[0].Offset);
        Assert.Equal("dependency-table"u8.ToArray(), envelope.Blocks[0].Payload.ToArray());
    }

    [Fact]
    public void DeclaredResourcePrefixCanBeReadWithoutTreatingStreamingTailAsEnvelopeData()
    {
        var prefix = CreateResource(("RERL", "dependency-table"u8.ToArray()));
        var artifact = new byte[prefix.Length + 8];
        prefix.CopyTo(artifact, 0);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(artifact, prefix.Length);
        var declaredSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(artifact));

        var envelope = ResourceEnvelopeReader.Read(artifact.AsMemory(0, declaredSize));

        Assert.Equal("dependency-table"u8.ToArray(), envelope.Blocks[0].Payload.ToArray());
        var exception = Assert.Throws<S2ModKitException>(() => ResourceEnvelopeReader.Read(artifact));
        Assert.Equal("RESOURCE_SIZE_MISMATCH", exception.Error.Code);
    }

    private static byte[] CreateResource(params (string Type, byte[] Payload)[] blocks)
        => CreateResource(16, 16, blocks);

    private static byte[] CreateResource(int subsequentAlignment, params (string Type, byte[] Payload)[] blocks)
        => CreateResource(16, subsequentAlignment, blocks);

    private static byte[] CreateResource(int firstAlignment, int subsequentAlignment, params (string Type, byte[] Payload)[] blocks)
    {
        const int tableStart = 16;
        var payloadStart = Align(tableStart + blocks.Length * 12, firstAlignment);
        var offsets = new int[blocks.Length];
        var position = payloadStart;
        for (var index = 0; index < blocks.Length; index++)
        {
            position = Align(position, index == 0 ? firstAlignment : subsequentAlignment);
            offsets[index] = position;
            position += blocks[index].Payload.Length;
        }

        var bytes = new byte[position];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), tableStart - 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), blocks.Length);
        for (var index = 0; index < blocks.Length; index++)
        {
            var entry = tableStart + index * 12;
            Encoding.ASCII.GetBytes(blocks[index].Type).CopyTo(bytes, entry);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4, 4), offsets[index] - (entry + 4));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 8, 4), blocks[index].Payload.Length);
            blocks[index].Payload.CopyTo(bytes, offsets[index]);
        }

        return bytes;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) & -alignment;
}

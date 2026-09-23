using System.Buffers.Binary;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed record Source2ResourceBlock(
    int Index,
    string Type,
    int EntryOffset,
    int Offset,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> PaddingAfter);

public sealed record Source2ResourceEnvelope(
    ushort HeaderVersion,
    ushort ResourceVersion,
    int TableStart,
    int PayloadStart,
    ReadOnlyMemory<byte> HeaderAndTable,
    IReadOnlyList<Source2ResourceBlock> Blocks)
{
    public IReadOnlyList<ResourceBlockSnapshot> CreateBlockSnapshots() => Blocks
        .Select(block => new ResourceBlockSnapshot(block.Type, block.Index, block.Offset, block.Payload.Length, ContentHash.Compute(block.Payload.Span)))
        .ToArray();
}

public static class ResourceEnvelopeReader
{
    private const int FixedHeaderSize = 16;
    private const int BlockEntrySize = 12;
    private const int BlockAlignment = 16;
    private const int MinimumBlockAlignment = 4;
    private const int MaximumBlockCount = 4096;
    private const int MaximumResourceSize = 1024 * 1024 * 1024;

    public static Source2ResourceEnvelope Read(ReadOnlyMemory<byte> content)
    {
        try
        {
            return ReadCore(content);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new S2ModKitException(
                new S2Error("RESOURCE_ENVELOPE_MALFORMED", "source2_envelope", "The compiled resource envelope is malformed or uses unsupported bounds.", "Inspect the input with a compatible Source 2 tool; no mutation was attempted.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static Source2ResourceEnvelope ReadCore(ReadOnlyMemory<byte> content)
    {
        if (content.Length < FixedHeaderSize || content.Length > MaximumResourceSize)
        {
            throw Errors.Unsupported("RESOURCE_SIZE_UNSUPPORTED", $"Resource size {content.Length} is outside the supported range.", "Use a compiled model resource between 16 bytes and 1 GiB.");
        }

        var bytes = content.Span;
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        if (declaredSize != content.Length)
        {
            throw Errors.Unsupported("RESOURCE_SIZE_MISMATCH", $"Resource header declares {declaredSize} bytes but the input contains {content.Length}.", "Use an intact standalone compiled-model resource.");
        }

        var headerVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        var resourceVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        if (headerVersion != 12)
        {
            throw Errors.Unsupported("RESOURCE_HEADER_VERSION_UNSUPPORTED", $"Resource header version {headerVersion} is not supported.", "Use a Source 2 resource with header version 12 or add a reviewed adapter profile.");
        }

        var tableStart = checked(8 + BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4)));
        var blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
        if (tableStart < FixedHeaderSize || blockCount < 1 || blockCount > MaximumBlockCount)
        {
            throw Errors.Unsupported("RESOURCE_BLOCK_TABLE_UNSUPPORTED", $"Block table start {tableStart} or count {blockCount} is unsupported.", "Use an intact resource containing 1-4096 blocks.");
        }

        var tableEnd = checked(tableStart + checked(blockCount * BlockEntrySize));
        var minimumPayloadStart = Align(tableEnd, MinimumBlockAlignment);
        if (tableEnd > content.Length || minimumPayloadStart > content.Length)
        {
            throw Errors.Unsupported("RESOURCE_BLOCK_TABLE_TRUNCATED", "The resource block table extends beyond the input.", "Use an intact compiled resource.");
        }

        var firstEntryOffset = tableStart;
        var firstRelativeOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(firstEntryOffset + 4, 4));
        var firstAbsoluteOffset = (long)firstEntryOffset + 4 + firstRelativeOffset;
        if (firstAbsoluteOffset < minimumPayloadStart
            || firstAbsoluteOffset > content.Length
            || firstAbsoluteOffset % MinimumBlockAlignment != 0)
        {
            throw Errors.Unsupported("RESOURCE_FIRST_BLOCK_OFFSET_UNSUPPORTED", $"First block starts at {firstAbsoluteOffset}; the table ends at {tableEnd}.", "Use an intact resource with a first block aligned to at least four bytes.");
        }

        var payloadStart = checked((int)firstAbsoluteOffset);

        var blocks = new Source2ResourceBlock[blockCount];
        var previousEnd = payloadStart;
        for (var index = 0; index < blockCount; index++)
        {
            var entryOffset = checked(tableStart + checked(index * BlockEntrySize));
            var typeBytes = bytes.Slice(entryOffset, 4);
            if (!IsPrintableAscii(typeBytes))
            {
                throw Errors.Unsupported("RESOURCE_BLOCK_TYPE_INVALID", $"Block {index} has a non-printable type code.", "Use an intact compiled resource.");
            }

            var type = Encoding.ASCII.GetString(typeBytes);
            var relativeOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(entryOffset + 4, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(entryOffset + 8, 4));
            var absoluteOffsetValue = (long)entryOffset + 4 + relativeOffset;
            var endValue = absoluteOffsetValue + size;
            if (size < 0 || absoluteOffsetValue < payloadStart || absoluteOffsetValue % MinimumBlockAlignment != 0 || absoluteOffsetValue < previousEnd || endValue > content.Length)
            {
                throw Errors.Unsupported("RESOURCE_BLOCK_RANGE_INVALID", $"Block {index} ({type}) has invalid offset {absoluteOffsetValue} or size {size}.", "Use an intact, ordered, and at least four-byte-aligned compiled resource.");
            }

            var absoluteOffset = checked((int)absoluteOffsetValue);
            var end = checked((int)endValue);

            blocks[index] = new Source2ResourceBlock(
                index,
                type,
                entryOffset,
                absoluteOffset,
                content.Slice(absoluteOffset, size),
                ReadOnlyMemory<byte>.Empty);
            previousEnd = end;
        }

        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var payloadEnd = checked(block.Offset + block.Payload.Length);
            var paddingEnd = index + 1 < blocks.Length ? blocks[index + 1].Offset : content.Length;
            blocks[index] = block with { PaddingAfter = content.Slice(payloadEnd, paddingEnd - payloadEnd) };
        }

        return new Source2ResourceEnvelope(headerVersion, resourceVersion, tableStart, payloadStart, content[..payloadStart], blocks);
    }

    internal static int Align(int value, int alignment) => checked((value + alignment - 1) & -alignment);

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is < 0x20 or > 0x7e)
            {
                return false;
            }
        }

        return true;
    }
}

public static class ResourceEnvelopeWriter
{
    private const int BlockAlignment = 16;
    private const int MaximumResourceSize = 1024 * 1024 * 1024;

    public static byte[] Rebuild(Source2ResourceEnvelope envelope, IReadOnlyDictionary<int, ReadOnlyMemory<byte>> replacements)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Keys.Any(index => index < 0 || index >= envelope.Blocks.Count))
        {
            throw Errors.Unsupported("RESOURCE_REPLACEMENT_INDEX_INVALID", "A replacement references a block outside the resource table.", "Use block indices from the current inspection snapshot.");
        }

        try
        {
            var offsets = new int[envelope.Blocks.Count];
            var payloads = new ReadOnlyMemory<byte>[envelope.Blocks.Count];
            var position = envelope.PayloadStart;
            for (var index = 0; index < envelope.Blocks.Count; index++)
            {
                position = ResourceEnvelopeReader.Align(position, BlockAlignment);
                offsets[index] = position;
                payloads[index] = replacements.TryGetValue(index, out var replacement) ? replacement : envelope.Blocks[index].Payload;
                position = checked(position + payloads[index].Length);
                position = checked(position + envelope.Blocks[index].PaddingAfter.Length);
                if (position > MaximumResourceSize)
                {
                    throw Errors.Unsupported("RESOURCE_OUTPUT_SIZE_UNSUPPORTED", "The rebuilt resource exceeds the supported 1 GiB limit.", "Use a smaller target block or a future streaming writer.");
                }
            }

            var output = new byte[position];
            envelope.HeaderAndTable.Span.CopyTo(output);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), checked((uint)output.Length));
            for (var index = 0; index < envelope.Blocks.Count; index++)
            {
                var block = envelope.Blocks[index];
                payloads[index].Span.CopyTo(output.AsSpan(offsets[index], payloads[index].Length));
                block.PaddingAfter.Span.CopyTo(output.AsSpan(offsets[index] + payloads[index].Length, block.PaddingAfter.Length));
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(block.EntryOffset + 4, 4), checked(offsets[index] - (block.EntryOffset + 4)));
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(block.EntryOffset + 8, 4), payloads[index].Length);
            }

            var rebuilt = ResourceEnvelopeReader.Read(output);
            foreach (var block in envelope.Blocks.Where(block => !replacements.ContainsKey(block.Index)))
            {
                if (!rebuilt.Blocks[block.Index].Payload.Span.SequenceEqual(block.Payload.Span))
                {
                    throw Errors.Verification("NON_TARGET_BLOCK_CHANGED", $"Non-target block {block.Index} ({block.Type}) changed during rebuild.", "Reject the candidate and inspect the envelope writer.");
                }
            }

            return output;
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new S2ModKitException(
                new S2Error("RESOURCE_REBUILD_OVERFLOW", "source2_envelope", "Checked resource arithmetic overflowed during rebuild.", "Reject the input or target payload; no candidate was published.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }
}

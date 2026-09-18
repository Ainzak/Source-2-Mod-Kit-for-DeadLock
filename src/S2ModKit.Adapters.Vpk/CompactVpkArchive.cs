using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Vpk;

public sealed record CompactVpkEntry(
    string LogicalPath,
    string Extension,
    string DirectoryPath,
    string Name,
    uint Crc32,
    long Length,
    long DataOffset,
    ContentHash ContentHash);

public sealed record CompactVpkArchive(
    string Path,
    ContentHash ContentHash,
    long Size,
    IReadOnlyList<CompactVpkEntry> Entries);

[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The VPK v2 binary format mandates MD5 checksums; SHA-256 is used separately for content identity.")]
public static class CompactVpkArchiveIO
{
    private const uint Signature = 0x55AA1234;
    private const uint Version = 2;
    private const ushort DirectoryArchiveIndex = 0x7fff;
    private const ushort EntryTerminator = 0xffff;
    private const int HeaderSize = 28;
    private const int OtherMd5Size = 48;
    private const int MaximumTreeBytes = 64 * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private const int MaximumTreeStringBytes = 4096;
    private const int CopyBufferSize = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<CompactVpkArchive> ReadAndVerifyAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw Errors.Input("VPK_NOT_FOUND", $"VPK '{fullPath}' does not exist.", "Provide an existing immutable single-file VPK v2 archive.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length < HeaderSize + OtherMd5Size || fileInfo.Length >= uint.MaxValue)
        {
            throw Errors.Unsupported("VPK_SIZE_UNSUPPORTED", $"VPK size {fileInfo.Length} is outside the compact profile.", "Use a single-file VPK v2 archive below 4 GiB.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var headerBytes = new byte[HeaderSize];
        await ReadExactlyAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(4, 4));
        var treeSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(8, 4));
        var fileDataSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(12, 4));
        var archiveMd5Size = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(16, 4));
        var otherMd5Size = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(20, 4));
        var signatureSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(24, 4));
        if (signature != Signature || version != Version)
        {
            throw Errors.Unsupported("VPK_PROFILE_UNSUPPORTED", $"VPK signature/version {signature:x8}/{version} is not the supported single-file v2 profile.", "Use an intact VPK v2 archive or add a reviewed adapter profile.");
        }

        if (treeSize is 0 or > MaximumTreeBytes || archiveMd5Size != 0 || otherMd5Size != OtherMd5Size || signatureSize != 0)
        {
            throw Errors.Unsupported("VPK_SECTIONS_UNSUPPORTED", "The VPK uses unsupported tree, archive-MD5, signature, or section sizes.", "Use the compact unsigned single-file VPK v2 profile.");
        }

        var expectedLength = checked((long)HeaderSize + treeSize + fileDataSize + archiveMd5Size + otherMd5Size + signatureSize);
        if (expectedLength != stream.Length)
        {
            throw Errors.Verification("VPK_LENGTH_MISMATCH", $"VPK length {stream.Length} does not match header length {expectedLength}.", "Reject the truncated or malformed archive.");
        }

        var tree = new byte[(int)treeSize];
        await ReadExactlyAsync(stream, tree, cancellationToken).ConfigureAwait(false);
        var dataStart = checked((long)HeaderSize + treeSize);
        var entries = ParseTree(tree, dataStart, fileDataSize);
        await VerifyHeaderChecksumsAsync(stream, tree, dataStart + fileDataSize, cancellationToken).ConfigureAwait(false);

        var verifiedEntries = new List<CompactVpkEntry>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = await ComputeEntryDigestAsync(stream, entry.DataOffset, entry.Length, cancellationToken).ConfigureAwait(false);
            if (digest.Crc32 != entry.Crc32)
            {
                throw Errors.Verification("VPK_ENTRY_CRC_MISMATCH", $"Entry '{entry.LogicalPath}' CRC32 {digest.Crc32:x8} does not match {entry.Crc32:x8}.", "Reject the damaged archive and restore it from a known-good source.");
            }

            verifiedEntries.Add(entry with { ContentHash = digest.ContentHash });
        }

        stream.Position = 0;
        var archiveSha = new ContentHash(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
        return new CompactVpkArchive(fullPath, archiveSha, stream.Length, verifiedEntries);
    }

    public static async Task WriteAsync(
        string outputPath,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw Errors.Input("VPK_EMPTY_ARCHIVE", "A VPK must contain at least one entry.", "Provide one or more logical paths and byte payloads.");
        }

        var prepared = entries.Select(pair =>
        {
            var parts = VpkPathParts.FromLogicalPath(pair.Key);
            return new WriteEntry(parts, pair.Value.Length, VpkCrc32.Compute(pair.Value.Span), pair.Value, null, 0);
        }).ToArray();
        await WritePreparedAsync(Path.GetFullPath(outputPath), prepared, cancellationToken).ConfigureAwait(false);
    }

    public static async Task RepackReplacingEntryAsync(
        CompactVpkArchive source,
        string entryLogicalPath,
        ReadOnlyMemory<byte> replacement,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalizedTarget = NormalizeVpkPath(entryLogicalPath);
        var matches = source.Entries.Where(entry => string.Equals(entry.LogicalPath, normalizedTarget, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw Errors.Verification("VPK_TARGET_ENTRY_CARDINALITY", $"Expected exactly one source entry '{normalizedTarget}', found {matches.Length}.", "Select one unambiguous logical VPK path.");
        }

        var prepared = source.Entries.Select(entry =>
        {
            var parts = new VpkPathParts(entry.Extension, entry.DirectoryPath, entry.Name, entry.LogicalPath);
            return string.Equals(entry.LogicalPath, normalizedTarget, StringComparison.Ordinal)
                ? new WriteEntry(parts, replacement.Length, VpkCrc32.Compute(replacement.Span), replacement, null, 0)
                : new WriteEntry(parts, entry.Length, entry.Crc32, default, source.Path, entry.DataOffset);
        }).ToArray();
        await WritePreparedAsync(Path.GetFullPath(outputPath), prepared, cancellationToken).ConfigureAwait(false);
    }

    public static string NormalizeVpkPath(string value)
    {
        var normalized = StableIdentity.NormalizePath(value);
        if (normalized.Length == 0
            || normalized[^1] == '/'
            || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || normalized.IndexOfAny([':', '\0']) >= 0)
        {
            throw Errors.Input("VPK_ENTRY_PATH_INVALID", $"'{value}' is not a safe VPK logical path.", "Use a relative slash-separated path without parent segments.");
        }

        return normalized;
    }

    private static List<CompactVpkEntry> ParseTree(byte[] tree, long dataStart, uint fileDataSize)
    {
        var entries = new List<CompactVpkEntry>();
        using var stream = new MemoryStream(tree, writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        while (true)
        {
            var extension = ReadCString(reader);
            if (extension.Length == 0)
            {
                break;
            }

            ValidateTreePart(extension, "extension");
            while (true)
            {
                var directory = ReadCString(reader);
                if (directory.Length == 0)
                {
                    break;
                }

                ValidateTreePart(directory, "directory");
                while (true)
                {
                    var name = ReadCString(reader);
                    if (name.Length == 0)
                    {
                        break;
                    }

                    ValidateTreePart(name, "name");
                    if (stream.Length - stream.Position < 18)
                    {
                        throw Errors.Verification("VPK_TREE_TRUNCATED", "A VPK entry descriptor is truncated.", "Reject the malformed archive.");
                    }

                    var crc = reader.ReadUInt32();
                    var preloadBytes = reader.ReadUInt16();
                    var archiveIndex = reader.ReadUInt16();
                    var offset = reader.ReadUInt32();
                    var length = reader.ReadUInt32();
                    var terminator = reader.ReadUInt16();
                    if (preloadBytes != 0 || archiveIndex != DirectoryArchiveIndex || terminator != EntryTerminator)
                    {
                        throw Errors.Unsupported("VPK_ENTRY_PROFILE_UNSUPPORTED", "The VPK uses preload bytes, an external chunk, or an invalid entry terminator.", "Use the compact embedded-data profile or add a reviewed adapter profile.");
                    }

                    var logicalPath = BuildLogicalPath(extension, directory, name);
                    var absoluteOffset = checked(dataStart + offset);
                    if ((ulong)offset + length > fileDataSize)
                    {
                        throw Errors.Verification("VPK_ENTRY_RANGE_INVALID", $"Entry '{logicalPath}' lies outside the declared data section.", "Reject the malformed archive.");
                    }

                    entries.Add(new CompactVpkEntry(logicalPath, extension, directory, name, crc, length, absoluteOffset, default));
                    if (entries.Count > MaximumEntryCount)
                    {
                        throw Errors.Unsupported("VPK_ENTRY_COUNT_UNSUPPORTED", $"The archive exceeds {MaximumEntryCount} entries.", "Use a bounded archive or add a reviewed higher-capacity profile.");
                    }
                }
            }
        }

        if (stream.Position != stream.Length)
        {
            throw Errors.Verification("VPK_TREE_TRAILING_DATA", "The directory tree contains bytes after its terminator.", "Reject the malformed archive.");
        }

        if (entries.Count == 0)
        {
            throw Errors.Verification("VPK_EMPTY_ARCHIVE", "The VPK directory tree contains no entries.", "Use an intact non-empty archive.");
        }

        if (entries.Select(entry => entry.LogicalPath).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw Errors.Verification("VPK_DUPLICATE_ENTRY", "The VPK contains duplicate normalized logical paths.", "Resolve the ambiguous archive before packaging.");
        }

        long expectedOffset = 0;
        foreach (var entry in entries.OrderBy(entry => entry.DataOffset))
        {
            var relativeOffset = entry.DataOffset - dataStart;
            if (relativeOffset != expectedOffset)
            {
                throw Errors.Unsupported("VPK_DATA_LAYOUT_UNSUPPORTED", "The VPK data section contains gaps, overlaps, or non-canonical ranges.", "Use a compact contiguous archive or add a reviewed layout profile.");
            }

            expectedOffset = checked(expectedOffset + entry.Length);
        }

        if (expectedOffset != fileDataSize)
        {
            throw Errors.Verification("VPK_DATA_SIZE_MISMATCH", "Entry ranges do not cover the declared VPK data section.", "Reject the malformed archive.");
        }

        return entries;
    }

    private static async Task WritePreparedAsync(string outputPath, IReadOnlyList<WriteEntry> inputEntries, CancellationToken cancellationToken)
    {
        if (inputEntries.Count is 0 or > MaximumEntryCount)
        {
            throw Errors.Unsupported("VPK_ENTRY_COUNT_UNSUPPORTED", $"A compact VPK must contain between 1 and {MaximumEntryCount} entries.", "Use a bounded non-empty archive.");
        }

        var entries = inputEntries
            .OrderBy(entry => entry.Parts.Extension, StringComparer.Ordinal)
            .ThenBy(entry => entry.Parts.DirectoryPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.Parts.Name, StringComparer.Ordinal)
            .ToArray();
        if (entries.Select(entry => entry.Parts.LogicalPath).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw Errors.Input("VPK_DUPLICATE_ENTRY", "Cannot write duplicate VPK logical paths.", "Provide one payload per normalized logical path.");
        }

        long runningOffset = 0;
        var offsets = new uint[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].Length < 0 || entries[index].Length > uint.MaxValue || runningOffset > uint.MaxValue)
            {
                throw Errors.Unsupported("VPK_SIZE_UNSUPPORTED", "An entry or archive exceeds the compact 4 GiB profile.", "Use a smaller archive or add a reviewed multi-part profile.");
            }

            offsets[index] = (uint)runningOffset;
            runningOffset = checked(runningOffset + entries[index].Length);
        }

        if (runningOffset >= uint.MaxValue)
        {
            throw Errors.Unsupported("VPK_SIZE_UNSUPPORTED", "The data section reaches the compact 4 GiB boundary.", "Use a smaller archive or add a reviewed multi-part profile.");
        }

        var tree = BuildTree(entries, offsets);
        if (tree.Length > MaximumTreeBytes)
        {
            throw Errors.Unsupported("VPK_TREE_SIZE_UNSUPPORTED", "The VPK directory tree exceeds 64 MiB.", "Use a bounded archive.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("VPK output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw Errors.Input("VPK_OUTPUT_EXISTS", $"Output '{outputPath}' already exists.", "Choose a new immutable candidate path.");
        }

        var partialPath = $"{outputPath}.partial-{Guid.NewGuid():N}";
        try
        {
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(BuildHeader((uint)tree.Length, (uint)runningOffset), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tree, cancellationToken).ConfigureAwait(false);

                await using var sharedSource = OpenSharedSource(entries);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.SourcePath is null)
                    {
                        await output.WriteAsync(entry.Memory, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (sharedSource is null || !string.Equals(Path.GetFullPath(entry.SourcePath), Path.GetFullPath(sharedSource.Name), StringComparison.OrdinalIgnoreCase))
                        {
                            throw Errors.Unsupported("VPK_MULTI_SOURCE_UNSUPPORTED", "Streaming repack received more than one source archive.", "Use one immutable source VPK per package build.");
                        }

                        sharedSource.Position = entry.SourceOffset;
                        await CopyExactlyAsync(sharedSource, output, entry.Length, cancellationToken).ConfigureAwait(false);
                    }
                }

                var treeChecksum = MD5.HashData(tree);
                var emptyArchiveChecksum = MD5.HashData([]);
                await output.WriteAsync(treeChecksum, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(emptyArchiveChecksum, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                var wholeFileChecksum = await MD5.HashDataAsync(output, cancellationToken).ConfigureAwait(false);
                output.Position = output.Length;
                await output.WriteAsync(wholeFileChecksum, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);

                var expectedLength = checked((long)HeaderSize + tree.Length + runningOffset + OtherMd5Size);
                if (output.Length != expectedLength)
                {
                    throw Errors.Verification("VPK_WRITE_LENGTH_MISMATCH", $"Written VPK length {output.Length} does not match {expectedLength}.", "Reject the temporary archive.");
                }
            }

            File.Move(partialPath, outputPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    private static FileStream? OpenSharedSource(IReadOnlyList<WriteEntry> entries)
    {
        var sourcePath = entries.Select(entry => entry.SourcePath).FirstOrDefault(path => path is not null);
        return sourcePath is null
            ? null
            : new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    private static byte[] BuildTree(IReadOnlyList<WriteEntry> entries, uint[] offsets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        var indexed = entries.Select((entry, index) => (Entry: entry, Offset: offsets[index]));
        foreach (var extensionGroup in indexed.GroupBy(item => item.Entry.Parts.Extension, StringComparer.Ordinal))
        {
            WriteCString(writer, extensionGroup.Key);
            foreach (var directoryGroup in extensionGroup.GroupBy(item => item.Entry.Parts.DirectoryPath, StringComparer.Ordinal))
            {
                WriteCString(writer, directoryGroup.Key);
                foreach (var item in directoryGroup)
                {
                    WriteCString(writer, item.Entry.Parts.Name);
                    writer.Write(item.Entry.Crc32);
                    writer.Write((ushort)0);
                    writer.Write(DirectoryArchiveIndex);
                    writer.Write(item.Offset);
                    writer.Write((uint)item.Entry.Length);
                    writer.Write(EntryTerminator);
                }

                WriteCString(writer, string.Empty);
            }

            WriteCString(writer, string.Empty);
        }

        WriteCString(writer, string.Empty);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildHeader(uint treeSize, uint fileDataSize)
    {
        var bytes = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), treeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), fileDataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), OtherMd5Size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 0);
        return bytes;
    }

    private static async Task VerifyHeaderChecksumsAsync(FileStream stream, byte[] tree, long otherMd5Offset, CancellationToken cancellationToken)
    {
        stream.Position = otherMd5Offset;
        var stored = new byte[OtherMd5Size];
        await ReadExactlyAsync(stream, stored, cancellationToken).ConfigureAwait(false);
        var expectedTree = MD5.HashData(tree);
        var expectedArchive = MD5.HashData([]);
        var expectedWhole = await ComputeMd5SegmentAsync(stream, otherMd5Offset + 32, cancellationToken).ConfigureAwait(false);
        if (!stored.AsSpan(0, 16).SequenceEqual(expectedTree)
            || !stored.AsSpan(16, 16).SequenceEqual(expectedArchive)
            || !stored.AsSpan(32, 16).SequenceEqual(expectedWhole))
        {
            throw Errors.Verification("VPK_MD5_MISMATCH", "The VPK tree/archive/whole-file MD5 checks do not validate.", "Reject the damaged or non-canonical archive.");
        }
    }

    private static async Task<byte[]> ComputeMd5SegmentAsync(FileStream stream, long length, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw Errors.Verification("VPK_TRUNCATED", "The VPK ended while computing its whole-file checksum.", "Reject the truncated archive.");
            }

            hash.AppendData(buffer.AsSpan(0, count));
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    private static async Task<EntryDigest> ComputeEntryDigestAsync(FileStream stream, long offset, long length, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var crc = VpkCrc32.Initial;
        var buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw Errors.Verification("VPK_ENTRY_TRUNCATED", "The VPK ended inside an entry payload.", "Reject the truncated archive.");
            }

            sha.AppendData(buffer.AsSpan(0, count));
            crc = VpkCrc32.Append(crc, buffer.AsSpan(0, count));
            remaining -= count;
        }

        return new EntryDigest(new ContentHash(Convert.ToHexStringLower(sha.GetHashAndReset())), VpkCrc32.Finalize(crc));
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw Errors.Verification("VPK_SOURCE_ENTRY_TRUNCATED", "The source VPK changed or ended while repacking an entry.", "Reject the candidate and restore the immutable source archive.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw Errors.Verification("VPK_TRUNCATED", "The VPK ended before the declared structure was read.", "Reject the truncated archive.");
            }

            read += count;
        }
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
            {
                throw Errors.Verification("VPK_TREE_TRUNCATED", "A VPK tree string is not null-terminated.", "Reject the malformed archive.");
            }

            var value = reader.ReadByte();
            if (value == 0)
            {
                try
                {
                    return StrictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException exception)
                {
                    throw new S2ModKitException(
                        new S2Error("VPK_TREE_UTF8_INVALID", "verification", "A VPK tree string is not valid UTF-8.", "Reject the malformed archive.", ErrorCategory.RewriteOrVerification),
                        exception);
                }
            }

            bytes.Add(value);
            if (bytes.Count > MaximumTreeStringBytes)
            {
                throw Errors.Unsupported("VPK_TREE_STRING_UNSUPPORTED", "A VPK tree string exceeds 4096 bytes.", "Use a bounded archive.");
            }
        }
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumTreeStringBytes || bytes.AsSpan().Contains((byte)0))
        {
            throw Errors.Input("VPK_TREE_STRING_INVALID", "A VPK path component cannot be represented safely.", "Use bounded UTF-8 paths without null characters.");
        }

        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static string BuildLogicalPath(string extension, string directory, string name)
    {
        var fileName = extension == " " ? name : $"{name}.{extension}";
        var path = directory == " " ? fileName : $"{directory}/{fileName}";
        var normalized = NormalizeVpkPath(path);
        var canonicalParts = VpkPathParts.FromLogicalPath(normalized);
        var expectedExtension = extension == " " ? " " : extension.ToLowerInvariant();
        var expectedDirectory = directory == " " ? " " : directory.ToLowerInvariant();
        if (!string.Equals(canonicalParts.Extension, expectedExtension, StringComparison.Ordinal)
            || !string.Equals(canonicalParts.DirectoryPath, expectedDirectory, StringComparison.Ordinal)
            || !string.Equals(canonicalParts.Name, name.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw Errors.Unsupported("VPK_PATH_PROFILE_UNSUPPORTED", $"Entry path '{path}' is not canonical for the compact profile.", "Use lower-case slash-separated VPK paths.");
        }

        return normalized;
    }

    private static void ValidateTreePart(string value, string description)
    {
        var invalidDirectory = description == "directory"
            && value != " "
            && value.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..");
        if ((description != "directory" && value.Contains('/'))
            || invalidDirectory
            || value.Contains('\\')
            || value.Contains(':')
            || value is "." or ".."
            || (description != "directory" && value == " "))
        {
            throw Errors.Verification("VPK_TREE_PATH_INVALID", $"VPK {description} component '{value}' is invalid.", "Reject the malformed archive.");
        }
    }

    private sealed record VpkPathParts(string Extension, string DirectoryPath, string Name, string LogicalPath)
    {
        public static VpkPathParts FromLogicalPath(string value)
        {
            var normalized = NormalizeVpkPath(value);
            var slash = normalized.LastIndexOf('/');
            var directory = slash < 0 ? " " : normalized[..slash];
            var fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
            var dot = fileName.LastIndexOf('.');
            var extension = dot <= 0 || dot == fileName.Length - 1 ? " " : fileName[(dot + 1)..];
            var name = extension == " " ? fileName : fileName[..dot];
            if (name.Length == 0)
            {
                throw Errors.Input("VPK_ENTRY_PATH_INVALID", $"'{value}' has no file name.", "Use a complete VPK logical path.");
            }

            return new VpkPathParts(extension, directory, name, normalized);
        }
    }

    private sealed record WriteEntry(
        VpkPathParts Parts,
        long Length,
        uint Crc32,
        ReadOnlyMemory<byte> Memory,
        string? SourcePath,
        long SourceOffset);

    private sealed record EntryDigest(ContentHash ContentHash, uint Crc32);
}

internal static class VpkCrc32
{
    private static readonly uint[] Table = BuildTable();

    public const uint Initial = uint.MaxValue;

    public static uint Compute(ReadOnlySpan<byte> bytes) => Finalize(Append(Initial, bytes));

    public static uint Append(uint current, ReadOnlySpan<byte> bytes)
    {
        var crc = current;
        foreach (var value in bytes)
        {
            crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Finalize(uint value) => value ^ uint.MaxValue;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}

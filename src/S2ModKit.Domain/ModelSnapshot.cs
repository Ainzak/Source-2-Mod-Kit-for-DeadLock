using System.Security.Cryptography;
using System.Text;

namespace S2ModKit.Domain;

public sealed record ArtifactSnapshot(
    string LogicalPath,
    ContentHash ContentHash,
    long Size,
    IReadOnlyList<ResourceBlockSnapshot> Blocks);

public sealed record ResourceBlockSnapshot(
    string Type,
    int Index,
    long Offset,
    int Size,
    ContentHash ContentHash);

public sealed record ModelSnapshot(
    ArtifactSnapshot Artifact,
    IReadOnlyList<LodSnapshot> Lods);

public sealed record LodSnapshot(
    int Level,
    IReadOnlyList<MeshSnapshot> Meshes);

public sealed record MeshSnapshot(
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    ContentHash ImmutableSemanticHash,
    IReadOnlyList<DrawCallSnapshot> DrawCalls)
{
    public MeshGeometrySnapshot? Geometry { get; init; }

    public MechanicalMeshLineage? MechanicalLineage { get; init; }
}

public sealed record MechanicalMeshLineage(
    string Key,
    string SourceLabel,
    string SourceName)
{
    public const int MaximumSourceLabelLength = 1024;

    public static MechanicalMeshLineage FromSourceLabel(string sourceLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);
        var normalized = NormalizeSourceText(sourceLabel, nameof(sourceLabel));
        return new MechanicalMeshLineage(normalized.ToLowerInvariant(), normalized, normalized);
    }

    public bool IsCanonical()
    {
        try
        {
            var normalized = FromSourceLabel(SourceLabel);
            return string.Equals(Key, normalized.Key, StringComparison.Ordinal)
                && string.Equals(SourceLabel, normalized.SourceLabel, StringComparison.Ordinal)
                && string.Equals(SourceName, NormalizeSourceText(SourceName, nameof(SourceName)), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeSourceText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized;
        try
        {
            normalized = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The mechanical mesh lineage text is not valid Unicode.", parameterName, exception);
        }

        if (normalized.Length is 0 or > MaximumSourceLabelLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Mechanical mesh lineage text must contain 1-{MaximumSourceLabelLength} non-control characters after normalization.",
                parameterName);
        }

        return normalized;
    }
}

public sealed record MeshGeometrySnapshot(
    string Status,
    string Summary,
    IReadOnlyList<VertexBufferSnapshot> VertexBuffers,
    IReadOnlyList<IndexBufferSnapshot> IndexBuffers,
    IReadOnlyList<DrawCallGeometrySnapshot> DrawCalls,
    GeometryCodecIdentity? Codec)
{
    public IReadOnlyList<ConnectedComponentSnapshot> ConnectedComponents { get; init; } = [];
}

public sealed record VertexBufferSnapshot(
    int Ordinal,
    int ResourceBlockIndex,
    int VertexCount,
    int Stride,
    ContentHash EncodedHash,
    ContentHash DecodedHash,
    PositionLayout PositionLayout);

public sealed record IndexBufferSnapshot(
    int Ordinal,
    int ResourceBlockIndex,
    int IndexCount,
    int Stride,
    ContentHash EncodedHash,
    ContentHash DecodedHash);

public sealed record DrawCallGeometrySnapshot(
    string DrawCallId,
    int VertexBufferOrdinal,
    int IndexBufferOrdinal,
    int BaseVertex,
    int VertexEndExclusive,
    int UniqueVertexCount,
    ContentHash VertexSetHash,
    GeometryBounds Bounds,
    bool ExclusivelyOwned);

public sealed record ConnectedComponentSnapshot(
    string Id,
    string DrawCallId,
    int VertexBufferOrdinal,
    int IndexBufferOrdinal,
    int VertexCount,
    int TriangleCount,
    ContentHash VertexSetHash,
    GeometryBounds Bounds,
    bool ExclusivelyOwned);

public sealed record DrawCallSnapshot(
    string Id,
    string MaterialPath,
    int DrawCallOrdinal,
    long IndexStart,
    long IndexCount)
{
    public static DrawCallSnapshot Create(
        string resourcePath,
        int lod,
        int meshOrdinal,
        int drawCallOrdinal,
        string materialPath,
        long indexStart,
        long indexCount)
    {
        var normalizedResource = StableIdentity.NormalizePath(resourcePath);
        var normalizedMaterial = StableIdentity.NormalizePath(materialPath);
        var identity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{normalizedResource}|{lod}|{meshOrdinal}|{drawCallOrdinal}|{normalizedMaterial}|{indexStart}|{indexCount}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new DrawCallSnapshot(
            $"dc_{Convert.ToHexStringLower(hash.AsSpan(0, 12))}",
            normalizedMaterial,
            drawCallOrdinal,
            indexStart,
            indexCount);
    }
}

public static class StableIdentity
{
    public static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Replace('\\', '/').ToLowerInvariant();
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.TrimStart('/');
    }
}

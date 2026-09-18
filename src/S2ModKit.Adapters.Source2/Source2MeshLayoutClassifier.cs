using System.Globalization;
using System.Text;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

internal static class Source2MeshLayoutKind
{
    public const string RootMdatBuffers = "root_mdat_mvtx_midx";

    public const string EmbeddedMbuf = "embedded_mbuf";

    public const string ExternalResource = "external_resource";

    public const string EmptyReference = "empty_reference";

    public const string Malformed = "malformed";
}

internal sealed record Source2MeshLayoutClassification(
    string Kind,
    bool IsSupported,
    string? ErrorCode,
    string Summary,
    string Remediation,
    IReadOnlyList<string> NonEmptyReferences,
    int EmptyReferenceCount);

internal static class Source2MeshLayoutClassifier
{
    private const int MaximumMeshReferenceCount = 4096;

    public static Source2MeshLayoutClassification Classify(
        KVObject modelData,
        IReadOnlyList<string> resourceBlockTypes)
    {
        ArgumentNullException.ThrowIfNull(modelData);
        ArgumentNullException.ThrowIfNull(resourceBlockTypes);
        var references = ReadMeshReferences(modelData);
        if (references.Error is not null)
        {
            return Malformed(
                "MESH_REFERENCE_TABLE_MALFORMED",
                references.Error,
                "Use an intact model whose m_refMeshes value is a bounded array of resource-handle strings.",
                [],
                0);
        }

        var values = references.Values!;
        var nonEmpty = values
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var emptyCount = values.Count(value => value.Length == 0);
        var mdatCount = resourceBlockTypes.Count(type => string.Equals(type, "MDAT", StringComparison.Ordinal));
        var mvtxCount = resourceBlockTypes.Count(type => string.Equals(type, "MVTX", StringComparison.Ordinal));
        var midxCount = resourceBlockTypes.Count(type => string.Equals(type, "MIDX", StringComparison.Ordinal));
        var mbufCount = resourceBlockTypes.Count(type => string.Equals(type, "MBUF", StringComparison.Ordinal));
        var hasRootPayloadBuffers = mvtxCount > 0 || midxCount > 0;

        if (nonEmpty.Length > 0)
        {
            if (emptyCount > 0)
            {
                return Malformed(
                    "MESH_LAYOUT_COMBINATION_MALFORMED",
                    "The model combines a non-empty external mesh reference with an empty handle or embedded mesh-buffer blocks.",
                    "Use a model with one unambiguous external or embedded mesh storage profile.",
                    nonEmpty,
                    emptyCount);
            }

            return new Source2MeshLayoutClassification(
                Source2MeshLayoutKind.ExternalResource,
                false,
                "EXTERNAL_MESH_RESOURCE_UNSUPPORTED",
                $"The model references {nonEmpty.Length.ToString(CultureInfo.InvariantCulture)} non-empty external mesh resource(s).",
                "Resolve external mesh resources through a future explicitly reviewed adapter profile.",
                nonEmpty,
                emptyCount);
        }

        if (mbufCount > 0)
        {
            if (hasRootPayloadBuffers)
            {
                return Malformed(
                    "MESH_BUFFER_LAYOUT_MALFORMED",
                    "The model combines embedded MBUF storage with root MVTX/MIDX payload blocks.",
                    "Use a model with one unambiguous characterized buffer profile.",
                    [],
                    emptyCount);
            }

            return new Source2MeshLayoutClassification(
                Source2MeshLayoutKind.EmbeddedMbuf,
                true,
                null,
                $"The model contains {mbufCount.ToString(CultureInfo.InvariantCulture)} embedded MBUF block(s) eligible for bounded read-only profile inspection.",
                string.Empty,
                [],
                emptyCount);
        }

        if (emptyCount > 0)
        {
            return new Source2MeshLayoutClassification(
                Source2MeshLayoutKind.EmptyReference,
                false,
                "EMPTY_MESH_REFERENCE_UNSUPPORTED",
                $"The model contains {emptyCount.ToString(CultureInfo.InvariantCulture)} empty mesh resource handle(s) without an embedded MBUF payload.",
                "Use an intact model with resolved external resources or one characterized embedded storage profile.",
                [],
                emptyCount);
        }

        if (mdatCount > 0 && mvtxCount > 0 && midxCount > 0)
        {
            return new Source2MeshLayoutClassification(
                Source2MeshLayoutKind.RootMdatBuffers,
                true,
                null,
                "The model uses the supported root MDAT mesh profile with MVTX and MIDX payloads.",
                string.Empty,
                [],
                0);
        }

        return Malformed(
            "MESH_BUFFER_LAYOUT_MALFORMED",
            $"The model has an incomplete root buffer set (MDAT={mdatCount.ToString(CultureInfo.InvariantCulture)}, MVTX={mvtxCount.ToString(CultureInfo.InvariantCulture)}, MIDX={midxCount.ToString(CultureInfo.InvariantCulture)}) and no embedded MBUF payload.",
            "Use an intact compiled model with one characterized mesh storage profile.",
            [],
            0);
    }

    public static void RequireSupportedRoot(Source2MeshLayoutClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (!classification.IsSupported
            || !string.Equals(classification.Kind, Source2MeshLayoutKind.RootMdatBuffers, StringComparison.Ordinal))
        {
            throw Errors.Unsupported(
                classification.ErrorCode ?? "MESH_BUFFER_LAYOUT_MALFORMED",
                classification.Summary,
                classification.Remediation);
        }
    }

    private static MeshReferenceReadResult ReadMeshReferences(KVObject modelData)
    {
        if (!modelData.TryGetValue("m_refMeshes", out var value) || value is null || !value.IsArray)
        {
            return new MeshReferenceReadResult(null, "Model DATA does not contain an m_refMeshes array.");
        }

        if (value.Count > MaximumMeshReferenceCount)
        {
            return new MeshReferenceReadResult(
                null,
                $"Model DATA declares {value.Count.ToString(CultureInfo.InvariantCulture)} mesh references; the limit is {MaximumMeshReferenceCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        var references = new List<string>(value.Count);
        for (var index = 0; index < value.Count; index++)
        {
            var item = value[index];
            if (item is null || item.IsArray || item.IsCollection || item.ValueType != KVValueType.String)
            {
                return new MeshReferenceReadResult(
                    null,
                    $"Model DATA mesh reference {index.ToString(CultureInfo.InvariantCulture)} is not a string resource handle.");
            }

            var text = item.ToString(CultureInfo.InvariantCulture).Trim();
            if (text.Length == 0)
            {
                references.Add(string.Empty);
                continue;
            }

            try
            {
                references.Add(StableIdentity.NormalizePath(text));
            }
            catch (ArgumentException)
            {
                return new MeshReferenceReadResult(
                    null,
                    $"Model DATA mesh reference {index.ToString(CultureInfo.InvariantCulture)} is not a valid logical resource path.");
            }
        }

        return new MeshReferenceReadResult(references, null);
    }

    private static Source2MeshLayoutClassification Malformed(
        string code,
        string summary,
        string remediation,
        IReadOnlyList<string> references,
        int emptyReferenceCount) => new(
            Source2MeshLayoutKind.Malformed,
            false,
            code,
            summary,
            remediation,
            references,
            emptyReferenceCount);

    private sealed record MeshReferenceReadResult(
        IReadOnlyList<string>? Values,
        string? Error);
}

internal static class Source2MeshLineageExtractor
{
    public static MechanicalMeshLineage Extract(
        KVObject descriptor,
        int lod,
        string context,
        string sourceNameKey = "m_Name")
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfNegative(lod);

        if (!descriptor.TryGetValue(sourceNameKey, out var value)
            || value is null
            || value.IsArray
            || value.IsCollection
            || value.ValueType != KVValueType.String)
        {
            throw Errors.Unsupported(
                "MESH_LINEAGE_SOURCE_NAME_INVALID",
                $"{context} does not contain one string {sourceNameKey} lineage source.",
                "Use an intact embedded-mesh descriptor with a non-empty source-authored name.");
        }

        try
        {
            var sourceName = value.ToString(CultureInfo.InvariantCulture).Trim().Normalize(NormalizationForm.FormC);
            var lodSuffix = $"_lod{lod.ToString(CultureInfo.InvariantCulture)}";
            var sourceLabel = sourceName.EndsWith(lodSuffix, StringComparison.Ordinal)
                ? sourceName[..^lodSuffix.Length]
                : sourceName;
            var lineage = MechanicalMeshLineage.FromSourceLabel(sourceLabel);
            lineage = lineage with { SourceName = sourceName };
            if (!lineage.IsCanonical())
            {
                throw new ArgumentException("The normalized source name is outside the mechanical lineage contract.", nameof(descriptor));
            }

            return lineage;
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error(
                    "MESH_LINEAGE_SOURCE_NAME_INVALID",
                    "source2_adapter",
                    $"{context} contains an invalid {sourceNameKey} lineage source.",
                    "Use an intact embedded-mesh descriptor with a bounded non-control source-authored name.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }
}

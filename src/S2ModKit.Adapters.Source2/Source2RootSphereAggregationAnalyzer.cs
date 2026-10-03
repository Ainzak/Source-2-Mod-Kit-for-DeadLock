using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

// Observational diagnostic: mapped DATA root spheres equal the maximum mapped MDAT render sphere
// radius across meshes and LODs. It records a scalar relation only and never promotes a coordinate
// space, contributor status, or mutation policy; root/render sphere spaces stay unsupported.
internal sealed record Source2RootSphereAggregationDiagnostic(
    int SchemaVersion,
    string Relation,
    string EvidenceScope,
    string Status,
    string? ReasonCode,
    IReadOnlyList<Source2RootSphereAggregationRow> Rows)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReasonCode { get; init; } = ReasonCode;
}

internal sealed record Source2RootSphereAggregationRow(
    CullingFieldIdentity RootField,
    string RootBoneName,
    float StoredRadius,
    float? MappedMaximum,
    string Status,
    IReadOnlyList<CullingFieldIdentity> Sources)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public float? MappedMaximum { get; init; } = MappedMaximum;
}

internal static class Source2RootSphereAggregationAnalyzer
{
    internal const string ExtensionKey = "rootSphereAggregationV1";
    private const int DiagnosticSchemaVersion = 1;
    private const string RelationName = "mapped_render_radius_max";
    private const string EvidenceScopeName = "offline_static";
    private const string StatusComplete = "complete";
    private const string StatusUnsupported = "unsupported";
    private const string InputUnavailableReasonCode = "ROOT_SPHERE_AGGREGATION_INPUT_UNAVAILABLE";
    private const string InputInconsistentReasonCode = "ROOT_SPHERE_AGGREGATION_INPUT_INCONSISTENT";
    private const string RootSpherePathPrefix = "m_modelSkeleton.m_boneSphere[";
    private const string RenderSpherePathPrefix = "m_skeleton.m_bones[";
    private const string RenderSpherePathSuffix = "].m_flSphereRadius";
    private const string RootSphereBlockType = "DATA";
    private const string RenderSphereBlockType = "MDAT";

    public static IReadOnlyDictionary<string, JsonElement> Attach(CullingInventoryDocument document)
    {
        var extensions = new Dictionary<string, JsonElement>(document.Extensions);
        extensions[ExtensionKey] = JsonSerializer.SerializeToElement(Analyze(document), JsonDefaults.Options);
        return extensions;
    }

    public static Source2RootSphereAggregationDiagnostic Analyze(CullingInventoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var rootFields = new List<(CullingInventoryField Field, int Ordinal)>();
        var renderFields = new List<(CullingInventoryField Field, int Ordinal)>();
        var malformedFamilyClaim = false;
        foreach (var field in document.Fields)
        {
            var fieldPath = field.Identity.FieldPath;
            if (fieldPath.StartsWith(RootSpherePathPrefix, StringComparison.Ordinal))
            {
                // Only exactly indexed sphere members live under the root prefix, so a path that
                // claims the family but fails exact parsing is an inconsistent identity, never an
                // ignorable field. Render bone box fields share the render prefix, so a render
                // family claim also requires the exact sphere suffix.
                if (!TryParseIndexedPath(fieldPath, RootSpherePathPrefix, "]", out var rootOrdinal))
                {
                    malformedFamilyClaim = true;
                }
                else
                {
                    rootFields.Add((field, rootOrdinal));
                }

                continue;
            }

            if (!fieldPath.StartsWith(RenderSpherePathPrefix, StringComparison.Ordinal)
                || !fieldPath.EndsWith(RenderSpherePathSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseIndexedPath(fieldPath, RenderSpherePathPrefix, RenderSpherePathSuffix, out var renderOrdinal))
            {
                malformedFamilyClaim = true;
                continue;
            }

            renderFields.Add((field, renderOrdinal));
        }

        // Availability is checked first: no concrete root spheres or no mesh remaps means there is
        // nothing to aggregate, which is not a claim that the resource is inconsistent.
        if (rootFields.Count == 0 || document.BoneRemaps.Count == 0)
        {
            return Unsupported(InputUnavailableReasonCode);
        }

        if (malformedFamilyClaim
            || !TryValidateRemaps(document, out var modelNames, out var remapsByMesh)
            || HasMeshOrdinalWithoutRemap(document, remapsByMesh)
            || !TryValidateRenderFields(document, renderFields, remapsByMesh, out var renderFieldsByMeshOrdinal)
            || !TryValidateRootFields(document, rootFields, remapsByMesh, modelNames, out var rootFieldsByOrdinal))
        {
            return Unsupported(InputInconsistentReasonCode);
        }

        var rows = new List<Source2RootSphereAggregationRow>(modelNames.Count);
        for (var modelBoneIndex = 0; modelBoneIndex < modelNames.Count; modelBoneIndex++)
        {
            rows.Add(CreateRow(
                rootFieldsByOrdinal[modelBoneIndex],
                modelNames[modelBoneIndex],
                modelBoneIndex,
                remapsByMesh,
                renderFieldsByMeshOrdinal));
        }

        return new Source2RootSphereAggregationDiagnostic(
            DiagnosticSchemaVersion,
            RelationName,
            EvidenceScopeName,
            StatusComplete,
            null,
            rows);
    }

    private static bool TryValidateRemaps(
        CullingInventoryDocument document,
        out IReadOnlyList<string> modelNames,
        out Dictionary<int, CullingMeshBoneRemapInventory> remapsByMesh)
    {
        modelNames = [];
        remapsByMesh = [];
        var byMesh = new Dictionary<int, CullingMeshBoneRemapInventory>();
        IReadOnlyList<string>? names = null;
        foreach (var remap in document.BoneRemaps)
        {
            if (remap.ResourcePath != document.Resource.LogicalPath
                || remap.ResourceHash != document.Resource.ContentHash
                || byMesh.ContainsKey(remap.MeshOrdinal)
                || remap.BoneRemapStatus != "verified"
                || remap.RenderBoneNamesStatus != "verified"
                || remap.ModelBoneNamesStatus != "verified"
                || remap.CoveredLods.Count == 0
                || remap.BoneRemapValues is not { } values
                || remap.RenderBoneNames is not { Count: > 0 } renderNames
                || remap.ModelBoneNames is not { Count: > 0 } currentNames)
            {
                return false;
            }

            // Repeated model-name tables must agree; remaps may exceed the render-name count, but
            // never truncate it and never index outside the model-name table.
            if (names is null)
            {
                names = currentNames;
            }
            else if (!names.SequenceEqual(currentNames))
            {
                return false;
            }

            if (values.Count < renderNames.Count
                || values.Any(value => value < 0 || value >= names.Count))
            {
                return false;
            }

            for (var renderIndex = 0; renderIndex < renderNames.Count; renderIndex++)
            {
                if (names[values[renderIndex]] != renderNames[renderIndex])
                {
                    return false;
                }
            }

            byMesh.Add(remap.MeshOrdinal, remap);
        }

        if (names is null)
        {
            return false;
        }

        modelNames = names;
        remapsByMesh = byMesh;
        return true;
    }

    // Every mesh represented anywhere in the inventory must have a remap record; otherwise an
    // omitted mesh on an already-covered LOD would silently shrink the aggregated maximum.
    private static bool HasMeshOrdinalWithoutRemap(
        CullingInventoryDocument document,
        Dictionary<int, CullingMeshBoneRemapInventory> remapsByMesh) =>
        document.Fields.Any(field => field.Identity.MeshOrdinal is int meshOrdinal
            && !remapsByMesh.ContainsKey(meshOrdinal));

    private static bool TryValidateRenderFields(
        CullingInventoryDocument document,
        List<(CullingInventoryField Field, int Ordinal)> renderFields,
        Dictionary<int, CullingMeshBoneRemapInventory> remapsByMesh,
        out Dictionary<int, Dictionary<int, CullingInventoryField>> renderFieldsByMeshOrdinal)
    {
        renderFieldsByMeshOrdinal = [];
        var byMesh = remapsByMesh.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<int, CullingInventoryField>());
        foreach (var (field, ordinal) in renderFields)
        {
            var identity = field.Identity;
            if (identity.ResourcePath != document.Resource.LogicalPath
                || identity.ResourceHash != document.Resource.ContentHash
                || identity.MeshOrdinal is not int meshOrdinal
                || !remapsByMesh.TryGetValue(meshOrdinal, out var remap)
                || identity.FieldOrdinal != ordinal
                || identity.ResourceBlockIndex != remap.MeshResourceBlockIndex
                || identity.ResourceBlockType != RenderSphereBlockType
                || !SameLods(identity.CoveredLods, remap.CoveredLods)
                || !TryReadSphereRadius(field, out _)
                || !byMesh[meshOrdinal].TryAdd(ordinal, field))
            {
                return false;
            }
        }

        foreach (var (meshOrdinal, remap) in remapsByMesh)
        {
            var fields = byMesh[meshOrdinal];
            var renderBoneCount = remap.RenderBoneNames!.Count;
            if (fields.Count != renderBoneCount
                || Enumerable.Range(0, renderBoneCount).Any(boneIndex => !fields.ContainsKey(boneIndex)))
            {
                return false;
            }
        }

        renderFieldsByMeshOrdinal = byMesh;
        return true;
    }

    private static bool TryValidateRootFields(
        CullingInventoryDocument document,
        List<(CullingInventoryField Field, int Ordinal)> rootFields,
        Dictionary<int, CullingMeshBoneRemapInventory> remapsByMesh,
        IReadOnlyList<string> modelNames,
        out Dictionary<int, CullingInventoryField> rootFieldsByOrdinal)
    {
        rootFieldsByOrdinal = [];
        var byOrdinal = new Dictionary<int, CullingInventoryField>();
        var unionLods = NormalizeLods(remapsByMesh.Values.SelectMany(remap => remap.CoveredLods)).ToArray();
        var blockIndex = rootFields[0].Field.Identity.ResourceBlockIndex;
        foreach (var (field, ordinal) in rootFields)
        {
            var identity = field.Identity;
            if (identity.ResourcePath != document.Resource.LogicalPath
                || identity.ResourceHash != document.Resource.ContentHash
                || identity.MeshOrdinal is not null
                || identity.FieldOrdinal != ordinal
                || identity.ResourceBlockIndex != blockIndex
                || identity.ResourceBlockType != RootSphereBlockType
                || !SameLods(identity.CoveredLods, unionLods)
                || !TryReadSphereRadius(field, out _)
                || !byOrdinal.TryAdd(ordinal, field))
            {
                return false;
            }
        }

        if (byOrdinal.Count != modelNames.Count
            || Enumerable.Range(0, modelNames.Count).Any(index => !byOrdinal.ContainsKey(index)))
        {
            return false;
        }

        rootFieldsByOrdinal = byOrdinal;
        return true;
    }

    private static Source2RootSphereAggregationRow CreateRow(
        CullingInventoryField rootField,
        string rootBoneName,
        int modelBoneIndex,
        Dictionary<int, CullingMeshBoneRemapInventory> remapsByMesh,
        Dictionary<int, Dictionary<int, CullingInventoryField>> renderFieldsByMeshOrdinal)
    {
        var storedRadius = rootField.RawOriginalValue!.Radius!.Value;
        var sources = new List<(CullingFieldIdentity Identity, float Radius)>();
        foreach (var (meshOrdinal, remap) in remapsByMesh.OrderBy(pair => pair.Key))
        {
            var values = remap.BoneRemapValues!;
            var fields = renderFieldsByMeshOrdinal[meshOrdinal];
            for (var renderIndex = 0; renderIndex < remap.RenderBoneNames!.Count; renderIndex++)
            {
                if (values[renderIndex] == modelBoneIndex)
                {
                    var field = fields[renderIndex];
                    sources.Add((field.Identity, field.RawOriginalValue!.Radius!.Value));
                }
            }
        }

        var orderedSources = sources
            .OrderBy(source => source.Identity.MeshOrdinal!.Value)
            .ThenBy(source => source.Identity.ResourceBlockIndex)
            .ThenBy(source => source.Identity.FieldOrdinal!.Value)
            .ToArray();
        if (orderedSources.Length == 0)
        {
            return new Source2RootSphereAggregationRow(rootField.Identity, rootBoneName, storedRadius, null, "unmapped", []);
        }

        // Every mapped radius is validated finite and nonnegative, so plain comparison is
        // bit-faithful; only the two zero encodings need explicit canonicalization.
        var maximum = float.NegativeInfinity;
        foreach (var source in orderedSources)
        {
            if (source.Radius > maximum)
            {
                maximum = source.Radius;
            }
        }

        if (maximum == 0f)
        {
            maximum = 0f;
        }

        var matched = BitConverter.SingleToInt32Bits(storedRadius) == BitConverter.SingleToInt32Bits(maximum);
        return new Source2RootSphereAggregationRow(
            rootField.Identity,
            rootBoneName,
            storedRadius,
            maximum,
            matched ? "matched" : "mismatch",
            orderedSources.Select(source => source.Identity).ToArray());
    }

    private static bool TryReadSphereRadius(CullingInventoryField field, out float radius)
    {
        radius = 0f;
        var value = field.RawOriginalValue;
        if (value is null || value.Kind != "sphere_radius" || value.Radius is not { } raw
            || !float.IsFinite(raw) || raw < 0f)
        {
            return false;
        }

        radius = raw;
        return true;
    }

    private static bool SameLods(IReadOnlyList<int> left, IReadOnlyList<int> right) =>
        NormalizeLods(left).SequenceEqual(NormalizeLods(right));

    private static IEnumerable<int> NormalizeLods(IEnumerable<int> lods) =>
        lods.ToHashSet().OrderBy(lod => lod);

    private static bool TryParseIndexedPath(string fieldPath, string prefix, string suffix, out int ordinal)
    {
        ordinal = 0;
        return fieldPath.StartsWith(prefix, StringComparison.Ordinal)
            && fieldPath.EndsWith(suffix, StringComparison.Ordinal)
            && fieldPath.Length > prefix.Length + suffix.Length
            && int.TryParse(
                fieldPath.AsSpan(prefix.Length, fieldPath.Length - prefix.Length - suffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ordinal);
    }

    private static Source2RootSphereAggregationDiagnostic Unsupported(string reasonCode) =>
        new(DiagnosticSchemaVersion, RelationName, EvidenceScopeName, StatusUnsupported, reasonCode, []);
}

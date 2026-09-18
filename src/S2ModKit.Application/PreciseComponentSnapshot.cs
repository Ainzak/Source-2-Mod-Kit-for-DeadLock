using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal sealed record PreciseObservedDrawCall(
    string MaterialPath,
    SelectedDrawCall Selection);

internal sealed record PreciseObservedMesh(
    int Lod,
    string ResourcePath,
    int MeshOrdinal,
    int ResourceBlockIndex,
    ContentHash ImmutableSemanticHash,
    MechanicalMeshLineage? Lineage,
    IReadOnlyList<PreciseObservedDrawCall> DrawCalls);

internal sealed record PreciseComponentSnapshot(
    IReadOnlyList<int> Lods,
    IReadOnlyList<PreciseObservedMesh> Meshes,
    IReadOnlyList<PreciseObservedDrawCall> DrawCalls,
    IReadOnlyList<ComponentLineageDiagnostic> LineageDiagnostics)
{
    public static PreciseComponentSnapshot Read(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Lods.Count == 0)
        {
            throw SnapshotError(
                "COMPONENT_MODEL_EMPTY",
                "The inspected model contains no LODs.",
                "Use a supported compiled model with at least one LOD and draw call.");
        }

        var lods = model.Lods.Select(lod => lod.Level).Order().ToArray();
        if (lods.Any(lod => lod < 0) || lods.Distinct().Count() != lods.Length)
        {
            throw SnapshotError(
                "COMPONENT_LOD_IDENTITY_DUPLICATE",
                "The inspected model contains a negative or duplicate LOD identity.",
                "Reject the inconsistent inspection result and re-inspect the input.");
        }

        var meshes = new List<PreciseObservedMesh>();
        var drawCalls = new List<PreciseObservedDrawCall>();
        var diagnostics = new List<ComponentLineageDiagnostic>();
        var meshLocations = new HashSet<(int Lod, string ResourcePath, int MeshOrdinal)>();
        var drawCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lod in model.Lods.OrderBy(lod => lod.Level))
        {
            foreach (var mesh in lod.Meshes
                .OrderBy(mesh => mesh.ResourcePath, StringComparer.Ordinal)
                .ThenBy(mesh => mesh.MeshOrdinal)
                .ThenBy(mesh => mesh.ResourceBlockIndex))
            {
                string resourcePath;
                try
                {
                    resourcePath = StableIdentity.NormalizePath(mesh.ResourcePath);
                }
                catch (ArgumentException exception)
                {
                    throw new S2ModKitException(
                        new S2Error(
                            "COMPONENT_RESOURCE_IDENTITY_INVALID",
                            "component_discovery",
                            $"LOD {lod.Level.ToString(CultureInfo.InvariantCulture)} contains an invalid mesh resource path.",
                            "Reject the inconsistent inspection result and re-inspect the input.",
                            ErrorCategory.UnsupportedCapability),
                        exception);
                }

                if (mesh.MeshOrdinal < 0
                    || mesh.ResourceBlockIndex < 0
                    || string.IsNullOrWhiteSpace(mesh.ImmutableSemanticHash.Value)
                    || !meshLocations.Add((lod.Level, resourcePath, mesh.MeshOrdinal)))
                {
                    throw SnapshotError(
                        "COMPONENT_MESH_IDENTITY_DUPLICATE",
                        $"LOD {lod.Level.ToString(CultureInfo.InvariantCulture)} contains an invalid or duplicate mesh identity.",
                        "Reject the inconsistent inspection result and re-inspect the input.");
                }

                var observedCalls = new List<PreciseObservedDrawCall>();
                var drawCallOrdinals = new HashSet<int>();
                foreach (var drawCall in mesh.DrawCalls
                    .OrderBy(call => call.DrawCallOrdinal)
                    .ThenBy(call => call.Id, StringComparer.Ordinal))
                {
                    string materialPath;
                    try
                    {
                        materialPath = StableIdentity.NormalizePath(drawCall.MaterialPath);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new S2ModKitException(
                            new S2Error(
                                "COMPONENT_MATERIAL_IDENTITY_INVALID",
                                "component_discovery",
                                $"Draw call '{drawCall.Id}' has an invalid material path.",
                                "Reject the inconsistent inspection result and re-inspect the input.",
                                ErrorCategory.UnsupportedCapability),
                            exception);
                    }

                    if (string.IsNullOrWhiteSpace(drawCall.Id)
                        || !drawCallIds.Add(drawCall.Id)
                        || drawCall.DrawCallOrdinal < 0
                        || !drawCallOrdinals.Add(drawCall.DrawCallOrdinal)
                        || drawCall.IndexStart < 0
                        || drawCall.IndexCount <= 0)
                    {
                        throw SnapshotError(
                            "COMPONENT_DRAW_CALL_IDENTITY_DUPLICATE",
                            $"The inspected model contains an invalid or duplicate draw-call identity '{drawCall.Id}'.",
                            "Reject the inconsistent inspection result and re-inspect the input.");
                    }

                    var observed = new PreciseObservedDrawCall(
                        materialPath,
                        new SelectedDrawCall(
                            lod.Level,
                            resourcePath,
                            mesh.MeshOrdinal,
                            mesh.ResourceBlockIndex,
                            drawCall.Id,
                            materialPath,
                            drawCall.DrawCallOrdinal,
                            drawCall.IndexStart,
                            drawCall.IndexCount));
                    observedCalls.Add(observed);
                    drawCalls.Add(observed);
                }

                MechanicalMeshLineage? lineage = null;
                if (mesh.MechanicalLineage is not null)
                {
                    if (mesh.MechanicalLineage.IsCanonical())
                    {
                        lineage = mesh.MechanicalLineage;
                    }
                    else
                    {
                        diagnostics.Add(new ComponentLineageDiagnostic(
                            string.IsNullOrWhiteSpace(mesh.MechanicalLineage.Key) ? null : mesh.MechanicalLineage.Key,
                            "COMPONENT_LINEAGE_FACT_INVALID",
                            $"LOD {lod.Level.ToString(CultureInfo.InvariantCulture)} mesh {mesh.MeshOrdinal.ToString(CultureInfo.InvariantCulture)} has a non-canonical mechanical lineage fact.",
                            [lod.Level]));
                    }
                }
                else
                {
                    diagnostics.Add(new ComponentLineageDiagnostic(
                        null,
                        "COMPONENT_LINEAGE_FACT_MISSING",
                        $"LOD {lod.Level.ToString(CultureInfo.InvariantCulture)} mesh {mesh.MeshOrdinal.ToString(CultureInfo.InvariantCulture)} has no mechanical lineage fact.",
                        [lod.Level]));
                }

                meshes.Add(new PreciseObservedMesh(
                    lod.Level,
                    resourcePath,
                    mesh.MeshOrdinal,
                    mesh.ResourceBlockIndex,
                    mesh.ImmutableSemanticHash,
                    lineage,
                    observedCalls));
            }
        }

        if (drawCalls.Count == 0)
        {
            throw SnapshotError(
                "COMPONENT_MODEL_EMPTY",
                "The inspected model contains no draw calls.",
                "Use a supported compiled model with at least one material draw call.");
        }

        return new PreciseComponentSnapshot(lods, meshes, drawCalls, diagnostics);
    }

    private static S2ModKitException SnapshotError(string code, string summary, string remediation) =>
        new(new S2Error(code, "component_discovery", summary, remediation, ErrorCategory.UnsupportedCapability));
}

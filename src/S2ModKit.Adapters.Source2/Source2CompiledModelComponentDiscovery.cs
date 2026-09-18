using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IComponentCapabilityAnalyzer
{
    private const string CapabilityAnalyzerName = "source2_transform_profile";
    private const string CapabilityAnalyzerVersion = "2";
    private const string CapabilityComponentVersionKey = "s2modkit.source2.component_capability";
    private const string CapabilityComponentVersionValue = "2";
    private const string TransformCapabilityOperation = "transform_component";
    private const int TransformCapabilityOperationVersion = 1;

    string IComponentCapabilityAnalyzer.AnalyzerName => CapabilityAnalyzerName;

    string IComponentCapabilityAnalyzer.AnalyzerVersion => CapabilityAnalyzerVersion;

    IReadOnlyDictionary<string, string> IComponentCapabilityAnalyzer.ComponentVersions
    {
        get
        {
            var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in ComponentVersions)
            {
                versions.Add(entry.Key, entry.Value);
            }

            versions.Add(CapabilityComponentVersionKey, CapabilityComponentVersionValue);
            return versions;
        }
    }

    bool IComponentCapabilityAnalyzer.CanAnalyze(ArtifactContent input, ModelSnapshot model) =>
        input is not null
        && model is not null
        && CanInspect(input);

    Task<IReadOnlyList<ComponentCapabilityAnalysis>> IComponentCapabilityAnalyzer.AnalyzeAsync(
        ComponentCapabilityAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Input is null || request.Model is null || request.Selections is null)
        {
            throw new ArgumentException("A complete component capability analysis request is required.", nameof(request));
        }

        ValidateCapabilityRequest(request.Input, request.Model);
        ValidateSelectionIdentities(request.Selections);
        if (request.Selections.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>([]);
        }

        using var parsed = Parse(request.Input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(request.Model, parsed.Snapshot);
        return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(
            AssessSelections(request.Selections, CreateCapabilityProfile(request.Input, parsed), cancellationToken));
    }

    private static void ValidateCapabilityRequest(ArtifactContent input, ModelSnapshot model)
    {
        string inputPath;
        string modelPath;
        try
        {
            inputPath = StableIdentity.NormalizePath(input.LogicalPath);
            modelPath = StableIdentity.NormalizePath(model.Artifact.LogicalPath);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error(
                    "INPUT_HASH_DRIFT",
                    "input",
                    "The capability analysis request has an invalid model identity.",
                    "Reload the immutable project input and inspect it again.",
                    ErrorCategory.InputOrResolution),
                exception);
        }

        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash
            || input.ContentHash != model.Artifact.ContentHash
            || input.Bytes.Length != model.Artifact.Size
            || !string.Equals(inputPath, modelPath, StringComparison.Ordinal))
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The capability analysis request no longer matches its immutable input.", "Reload the input and rerun component discovery.");
        }
    }

    private static ComponentCapabilityProfile CreateCapabilityProfile(ArtifactContent input, ParsedModel parsed)
    {
        var resourcePath = StableIdentity.NormalizePath(input.LogicalPath);
        var meshes = parsed.MeshesByOrdinal.Values
            .OrderBy(mesh => mesh.Lod)
            .ThenBy(mesh => mesh.MeshOrdinal)
            .Select(mesh => new ComponentProfileMesh(
                mesh.MeshOrdinal,
                mesh.Lod,
                mesh.BlockIndex,
                resourcePath,
                mesh.DrawCalls.Select(item => item.Snapshot).ToArray(),
                mesh.Geometry.Status,
                mesh.GeometryAnalysis is null ? null : mesh.Descriptor,
                mesh.GeometryAnalysis is null ? null : mesh.Block.Data,
                mesh.GeometryAnalysis,
                mesh.RawMbufAnalysis,
                mesh.PhysicsAnalysis,
                mesh.WholeMeshTransformAnalysis))
            .ToArray();
        var presentLods = parsed.Snapshot.Lods.Select(lod => lod.Level).Order().ToArray();
        return new ComponentCapabilityProfile(presentLods, resourcePath, meshes);
    }

    internal static IReadOnlyList<ComponentCapabilityAnalysis> AssessSelections(
        IReadOnlyList<ComponentCapabilitySelection> selections,
        ComponentCapabilityProfile profile,
        CancellationToken cancellationToken)
    {
        ValidateSelectionIdentities(selections);

        var results = new List<ComponentCapabilityAnalysis>(selections.Count);
        foreach (var selection in selections.OrderBy(item => item.SelectionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(AssessSelection(selection, profile));
        }

        return results;
    }

    private static void ValidateSelectionIdentities(IReadOnlyList<ComponentCapabilitySelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (selection is null
                || string.IsNullOrWhiteSpace(selection.SelectionId)
                || !seen.Add(selection.SelectionId))
            {
                throw Errors.Selection(
                    "TRANSFORM_SELECTION_INVALID",
                    "The capability analysis request contains an empty or duplicate selection identity.",
                    "Request one assessment per uniquely identified candidate selection.");
            }
        }
    }

    private static ComponentCapabilityAnalysis AssessSelection(
        ComponentCapabilitySelection selection,
        ComponentCapabilityProfile profile)
    {
        var identityIssue = ValidateSelectionIdentity(selection);
        if (identityIssue is not null)
        {
            return Assessment(selection.SelectionId, ComponentDiscoveryContract.Ambiguous, identityIssue);
        }

        var mapping = MapSelectionToMeshes(selection, profile);
        if (mapping.Issue is not null)
        {
            return Assessment(selection.SelectionId, ComponentDiscoveryContract.Ambiguous, mapping.Issue);
        }

        var structuralIssues = CollectStructuralIssues(mapping.SelectedMeshes, profile.PresentLods);
        if (structuralIssues.Count > 0)
        {
            return Assessment(selection.SelectionId, ComponentDiscoveryContract.Unsupported, structuralIssues);
        }

        if (mapping.SelectedMeshes.Count == 1
            && mapping.SelectedMeshes[0].Mesh is
            {
                RawMbufAnalysis: not null,
                PhysicsAnalysis: not null,
                WholeMeshTransformAnalysis: not null,
            } coupledMesh)
        {
            var metadata = coupledMesh.WholeMeshTransformAnalysis!;
            return new ComponentCapabilityAnalysis(
                selection.SelectionId,
                TransformCapabilityOperation,
                2,
                ComponentDiscoveryContract.Available,
                [Reason(
                    "COMPONENT_CAPABILITY_AVAILABLE",
                    "The complete component satisfies the atomic raw-MBUF/one-convex-PHYS transform profile.")],
                [new ComponentGeometryLodFacts(
                    coupledMesh.Lod,
                    metadata.VertexCount,
                    metadata.VertexSetHash,
                    true)]);
        }

        var analyzed = new List<(ComponentProfileMesh Mesh, Source2WholeMeshTransformAnalysis Analysis)>(mapping.SelectedMeshes.Count);
        foreach (var mapped in mapping.SelectedMeshes)
        {
            var failure = AssessMeshProfile(selection.SelectionId, mapped.Mesh, out var analysis);
            if (failure is not null)
            {
                return failure;
            }

            analyzed.Add((mapped.Mesh, analysis!));
        }

        var roots = analyzed.Select(item => item.Analysis.LocalSkinningRootBone).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1)
        {
            return Assessment(
                selection.SelectionId,
                ComponentDiscoveryContract.Unsupported,
                Reason(
                    "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED",
                    "The selected meshes use different local skinning roots across LODs."));
        }

        return new ComponentCapabilityAnalysis(
            selection.SelectionId,
            TransformCapabilityOperation,
            TransformCapabilityOperationVersion,
            ComponentDiscoveryContract.Available,
            [Reason(
                "COMPONENT_CAPABILITY_AVAILABLE",
                "One complete mesh in every present LOD satisfies the characterized whole-mesh transform profile.")],
            analyzed
                .OrderBy(item => item.Mesh.Lod)
                .Select(item => new ComponentGeometryLodFacts(
                    item.Mesh.Lod,
                    item.Analysis.VertexCount,
                    item.Analysis.VertexSetHash,
                    true))
                .ToArray());
    }

    private static ComponentCapabilityReason? ValidateSelectionIdentity(ComponentCapabilitySelection selection)
    {
        if (selection.CandidateKind is not (ComponentDiscoveryV2Contract.MaterialGroupKind
            or ComponentDiscoveryV2Contract.MeshLineageKind
            or ComponentDiscoveryV2Contract.CandidateUnionKind))
        {
            return Reason("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", "The selection kind is not material_group, mesh_lineage, or candidate_union.");
        }

        if (selection.MaterialPaths is null || selection.MaterialPaths.Count == 0)
        {
            return Reason("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", "The selection contains no material paths.");
        }

        string[] materials;
        try
        {
            materials = selection.MaterialPaths
                .Select(path => StableIdentity.NormalizePath(path ?? string.Empty))
                .ToArray();
        }
        catch (ArgumentException)
        {
            materials = [];
        }

        if (materials.Length != selection.MaterialPaths.Count
            || materials.Where((material, index) => !string.Equals(material, selection.MaterialPaths[index], StringComparison.Ordinal)).Any()
            || materials.Distinct(StringComparer.Ordinal).Count() != materials.Length
            || !materials.SequenceEqual(materials.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return Reason("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", "Selection material paths must be non-empty, normalized, unique, and ordinally sorted.");
        }

        if (selection.SelectedDrawCalls is null || selection.SelectedDrawCalls.Count == 0)
        {
            return Reason("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", "The selection contains no draw calls.");
        }

        var identities = selection.SelectedDrawCalls
            .Select(item => item?.DrawCallId)
            .ToArray();
        if (identities.Any(id => string.IsNullOrWhiteSpace(id))
            || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
        {
            return Reason("COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS", "The selection draw-call identities are empty or duplicated.");
        }

        return null;
    }

    private static ComponentSelectionMapping MapSelectionToMeshes(
        ComponentCapabilitySelection selection,
        ComponentCapabilityProfile profile)
    {
        var byId = new Dictionary<string, (ComponentProfileMesh Mesh, DrawCallSnapshot Call)>(StringComparer.Ordinal);
        foreach (var mesh in profile.Meshes)
        {
            if (!string.Equals(mesh.ResourcePath, profile.ResourcePath, StringComparison.Ordinal))
            {
                return new ComponentSelectionMapping(
                    Reason(
                        "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                        "A parsed mesh does not belong to the capability profile's normalized resource path."),
                    []);
            }

            foreach (var drawCall in mesh.DrawCalls)
            {
                if (!byId.TryAdd(drawCall.Id, (mesh, drawCall)))
                {
                    return new ComponentSelectionMapping(
                        Reason(
                            "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                            $"Draw-call identity '{drawCall.Id}' does not map to exactly one freshly parsed draw call."),
                        []);
                }
            }
        }

        var mapped = new Dictionary<int, List<DrawCallSnapshot>>();
        var selectionMaterials = selection.MaterialPaths.ToHashSet(StringComparer.Ordinal);
        var observedMaterials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selection.SelectedDrawCalls)
        {
            string resourcePath;
            string materialPath;
            try
            {
                resourcePath = StableIdentity.NormalizePath(item.ResourcePath ?? string.Empty);
                materialPath = StableIdentity.NormalizePath(item.MaterialPath ?? string.Empty);
            }
            catch (ArgumentException)
            {
                resourcePath = string.Empty;
                materialPath = string.Empty;
            }

            if (!byId.TryGetValue(item.DrawCallId, out var found))
            {
                return new ComponentSelectionMapping(
                    Reason(
                        "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                        $"Draw call '{item.DrawCallId}' is not present in the freshly parsed model."),
                    []);
            }

            var (mesh, call) = found;
            if (mesh.MeshOrdinal != item.MeshOrdinal
                || mesh.Lod != item.Lod
                || mesh.BlockIndex != item.ResourceBlockIndex
                || !string.Equals(mesh.ResourcePath, resourcePath, StringComparison.Ordinal)
                || !selectionMaterials.Contains(materialPath)
                || !Matches(call, item))
            {
                return new ComponentSelectionMapping(
                    Reason(
                        "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                        $"Draw call '{item.DrawCallId}' no longer matches its parsed mesh, LOD, block, material, or index range."),
                    []);
            }

            observedMaterials.Add(materialPath);

            if (!mapped.TryGetValue(mesh.MeshOrdinal, out var calls))
            {
                calls = [];
                mapped.Add(mesh.MeshOrdinal, calls);
            }

            calls.Add(call);
        }

        if (!observedMaterials.SetEquals(selectionMaterials))
        {
            return new ComponentSelectionMapping(
                Reason(
                    "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                    "The selected draw calls do not cover every declared material path exactly."),
                []);
        }

        var selectedMeshes = profile.Meshes
            .Where(mesh => mapped.ContainsKey(mesh.MeshOrdinal))
            .Select(mesh => new ComponentMappedMesh(mesh, mapped[mesh.MeshOrdinal]))
            .ToArray();
        return new ComponentSelectionMapping(null, selectedMeshes);
    }

    private static List<ComponentCapabilityReason> CollectStructuralIssues(
        IReadOnlyList<ComponentMappedMesh> selectedMeshes,
        IReadOnlyList<int> presentLods)
    {
        List<ComponentCapabilityReason> issues = [];
        foreach (var lod in presentLods.Order())
        {
            var count = selectedMeshes.Count(item => item.Mesh.Lod == lod);
            if (count == 0)
            {
                issues.Add(Reason(
                    "COMPONENT_INCOMPLETE_LOD_COVERAGE",
                    "The selection has no candidate mesh in one or more present LODs required by all_present."));
            }
            else if (count > 1)
            {
                issues.Add(Reason(
                    "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED",
                    "The selection maps to more than one embedded mesh in at least one LOD."));
            }
        }

        foreach (var mapped in selectedMeshes)
        {
            var selectedIds = mapped.SelectedDrawCalls.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (selectedIds.SetEquals(mapped.Mesh.DrawCalls.Select(item => item.Id)))
            {
                continue;
            }

            issues.Add(
                mapped.Mesh.GeometryAnalysis is not null && SharesVertexWithUnselected(mapped.Mesh, selectedIds)
                    ? Reason(
                        "TRANSFORM_SHARED_VERTEX_OWNERSHIP",
                        "Selected vertices are shared with draw calls outside the selection in the same mesh.")
                    : Reason(
                        "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED",
                        "The selection covers only part of one embedded mesh; the whole-mesh profile requires every draw call in the mesh."));
        }

        return issues
            .GroupBy(reason => reason.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static bool SharesVertexWithUnselected(ComponentProfileMesh mesh, HashSet<string> selectedIds)
    {
        var selectedVertices = new HashSet<(int Buffer, int Vertex)>();
        var unselectedVertices = new HashSet<(int Buffer, int Vertex)>();
        foreach (var call in mesh.GeometryAnalysis!.DrawCalls)
        {
            var target = selectedIds.Contains(call.Snapshot.DrawCallId) ? selectedVertices : unselectedVertices;
            foreach (var vertex in call.VertexIndices)
            {
                target.Add((call.Snapshot.VertexBufferOrdinal, vertex));
            }
        }

        selectedVertices.IntersectWith(unselectedVertices);
        return selectedVertices.Count > 0;
    }

    private static ComponentCapabilityAnalysis? AssessMeshProfile(
        string selectionId,
        ComponentProfileMesh mesh,
        out Source2WholeMeshTransformAnalysis? analysis)
    {
        analysis = null;
        if (string.Equals(mesh.GeometryStatus, "unavailable", StringComparison.Ordinal))
        {
            return Assessment(
                selectionId,
                ComponentDiscoveryContract.Blocked,
                Reason(
                    "MESHOPTIMIZER_CAPABILITY_UNAVAILABLE",
                    "The configured geometry codec is unavailable, so decoded geometry cannot be retained for this mesh."));
        }

        if (!string.Equals(mesh.GeometryStatus, "ready", StringComparison.Ordinal)
            || mesh.Descriptor is null
            || mesh.MeshData is null
            || mesh.GeometryAnalysis is null)
        {
            return Assessment(
                selectionId,
                ComponentDiscoveryContract.Unsupported,
                Reason(
                    "TRANSFORM_GEOMETRY_UNAVAILABLE",
                    "The decoded geometry of this mesh does not satisfy the characterized read-only analysis profile."));
        }

        try
        {
            analysis = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
                mesh.Descriptor,
                mesh.MeshData,
                mesh.GeometryAnalysis,
                $"embedded mesh {mesh.MeshOrdinal.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or IndexOutOfRangeException
            or KeyNotFoundException)
        {
            return Assessment(
                selectionId,
                ComponentDiscoveryContract.Unsupported,
                Reason(
                    "TRANSFORM_GEOMETRY_UNAVAILABLE",
                    $"The decoded mesh does not satisfy the characterized whole-mesh profile: {exception.Message}"));
        }
    }

    private static ComponentCapabilityAnalysis Assessment(
        string selectionId,
        string availability,
        ComponentCapabilityReason reason) => new(
            selectionId,
            TransformCapabilityOperation,
            TransformCapabilityOperationVersion,
            availability,
            [reason],
            []);

    private static ComponentCapabilityAnalysis Assessment(
        string selectionId,
        string availability,
        IReadOnlyList<ComponentCapabilityReason> reasons) => new(
            selectionId,
            TransformCapabilityOperation,
            TransformCapabilityOperationVersion,
            availability,
            reasons,
            []);

    private static ComponentCapabilityReason Reason(string code, string summary) => new(code, summary);

    private sealed record ComponentMappedMesh(
        ComponentProfileMesh Mesh,
        IReadOnlyList<DrawCallSnapshot> SelectedDrawCalls);

    private sealed record ComponentSelectionMapping(
        ComponentCapabilityReason? Issue,
        IReadOnlyList<ComponentMappedMesh> SelectedMeshes);
}

internal sealed record ComponentProfileMesh(
    int MeshOrdinal,
    int Lod,
    int BlockIndex,
    string ResourcePath,
    IReadOnlyList<DrawCallSnapshot> DrawCalls,
    string GeometryStatus,
    KVObject? Descriptor,
    KVObject? MeshData,
    Source2GeometryAnalysis? GeometryAnalysis,
    Source2RawMbufAnalysis? RawMbufAnalysis = null,
    Source2ConvexPhysAnalysis? PhysicsAnalysis = null,
    Source2WholeMeshTransformAnalysis? WholeMeshTransformAnalysis = null);

internal sealed record ComponentCapabilityProfile(
    IReadOnlyList<int> PresentLods,
    string ResourcePath,
    IReadOnlyList<ComponentProfileMesh> Meshes);

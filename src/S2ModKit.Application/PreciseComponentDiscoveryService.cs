using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed class PreciseComponentDiscoveryService(IComponentCapabilityAnalyzer? capabilityAnalyzer = null)
{
    private const string RemoveOperation = "remove_component";
    private const string TransformOperation = "transform_component";
    private const int OperationVersion = 1;

    public async Task<ComponentDiscoveryResultV2> DiscoverAsync(
        ArtifactContent input,
        ModelSnapshot model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();

        var modelIdentity = ValidateInput(input, model);
        var snapshot = PreciseComponentSnapshot.Read(model);
        var grouped = Group(modelIdentity, snapshot);
        var analyzerIdentity = CreateAnalyzerIdentity(capabilityAnalyzer);
        var analyses = await AnalyzeTransformCapabilitiesAsync(
            input,
            model,
            grouped.Candidates,
            cancellationToken).ConfigureAwait(false);
        var affineAnalyses = await AnalyzeAffineCapabilitiesAsync(
            input,
            model,
            grouped.Candidates,
            cancellationToken).ConfigureAwait(false);

        var candidates = grouped.Candidates
            .Select(draft => CreateCandidate(draft, analyses, affineAnalyses))
            .OrderBy(CandidateKindRank)
            .ThenBy(CandidateSortKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();
        var fingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(
            modelIdentity,
            analyzerIdentity,
            candidates,
            grouped.Diagnostics);
        return new ComponentDiscoveryResultV2(
            ComponentDiscoveryV2Contract.SchemaVersion,
            modelIdentity,
            fingerprint,
            analyzerIdentity,
            candidates,
            grouped.Diagnostics);
    }

    private static ComponentModelIdentity ValidateInput(ArtifactContent input, ModelSnapshot model)
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
                    "COMPONENT_INPUT_IDENTITY_INVALID",
                    "component_discovery",
                    "Precise component discovery received an invalid model logical path.",
                    "Reload the immutable project input and inspect it again.",
                    ErrorCategory.InputOrResolution),
                exception);
        }

        var computedHash = ContentHash.Compute(input.Bytes.Span);
        if (computedHash != input.ContentHash
            || input.ContentHash != model.Artifact.ContentHash
            || !string.Equals(inputPath, modelPath, StringComparison.Ordinal)
            || input.Bytes.Length != model.Artifact.Size)
        {
            throw DiscoveryError(
                "COMPONENT_INPUT_DRIFT",
                "The input bytes and inspected model identity do not match.",
                "Reload the immutable project input and inspect it again.",
                ErrorCategory.InputOrResolution);
        }

        return new ComponentModelIdentity(modelPath, input.ContentHash, input.Bytes.Length);
    }

    private static ComponentGroupingResult Group(
        ComponentModelIdentity model,
        PreciseComponentSnapshot snapshot)
    {
        var candidates = new List<ComponentCandidateDraft>();
        foreach (var materialGroup in snapshot.DrawCalls
            .GroupBy(item => item.MaterialPath, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var selected = materialGroup.Select(item => item.Selection).ToArray();
            var lods = snapshot.Lods.Select(lod =>
            {
                var ids = selected.Where(item => item.Lod == lod)
                    .Select(item => item.DrawCallId)
                    .ToArray();
                return new ComponentCandidateLod(lod, ids, ids.Length);
            }).ToArray();
            candidates.Add(ComponentCandidateDraft.Material(
                ComponentCandidateIdentity.ComputeMaterialCandidateId(model, materialGroup.Key, lods),
                model,
                materialGroup.Key,
                lods,
                selected));
        }

        var diagnostics = new List<ComponentLineageDiagnostic>(snapshot.LineageDiagnostics);
        foreach (var lineageGroup in snapshot.Meshes
            .Where(mesh => mesh.Lineage is not null)
            .GroupBy(mesh => mesh.Lineage!.Key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var members = lineageGroup.OrderBy(mesh => mesh.Lod).ThenBy(mesh => mesh.MeshOrdinal).ToArray();
            var observedLods = members.Select(mesh => mesh.Lod).Distinct().Order().ToArray();
            if (snapshot.LineageDiagnostics.Any(diagnostic =>
                string.Equals(diagnostic.LineageKey, lineageGroup.Key, StringComparison.Ordinal)))
            {
                continue;
            }

            var duplicateLod = members.GroupBy(mesh => mesh.Lod).FirstOrDefault(group => group.Count() != 1);
            if (duplicateLod is not null)
            {
                diagnostics.Add(Diagnostic(
                    lineageGroup.Key,
                    "COMPONENT_LINEAGE_DUPLICATE_LOD",
                    $"Mechanical lineage '{lineageGroup.Key}' maps to more than one mesh in LOD {duplicateLod.Key.ToString(CultureInfo.InvariantCulture)}.",
                    observedLods));
                continue;
            }

            if (!observedLods.SequenceEqual(snapshot.Lods))
            {
                diagnostics.Add(Diagnostic(
                    lineageGroup.Key,
                    "COMPONENT_LINEAGE_INCOMPLETE_LOD_COVERAGE",
                    $"Mechanical lineage '{lineageGroup.Key}' does not identify one mesh in every present LOD.",
                    observedLods));
                continue;
            }

            var sourceLabels = members.Select(mesh => mesh.Lineage!.SourceLabel)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sourceLabels.Length != 1)
            {
                diagnostics.Add(Diagnostic(
                    lineageGroup.Key,
                    "COMPONENT_LINEAGE_SOURCE_LABEL_CONFLICT",
                    $"Mechanical lineage '{lineageGroup.Key}' has conflicting source labels across LODs.",
                    observedLods));
                continue;
            }

            if (members.Any(mesh => mesh.DrawCalls.Count == 0))
            {
                diagnostics.Add(Diagnostic(
                    lineageGroup.Key,
                    "COMPONENT_LINEAGE_EMPTY_MESH",
                    $"Mechanical lineage '{lineageGroup.Key}' contains a mesh without draw calls.",
                    observedLods));
                continue;
            }

            var lods = members.Select(mesh => new MeshLineageCandidateLod(
                mesh.Lod,
                mesh.ResourcePath,
                mesh.MeshOrdinal,
                mesh.ResourceBlockIndex,
                mesh.ImmutableSemanticHash,
                mesh.Lineage!.SourceName,
                mesh.DrawCalls.Select(call => call.MaterialPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                mesh.DrawCalls.Select(call => call.Selection.DrawCallId).ToArray(),
                mesh.DrawCalls.Count)).ToArray();
            var materials = lods.SelectMany(lod => lod.MaterialPaths)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var selected = members.SelectMany(mesh => mesh.DrawCalls).Select(call => call.Selection).ToArray();
            candidates.Add(ComponentCandidateDraft.Lineage(
                ComponentCandidateIdentity.ComputeLineageCandidateId(model, lineageGroup.Key, sourceLabels[0], lods),
                model,
                lineageGroup.Key,
                sourceLabels[0],
                materials,
                lods,
                selected));
        }

        var duplicateCandidate = candidates.GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateCandidate is not null)
        {
            throw DiscoveryError(
                "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                $"Multiple precise component groups produced candidate identity '{duplicateCandidate.Key}'.",
                "Reject the discovery result; candidate identities must map to exactly one mechanical selection.");
        }

        return new ComponentGroupingResult(
            candidates
                .OrderBy(candidate => candidate.Kind == ComponentDiscoveryV2Contract.MaterialGroupKind ? 0 : 1)
                .ThenBy(candidate => candidate.SortKey, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToArray(),
            diagnostics
                .OrderBy(item => item.LineageKey ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => string.Join(',', item.ObservedLods), StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<IReadOnlyDictionary<string, ComponentCapabilityAnalysis>> AnalyzeTransformCapabilitiesAsync(
        ArtifactContent input,
        ModelSnapshot model,
        IReadOnlyList<ComponentCandidateDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (capabilityAnalyzer is null || !capabilityAnalyzer.CanAnalyze(input, model))
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var complete = drafts.Where(draft => draft.IsLodComplete).ToArray();
        if (complete.Length == 0)
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var selections = complete.Select(draft => new ComponentCapabilitySelection(
            draft.CandidateId,
            draft.Kind,
            draft.MaterialPaths,
            draft.SelectedDrawCalls)).ToArray();
        var returned = await capabilityAnalyzer.AnalyzeAsync(
            new ComponentCapabilityAnalysisRequest(input, model, selections),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (returned is null)
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE",
                "The capability analyzer returned no result collection.",
                "Reject the analyzer output and rerun with complete deterministic coverage.");
        }

        var expected = complete.ToDictionary(draft => draft.CandidateId, StringComparer.Ordinal);
        var analyses = new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        foreach (var analysis in returned)
        {
            if (analysis is null
                || !expected.TryGetValue(analysis.SelectionId, out var draft)
                || !analyses.TryAdd(analysis.SelectionId, analysis))
            {
                throw DiscoveryError(
                    "COMPONENT_CAPABILITY_ANALYSIS_AMBIGUOUS",
                    $"The capability analyzer returned an unknown, null, or duplicate precise selection '{analysis?.SelectionId}'.",
                    "Reject the analyzer output and rerun with one result per requested selection.");
            }

            ValidateAnalysis(analysis, model.Lods.Select(lod => lod.Level).Order().ToArray(), 1, 2);
        }

        var missing = expected.Keys.Except(analyses.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE",
                $"The capability analyzer omitted {missing.Length.ToString(CultureInfo.InvariantCulture)} requested precise selection(s).",
                "Reject the analyzer output and rerun with complete deterministic coverage.");
        }

        return analyses;
    }

    private async Task<IReadOnlyDictionary<string, ComponentCapabilityAnalysis>> AnalyzeAffineCapabilitiesAsync(
        ArtifactContent input,
        ModelSnapshot model,
        IReadOnlyList<ComponentCandidateDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (capabilityAnalyzer is not IAffineComponentCapabilityAnalyzer affineAnalyzer
            || !capabilityAnalyzer.CanAnalyze(input, model))
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var complete = drafts.Where(draft => draft.IsLodComplete).ToArray();
        if (complete.Length == 0)
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var selections = complete.Select(draft => new ComponentCapabilitySelection(
            draft.CandidateId,
            draft.Kind,
            draft.MaterialPaths,
            draft.SelectedDrawCalls)).ToArray();
        var returned = await affineAnalyzer.AnalyzeAffineAsync(
            new ComponentCapabilityAnalysisRequest(input, model, selections),
            cancellationToken).ConfigureAwait(false);
        if (returned is null)
        {
            throw DiscoveryError("COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE", "The affine analyzer returned no results.", "Reject the incomplete analyzer output.");
        }

        var expected = complete.Select(draft => draft.CandidateId).ToHashSet(StringComparer.Ordinal);
        var analyses = new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        foreach (var analysis in returned)
        {
            if (analysis is null || !expected.Contains(analysis.SelectionId)
                || !analyses.TryAdd(analysis.SelectionId, analysis))
            {
                throw DiscoveryError("COMPONENT_CAPABILITY_ANALYSIS_AMBIGUOUS", "The affine analyzer returned an unknown or duplicate selection.", "Reject the inconsistent analyzer output.");
            }

            ValidateAnalysis(analysis, model.Lods.Select(lod => lod.Level).Order().ToArray(), 4);
        }

        if (analyses.Count != expected.Count)
        {
            throw DiscoveryError("COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE", "The affine analyzer omitted a requested selection.", "Reject the incomplete analyzer output.");
        }

        return analyses;
    }

    private ComponentCandidateV2 CreateCandidate(
        ComponentCandidateDraft draft,
        IReadOnlyDictionary<string, ComponentCapabilityAnalysis> analyses,
        IReadOnlyDictionary<string, ComponentCapabilityAnalysis> affineAnalyses)
    {
        var capabilities = new List<ComponentCapability>
        {
            draft.IsLodComplete
                ? AvailableCapability(RemoveOperation)
                : IncompleteLodCapability(RemoveOperation, "The material group is absent from one or more present LODs required by all_present."),
            CreateTransformCapability(draft, analyses),
        };
        if (capabilityAnalyzer is IAffineComponentCapabilityAnalyzer)
        {
            capabilities.Add(draft.IsLodComplete
                ? CreateAffineCapability(draft, affineAnalyses)
                : new ComponentCapability(
                    TransformOperation,
                    4,
                    ComponentDiscoveryContract.Unsupported,
                    [new ComponentCapabilityReason("COMPONENT_INCOMPLETE_LOD_COVERAGE", "The candidate has no draw call in one or more present LODs.")],
                    []));
        }
        if (draft.Kind == ComponentDiscoveryV2Contract.MaterialGroupKind)
        {
            return new MaterialGroupComponentCandidateV2(
                draft.CandidateId,
                draft.Model,
                draft.MaterialPath!,
                CreateDisplayLabel(draft.MaterialPath!),
                draft.MaterialLods!,
                capabilities);
        }

        return new MeshLineageComponentCandidateV2(
            draft.CandidateId,
            draft.Model,
            draft.LineageKey!,
            draft.SourceLabel!,
            draft.SourceLabel!,
            draft.MaterialPaths,
            draft.LineageLods!,
            capabilities);
    }

    private static ComponentCapability CreateTransformCapability(
        ComponentCandidateDraft draft,
        IReadOnlyDictionary<string, ComponentCapabilityAnalysis> analyses)
    {
        if (!draft.IsLodComplete)
        {
            return IncompleteLodCapability(
                TransformOperation,
                "The material group is absent from one or more present LODs required by all_present.");
        }

        if (!analyses.TryGetValue(draft.CandidateId, out var analysis))
        {
            return new ComponentCapability(
                TransformOperation,
                OperationVersion,
                ComponentDiscoveryContract.Blocked,
                [new ComponentCapabilityReason(
                    "COMPONENT_CAPABILITY_ANALYZER_UNAVAILABLE",
                    "No compatible component capability analyzer is available for this model.")],
                []);
        }

        return new ComponentCapability(
            analysis.OperationKind,
            analysis.OperationVersion,
            analysis.Availability,
            analysis.Reasons.OrderBy(reason => reason.Code, StringComparer.Ordinal)
                .ThenBy(reason => reason.Summary, StringComparer.Ordinal)
                .ToArray(),
            analysis.GeometryByLod.OrderBy(item => item.Lod).ToArray());
    }

    private static ComponentCapability CreateAffineCapability(
        ComponentCandidateDraft draft,
        IReadOnlyDictionary<string, ComponentCapabilityAnalysis> analyses)
    {
        var analysis = analyses[draft.CandidateId];
        return new ComponentCapability(
            analysis.OperationKind,
            analysis.OperationVersion,
            analysis.Availability,
            analysis.Reasons.OrderBy(reason => reason.Code, StringComparer.Ordinal)
                .ThenBy(reason => reason.Summary, StringComparer.Ordinal).ToArray(),
            analysis.GeometryByLod.OrderBy(item => item.Lod).ToArray());
    }

    private static ComponentCapability AvailableCapability(string operationKind) => new(
        operationKind,
        OperationVersion,
        ComponentDiscoveryContract.Available,
        [new ComponentCapabilityReason(
            "COMPONENT_CAPABILITY_AVAILABLE",
            "The candidate has exact membership in every present LOD for this operation.")],
        []);

    private static ComponentCapability IncompleteLodCapability(string operationKind, string summary) => new(
        operationKind,
        OperationVersion,
        ComponentDiscoveryContract.Unsupported,
        [new ComponentCapabilityReason("COMPONENT_INCOMPLETE_LOD_COVERAGE", summary)],
        []);

    private static void ValidateAnalysis(
        ComponentCapabilityAnalysis analysis,
        IReadOnlyList<int> presentLods,
        params int[] allowedVersions)
    {
        if (!string.Equals(analysis.OperationKind, TransformOperation, StringComparison.Ordinal)
            || !allowedVersions.Contains(analysis.OperationVersion))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_OPERATION_INVALID",
                $"Selection '{analysis.SelectionId}' returned an unexpected operation contract.",
                "Return exactly one published transform_component assessment for every requested selection.");
        }

        if (analysis.Availability is not (ComponentDiscoveryContract.Available
            or ComponentDiscoveryContract.Blocked
            or ComponentDiscoveryContract.Unsupported
            or ComponentDiscoveryContract.Ambiguous))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_AVAILABILITY_INVALID",
                $"Selection '{analysis.SelectionId}' returned unknown availability '{analysis.Availability}'.",
                "Use a supported component availability value.");
        }

        if (analysis.Reasons is null
            || analysis.Reasons.Count == 0
            || analysis.Reasons.Any(reason => reason is null || !IsReasonCode(reason.Code) || string.IsNullOrWhiteSpace(reason.Summary))
            || analysis.GeometryByLod is null)
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_REASON_INVALID",
                $"Selection '{analysis.SelectionId}' returned an empty or invalid stable reason or geometry collection.",
                "Return stable reasons and a non-null geometry collection.");
        }

        var geometry = analysis.GeometryByLod.OrderBy(item => item.Lod).ToArray();
        if (geometry.GroupBy(item => item.Lod).Any(group => group.Count() != 1)
            || geometry.Any(item => item.SelectedVertexCount <= 0)
            || geometry.Any(item => string.IsNullOrWhiteSpace(item.VertexSetHash.Value))
            || geometry.Any(item => !presentLods.Contains(item.Lod)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_FACTS_INVALID",
                $"Selection '{analysis.SelectionId}' returned invalid per-LOD geometry facts.",
                "Return unique positive geometry facts only for LODs in the requested selection.");
        }

        if (string.Equals(analysis.Availability, ComponentDiscoveryContract.Available, StringComparison.Ordinal)
            && (!geometry.Select(item => item.Lod).SequenceEqual(presentLods)
                || geometry.Any(item => !item.ExclusivelyOwned)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_FACTS_INVALID",
                $"Selection '{analysis.SelectionId}' is marked available without complete exclusive geometry facts.",
                "Return one exclusively owned geometry fact for every present LOD or mark the capability unavailable.");
        }
    }

    private static ComponentCapabilityAnalyzerIdentity CreateAnalyzerIdentity(IComponentCapabilityAnalyzer? analyzer)
    {
        if (analyzer is null)
        {
            return new ComponentCapabilityAnalyzerIdentity(
                "none",
                "0",
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(analyzer.AnalyzerName)
            || string.IsNullOrWhiteSpace(analyzer.AnalyzerVersion)
            || analyzer.ComponentVersions.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_ANALYZER_IDENTITY_INVALID",
                "The component capability analyzer has an incomplete identity.",
                "Configure an analyzer with stable non-empty name and version facts.");
        }

        var componentVersions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var componentVersion in analyzer.ComponentVersions)
        {
            if (!componentVersions.TryAdd(componentVersion.Key, componentVersion.Value))
            {
                throw DiscoveryError(
                    "COMPONENT_CAPABILITY_ANALYZER_IDENTITY_INVALID",
                    "The component capability analyzer has duplicate identity facts.",
                    "Configure an analyzer with unique component version names.");
            }
        }

        return new ComponentCapabilityAnalyzerIdentity(analyzer.AnalyzerName, analyzer.AnalyzerVersion, componentVersions);
    }

    private static int CandidateKindRank(ComponentCandidateV2 candidate) => candidate switch
    {
        MaterialGroupComponentCandidateV2 => 0,
        MeshLineageComponentCandidateV2 => 1,
        _ => int.MaxValue,
    };

    private static string CandidateSortKey(ComponentCandidateV2 candidate) => candidate switch
    {
        MaterialGroupComponentCandidateV2 material => material.MaterialPath,
        MeshLineageComponentCandidateV2 lineage => lineage.LineageKey,
        _ => candidate.CandidateId,
    };

    private static string CreateDisplayLabel(string materialPath)
    {
        var separator = materialPath.LastIndexOf('/');
        var name = separator < 0 ? materialPath : materialPath[(separator + 1)..];
        if (name.EndsWith(".vmat_c", StringComparison.Ordinal))
        {
            return name[..^7];
        }

        return name.EndsWith(".vmat", StringComparison.Ordinal) ? name[..^5] : name;
    }

    private static bool IsReasonCode(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => character == '_' || char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));

    private static ComponentLineageDiagnostic Diagnostic(
        string? lineageKey,
        string code,
        string summary,
        IReadOnlyList<int> observedLods) => new(lineageKey, code, summary, observedLods);

    private static S2ModKitException DiscoveryError(
        string code,
        string summary,
        string remediation,
        ErrorCategory category = ErrorCategory.UnsupportedCapability) =>
        new(new S2Error(code, "component_discovery", summary, remediation, category));

    private sealed record ComponentGroupingResult(
        IReadOnlyList<ComponentCandidateDraft> Candidates,
        IReadOnlyList<ComponentLineageDiagnostic> Diagnostics);

    private sealed record ComponentCandidateDraft(
        string CandidateId,
        string Kind,
        ComponentModelIdentity Model,
        string SortKey,
        IReadOnlyList<string> MaterialPaths,
        IReadOnlyList<SelectedDrawCall> SelectedDrawCalls,
        bool IsLodComplete,
        string? MaterialPath,
        IReadOnlyList<ComponentCandidateLod>? MaterialLods,
        string? LineageKey,
        string? SourceLabel,
        IReadOnlyList<MeshLineageCandidateLod>? LineageLods)
    {
        public static ComponentCandidateDraft Material(
            string candidateId,
            ComponentModelIdentity model,
            string materialPath,
            IReadOnlyList<ComponentCandidateLod> lods,
            IReadOnlyList<SelectedDrawCall> selected) => new(
                candidateId,
                ComponentDiscoveryV2Contract.MaterialGroupKind,
                model,
                materialPath,
                [materialPath],
                selected,
                lods.All(lod => lod.DrawCallCount > 0),
                materialPath,
                lods,
                null,
                null,
                null);

        public static ComponentCandidateDraft Lineage(
            string candidateId,
            ComponentModelIdentity model,
            string lineageKey,
            string sourceLabel,
            IReadOnlyList<string> materialPaths,
            IReadOnlyList<MeshLineageCandidateLod> lods,
            IReadOnlyList<SelectedDrawCall> selected) => new(
                candidateId,
                ComponentDiscoveryV2Contract.MeshLineageKind,
                model,
                lineageKey,
                materialPaths,
                selected,
                true,
                null,
                null,
                lineageKey,
                sourceLabel,
                lods);

    }
}

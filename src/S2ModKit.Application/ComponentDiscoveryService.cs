using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed class ComponentDiscoveryService(IComponentCapabilityAnalyzer? capabilityAnalyzer = null)
{
    private const string RemoveOperation = "remove_component";
    private const string TransformOperation = "transform_component";
    private const int OperationVersion = 1;

    public async Task<ComponentDiscoveryResult> DiscoverAsync(
        ArtifactContent input,
        ModelSnapshot model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();

        var modelIdentity = ValidateInput(input, model);
        var observed = ReadCanonicalDrawCalls(model);
        var analyzerIdentity = CreateAnalyzerIdentity(capabilityAnalyzer);
        var drafts = CreateCandidateDrafts(modelIdentity, model.Lods, observed);
        var analyses = await AnalyzeTransformCapabilitiesAsync(
            input,
            model,
            drafts,
            cancellationToken).ConfigureAwait(false);

        var candidates = drafts.Select(draft => new ComponentCandidate(
            draft.CandidateId,
            ComponentDiscoveryContract.MaterialGroupKind,
            modelIdentity,
            draft.MaterialPath,
            CreateDisplayLabel(draft.MaterialPath),
            draft.Lods,
            [
                CreateRemovalCapability(draft),
                CreateTransformCapability(draft, analyses),
            ]))
            .OrderBy(candidate => candidate.MaterialPath, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

        var fingerprint = ComputeDiscoveryFingerprint(modelIdentity, analyzerIdentity, candidates);
        return new ComponentDiscoveryResult(
            ComponentDiscoveryContract.SchemaVersion,
            modelIdentity,
            fingerprint,
            analyzerIdentity,
            candidates);
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
                    "Component discovery received an invalid model logical path.",
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

    private static List<ObservedDrawCall> ReadCanonicalDrawCalls(ModelSnapshot model)
    {
        if (model.Lods.Count == 0)
        {
            throw DiscoveryError(
                "COMPONENT_MODEL_EMPTY",
                "The inspected model contains no LODs.",
                "Use a supported compiled model with at least one LOD and draw call.");
        }

        var duplicateLod = model.Lods.GroupBy(lod => lod.Level).FirstOrDefault(group => group.Count() > 1);
        if (duplicateLod is not null)
        {
            throw DiscoveryError(
                "COMPONENT_LOD_IDENTITY_DUPLICATE",
                $"The inspected model contains duplicate LOD {duplicateLod.Key.ToString(CultureInfo.InvariantCulture)}.",
                "Reject the inconsistent inspection result and re-inspect the input.");
        }

        var observed = new List<ObservedDrawCall>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lod in model.Lods.OrderBy(item => item.Level))
        {
            if (lod.Level < 0)
            {
                throw DiscoveryError(
                    "COMPONENT_LOD_IDENTITY_INVALID",
                    $"The inspected model contains negative LOD {lod.Level.ToString(CultureInfo.InvariantCulture)}.",
                    "Reject the inconsistent inspection result and re-inspect the input.");
            }

            foreach (var mesh in lod.Meshes)
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
                            $"LOD {lod.Level.ToString(CultureInfo.InvariantCulture)} mesh {mesh.MeshOrdinal.ToString(CultureInfo.InvariantCulture)} has an invalid resource path.",
                            "Reject the inconsistent inspection result and re-inspect the input.",
                            ErrorCategory.UnsupportedCapability),
                        exception);
                }

                foreach (var drawCall in mesh.DrawCalls
                    .OrderBy(item => item.DrawCallOrdinal)
                    .ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(drawCall.Id) || !identities.Add(drawCall.Id))
                    {
                        throw DiscoveryError(
                            "COMPONENT_DRAW_CALL_IDENTITY_DUPLICATE",
                            $"The inspected model contains an empty or duplicate draw-call identity '{drawCall.Id}'.",
                            "Reject the inconsistent inspection result and re-inspect the input.");
                    }

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

                    observed.Add(new ObservedDrawCall(
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
                            drawCall.IndexCount)));
                }
            }
        }

        if (observed.Count == 0)
        {
            throw DiscoveryError(
                "COMPONENT_MODEL_EMPTY",
                "The inspected model contains no draw calls.",
                "Use a supported compiled model with at least one material draw call.");
        }

        return observed;
    }

    private static CandidateDraft[] CreateCandidateDrafts(
        ComponentModelIdentity model,
        IReadOnlyList<LodSnapshot> modelLods,
        List<ObservedDrawCall> observed)
    {
        var lodLevels = modelLods.Select(lod => lod.Level).Order().ToArray();
        var drafts = observed.GroupBy(item => item.MaterialPath, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = group.Select(item => item.DrawCall)
                    .OrderBy(item => item.Lod)
                    .ThenBy(item => item.ResourcePath, StringComparer.Ordinal)
                    .ThenBy(item => item.MeshOrdinal)
                    .ThenBy(item => item.DrawCallOrdinal)
                    .ThenBy(item => item.DrawCallId, StringComparer.Ordinal)
                    .ToArray();
                var lods = lodLevels.Select(lod =>
                {
                    var ids = selected.Where(item => item.Lod == lod)
                        .Select(item => item.DrawCallId)
                        .ToArray();
                    return new ComponentCandidateLod(lod, ids, ids.Length);
                }).ToArray();
                return new CandidateDraft(
                    ComputeCandidateId(model, group.Key, lods),
                    group.Key,
                    lods,
                    selected);
            })
            .ToArray();

        var duplicateCandidate = drafts.GroupBy(draft => draft.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCandidate is not null)
        {
            throw DiscoveryError(
                "COMPONENT_CANDIDATE_MAPPING_AMBIGUOUS",
                $"Multiple material groups produced candidate identity '{duplicateCandidate.Key}'.",
                "Reject the discovery result; candidate identities must map to exactly one material group.");
        }

        return drafts;
    }

    private async Task<IReadOnlyDictionary<string, ComponentCapabilityAnalysis>> AnalyzeTransformCapabilitiesAsync(
        ArtifactContent input,
        ModelSnapshot model,
        IReadOnlyList<CandidateDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (capabilityAnalyzer is null || !capabilityAnalyzer.CanAnalyze(input, model))
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var complete = drafts.Where(IsLodComplete).ToArray();
        if (complete.Length == 0)
        {
            return new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        }

        var selections = complete.Select(draft => new ComponentCapabilitySelection(
            draft.CandidateId,
            ComponentDiscoveryContract.MaterialGroupKind,
            [draft.MaterialPath],
            draft.SelectedDrawCalls)).ToArray();
        var returned = await capabilityAnalyzer.AnalyzeAsync(
            new ComponentCapabilityAnalysisRequest(input, model, selections),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var expectedIds = selections.Select(selection => selection.SelectionId).ToHashSet(StringComparer.Ordinal);
        var results = new Dictionary<string, ComponentCapabilityAnalysis>(StringComparer.Ordinal);
        foreach (var analysis in returned)
        {
            if (!expectedIds.Contains(analysis.SelectionId) || !results.TryAdd(analysis.SelectionId, analysis))
            {
                throw DiscoveryError(
                    "COMPONENT_CAPABILITY_ANALYSIS_AMBIGUOUS",
                    $"The capability analyzer returned an unknown or duplicate selection '{analysis.SelectionId}'.",
                    "Reject the analyzer output and rerun with an implementation that covers each requested selection exactly once.");
            }

            ValidateAnalysis(analysis, complete.Single(draft => draft.CandidateId == analysis.SelectionId));
        }

        var missing = expectedIds.Except(results.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_ANALYSIS_INCOMPLETE",
                $"The capability analyzer omitted {missing.Length.ToString(CultureInfo.InvariantCulture)} requested selection(s).",
                "Reject the partial analyzer output and rerun with complete deterministic coverage.");
        }

        return results;
    }

    private static ComponentCapability CreateRemovalCapability(CandidateDraft draft) =>
        IsLodComplete(draft)
            ? AvailableCapability(RemoveOperation, [])
            : IncompleteLodCapability(RemoveOperation);

    private ComponentCapability CreateTransformCapability(
        CandidateDraft draft,
        IReadOnlyDictionary<string, ComponentCapabilityAnalysis> analyses)
    {
        if (!IsLodComplete(draft))
        {
            return IncompleteLodCapability(TransformOperation);
        }

        if (capabilityAnalyzer is null || !analyses.TryGetValue(draft.CandidateId, out var analysis))
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

    private static ComponentCapability AvailableCapability(
        string operationKind,
        IReadOnlyList<ComponentGeometryLodFacts> geometry) => new(
            operationKind,
            OperationVersion,
            ComponentDiscoveryContract.Available,
            [new ComponentCapabilityReason(
                "COMPONENT_CAPABILITY_AVAILABLE",
                "The candidate has exact membership in every present LOD for this operation.")],
            geometry);

    private static ComponentCapability IncompleteLodCapability(string operationKind) => new(
        operationKind,
        OperationVersion,
        ComponentDiscoveryContract.Unsupported,
        [new ComponentCapabilityReason(
            "COMPONENT_INCOMPLETE_LOD_COVERAGE",
            "The material group is absent from one or more present LODs required by all_present.")],
        []);

    private static bool IsLodComplete(CandidateDraft draft) => draft.Lods.All(lod => lod.DrawCallCount > 0);

    private static void ValidateAnalysis(ComponentCapabilityAnalysis analysis, CandidateDraft draft)
    {
        if (!string.Equals(analysis.OperationKind, TransformOperation, StringComparison.Ordinal)
            || analysis.OperationVersion is not (1 or 2))
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

        if (analysis.Reasons.Count == 0
            || analysis.Reasons.Any(reason => !IsReasonCode(reason.Code) || string.IsNullOrWhiteSpace(reason.Summary)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_REASON_INVALID",
                $"Selection '{analysis.SelectionId}' returned an empty or invalid stable reason.",
                "Return at least one upper-case stable reason code and a non-empty summary.");
        }

        var geometry = analysis.GeometryByLod.OrderBy(item => item.Lod).ToArray();
        if (geometry.GroupBy(item => item.Lod).Any(group => group.Count() > 1)
            || geometry.Any(item => item.SelectedVertexCount <= 0)
            || geometry.Any(item => string.IsNullOrWhiteSpace(item.VertexSetHash.Value))
            || geometry.Any(item => !draft.Lods.Any(lod => lod.Lod == item.Lod)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_FACTS_INVALID",
                $"Selection '{analysis.SelectionId}' returned invalid per-LOD geometry facts.",
                "Return unique positive geometry facts only for LODs in the requested selection.");
        }

        if (string.Equals(analysis.Availability, ComponentDiscoveryContract.Available, StringComparison.Ordinal)
            && (!geometry.Select(item => item.Lod).SequenceEqual(draft.Lods.Select(item => item.Lod))
                || geometry.Any(item => !item.ExclusivelyOwned)))
        {
            throw DiscoveryError(
                "COMPONENT_CAPABILITY_FACTS_INVALID",
                $"Selection '{analysis.SelectionId}' is marked available without complete exclusive geometry facts.",
                "Return one exclusively owned geometry fact for every requested LOD or mark the capability unavailable.");
        }
    }

    private static bool IsReasonCode(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => character == '_' || char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));

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
            componentVersions.Add(componentVersion.Key, componentVersion.Value);
        }

        return new ComponentCapabilityAnalyzerIdentity(
            analyzer.AnalyzerName,
            analyzer.AnalyzerVersion,
            componentVersions);
    }

    private static string ComputeCandidateId(
        ComponentModelIdentity model,
        string materialPath,
        ComponentCandidateLod[] lods)
    {
        var builder = new CanonicalTextBuilder()
            .Add("component_candidate")
            .Add(ComponentDiscoveryContract.SchemaVersion)
            .Add(model.LogicalPath)
            .Add(model.ContentHash.ToString())
            .Add(ComponentDiscoveryContract.MaterialGroupKind)
            .Add(materialPath)
            .Add(lods.Length);
        foreach (var lod in lods)
        {
            builder.Add("lod").Add(lod.Lod).Add(lod.DrawCallCount);
            foreach (var id in lod.DrawCallIds)
            {
                builder.Add(id);
            }
        }

        return $"cmp_{builder.ComputeHash().Value[..24]}";
    }

    private static ContentHash ComputeDiscoveryFingerprint(
        ComponentModelIdentity model,
        ComponentCapabilityAnalyzerIdentity analyzer,
        ComponentCandidate[] candidates)
    {
        var builder = new CanonicalTextBuilder()
            .Add("component_discovery")
            .Add(ComponentDiscoveryContract.SchemaVersion)
            .Add(model.LogicalPath)
            .Add(model.ContentHash.ToString())
            .Add(model.Size)
            .Add(analyzer.Name)
            .Add(analyzer.Version)
            .Add(analyzer.ComponentVersions.Count);
        foreach (var version in analyzer.ComponentVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Add(version.Key).Add(version.Value);
        }

        builder.Add(candidates.Length);
        foreach (var candidate in candidates)
        {
            builder.Add("candidate")
                .Add(candidate.CandidateId)
                .Add(candidate.Kind)
                .Add(candidate.MaterialPath)
                .Add(candidate.DisplayLabel)
                .Add(candidate.Lods.Count);
            foreach (var lod in candidate.Lods)
            {
                builder.Add("lod").Add(lod.Lod).Add(lod.DrawCallCount);
                foreach (var id in lod.DrawCallIds)
                {
                    builder.Add(id);
                }
            }

            builder.Add(candidate.Capabilities.Count);
            foreach (var capability in candidate.Capabilities)
            {
                builder.Add("capability")
                    .Add(capability.OperationKind)
                    .Add(capability.OperationVersion)
                    .Add(capability.Availability)
                    .Add(capability.Reasons.Count);
                foreach (var reason in capability.Reasons)
                {
                    builder.Add(reason.Code);
                }

                builder.Add(capability.GeometryByLod.Count);
                foreach (var geometry in capability.GeometryByLod)
                {
                    builder.Add("geometry")
                        .Add(geometry.Lod)
                        .Add(geometry.SelectedVertexCount)
                        .Add(geometry.VertexSetHash.ToString())
                        .Add(geometry.ExclusivelyOwned);
                }
            }
        }

        return builder.ComputeHash();
    }

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

    private static S2ModKitException DiscoveryError(
        string code,
        string summary,
        string remediation,
        ErrorCategory category = ErrorCategory.UnsupportedCapability) =>
        new(new S2Error(code, "component_discovery", summary, remediation, category));

    private sealed record ObservedDrawCall(string MaterialPath, SelectedDrawCall DrawCall);

    private sealed record CandidateDraft(
        string CandidateId,
        string MaterialPath,
        IReadOnlyList<ComponentCandidateLod> Lods,
        IReadOnlyList<SelectedDrawCall> SelectedDrawCalls);

    private sealed class CanonicalTextBuilder
    {
        private readonly StringBuilder value = new();

        public CanonicalTextBuilder Add(bool item) => Add(item ? "true" : "false");

        public CanonicalTextBuilder Add(int item) => Add(item.ToString(CultureInfo.InvariantCulture));

        public CanonicalTextBuilder Add(long item) => Add(item.ToString(CultureInfo.InvariantCulture));

        public CanonicalTextBuilder Add(string item)
        {
            value.Append(item.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(item)
                .Append('\n');
            return this;
        }

        public ContentHash ComputeHash() => ContentHash.Compute(Encoding.UTF8.GetBytes(value.ToString()));
    }
}

using System.Globalization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class ComponentCandidateUnionBuilder
{
    public static ComponentCandidateUnion Create(
        ModelSnapshot model,
        IReadOnlyList<ComponentCandidateV2> candidates)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || candidates.Any(candidate => candidate is null))
        {
            throw UnionError(
                "SCAFFOLD_COMPONENT_REQUIRED",
                "At least one precise component candidate is required.",
                "Select current material_group or mesh_lineage candidate IDs.");
        }

        var duplicateCandidate = candidates.GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateCandidate is not null)
        {
            throw UnionError(
                "SCAFFOLD_COMPONENT_DUPLICATE",
                $"Component candidate '{duplicateCandidate.Key}' was selected more than once.",
                "Provide every candidate ID exactly once.");
        }

        var snapshot = PreciseComponentSnapshot.Read(model);
        var modelIdentity = new ComponentModelIdentity(
            NormalizePath(model.Artifact.LogicalPath),
            model.Artifact.ContentHash,
            model.Artifact.Size);
        var selected = new Dictionary<string, SelectedDrawCall>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal))
        {
            if (candidate.Model != modelIdentity)
            {
                throw Stale(candidate.CandidateId, "model identity");
            }

            var expectedCandidateId = candidate switch
            {
                MaterialGroupComponentCandidateV2 material => ComponentCandidateIdentity.ComputeMaterialCandidateId(
                    material.Model,
                    material.MaterialPath,
                    material.Lods.ToArray()),
                MeshLineageComponentCandidateV2 lineage => ComponentCandidateIdentity.ComputeLineageCandidateId(
                    lineage.Model,
                    lineage.LineageKey,
                    lineage.SourceLabel,
                    lineage.Lods.ToArray()),
                _ => string.Empty,
            };
            if (!string.Equals(expectedCandidateId, candidate.CandidateId, StringComparison.Ordinal))
            {
                throw Stale(candidate.CandidateId, "candidate identity");
            }

            var candidateCalls = candidate switch
            {
                MaterialGroupComponentCandidateV2 material => ValidateMaterialCandidate(material, snapshot),
                MeshLineageComponentCandidateV2 lineage => ValidateLineageCandidate(lineage, snapshot),
                _ => throw UnionError(
                    "SCAFFOLD_COMPONENT_KIND_UNSUPPORTED",
                    $"Component candidate '{candidate.CandidateId}' has an unsupported runtime kind.",
                    "Use a material_group or mesh_lineage candidate from current discovery output."),
            };
            foreach (var call in candidateCalls)
            {
                selected.TryAdd(call.DrawCallId, call);
            }
        }

        if (selected.Count == 0)
        {
            throw UnionError(
                "SCAFFOLD_COMPONENT_STALE",
                "The selected precise candidates contain no current draw calls.",
                "Refresh component discovery and select current candidate IDs.");
        }

        var ordered = selected.Values
            .OrderBy(call => call.Lod)
            .ThenBy(call => call.ResourcePath, StringComparer.Ordinal)
            .ThenBy(call => call.MeshOrdinal)
            .ThenBy(call => call.DrawCallOrdinal)
            .ThenBy(call => call.DrawCallId, StringComparer.Ordinal)
            .ToArray();
        return new ComponentCandidateUnion(
            candidates.Select(candidate => candidate.CandidateId).Order(StringComparer.Ordinal).ToArray(),
            candidates.Select(candidate => candidate.Kind).Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind == ComponentDiscoveryV2Contract.MaterialGroupKind ? 0 : 1)
                .ThenBy(kind => kind, StringComparer.Ordinal)
                .ToArray(),
            ordered.Select(call => call.MaterialPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ordered);
    }

    private static SelectedDrawCall[] ValidateMaterialCandidate(
        MaterialGroupComponentCandidateV2 candidate,
        PreciseComponentSnapshot snapshot)
    {
        var material = NormalizePath(candidate.MaterialPath);
        if (!string.Equals(material, candidate.MaterialPath, StringComparison.Ordinal)
            || candidate.Lods.Count != snapshot.Lods.Count
            || candidate.Lods.Select(lod => lod.Lod).Distinct().Count() != candidate.Lods.Count)
        {
            throw Stale(candidate.CandidateId, "material membership");
        }

        var current = snapshot.DrawCalls.Where(call => call.MaterialPath == material)
            .Select(call => call.Selection)
            .ToArray();
        foreach (var lod in snapshot.Lods)
        {
            var expected = candidate.Lods.SingleOrDefault(item => item.Lod == lod)
                ?? throw Stale(candidate.CandidateId, $"LOD {lod.ToString(CultureInfo.InvariantCulture)} membership");
            var actualIds = current.Where(call => call.Lod == lod).Select(call => call.DrawCallId).ToArray();
            if (expected.DrawCallCount != expected.DrawCallIds.Count
                || expected.DrawCallCount != actualIds.Length
                || !expected.DrawCallIds.SequenceEqual(actualIds, StringComparer.Ordinal))
            {
                throw Stale(candidate.CandidateId, $"LOD {lod.ToString(CultureInfo.InvariantCulture)} draw calls");
            }
        }

        return current;
    }

    private static SelectedDrawCall[] ValidateLineageCandidate(
        MeshLineageComponentCandidateV2 candidate,
        PreciseComponentSnapshot snapshot)
    {
        var claimed = new MechanicalMeshLineage(candidate.LineageKey, candidate.SourceLabel, candidate.SourceLabel);
        if (!claimed.IsCanonical()
            || candidate.Lods.Count != snapshot.Lods.Count
            || candidate.Lods.Select(lod => lod.Lod).Distinct().Count() != candidate.Lods.Count)
        {
            throw Stale(candidate.CandidateId, "lineage identity");
        }

        var members = snapshot.Meshes.Where(mesh => mesh.Lineage?.Key == candidate.LineageKey)
            .OrderBy(mesh => mesh.Lod)
            .ToArray();
        if (members.Length != snapshot.Lods.Count
            || members.GroupBy(mesh => mesh.Lod).Any(group => group.Count() != 1)
            || members.Any(mesh => mesh.Lineage?.SourceLabel != candidate.SourceLabel)
            || snapshot.LineageDiagnostics.Any(diagnostic => diagnostic.LineageKey == candidate.LineageKey))
        {
            throw Stale(candidate.CandidateId, "complete lineage mapping");
        }

        foreach (var member in members)
        {
            var expected = candidate.Lods.SingleOrDefault(lod => lod.Lod == member.Lod)
                ?? throw Stale(candidate.CandidateId, $"LOD {member.Lod.ToString(CultureInfo.InvariantCulture)} lineage membership");
            var materials = member.DrawCalls.Select(call => call.MaterialPath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var drawCallIds = member.DrawCalls.Select(call => call.Selection.DrawCallId).ToArray();
            if (expected.ResourcePath != member.ResourcePath
                || expected.MeshOrdinal != member.MeshOrdinal
                || expected.ResourceBlockIndex != member.ResourceBlockIndex
                || expected.ImmutableSemanticHash != member.ImmutableSemanticHash
                || expected.SourceName != member.Lineage!.SourceName
                || expected.DrawCallCount != drawCallIds.Length
                || expected.DrawCallCount != expected.DrawCallIds.Count
                || !expected.MaterialPaths.SequenceEqual(materials, StringComparer.Ordinal)
                || !expected.DrawCallIds.SequenceEqual(drawCallIds, StringComparer.Ordinal))
            {
                throw Stale(candidate.CandidateId, $"LOD {member.Lod.ToString(CultureInfo.InvariantCulture)} exact mesh facts");
            }
        }

        var unionMaterials = members.SelectMany(member => member.DrawCalls)
            .Select(call => call.MaterialPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!candidate.MaterialPaths.SequenceEqual(unionMaterials, StringComparer.Ordinal))
        {
            throw Stale(candidate.CandidateId, "lineage material union");
        }

        return members.SelectMany(member => member.DrawCalls).Select(call => call.Selection).ToArray();
    }

    private static string NormalizePath(string value)
    {
        try
        {
            return StableIdentity.NormalizePath(value);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error(
                    "SCAFFOLD_COMPONENT_STALE",
                    "selection",
                    "A precise component candidate contains an invalid path.",
                    "Refresh component discovery and select current candidate IDs.",
                    ErrorCategory.SelectionOrLod),
                exception);
        }
    }

    private static S2ModKitException Stale(string candidateId, string fact) => UnionError(
        "SCAFFOLD_COMPONENT_STALE",
        $"Component candidate '{candidateId}' no longer matches its current {fact}.",
        "Refresh component discovery and select current candidate IDs.");

    private static S2ModKitException UnionError(string code, string summary, string remediation) =>
        new(new S2Error(code, "selection", summary, remediation, ErrorCategory.SelectionOrLod));
}

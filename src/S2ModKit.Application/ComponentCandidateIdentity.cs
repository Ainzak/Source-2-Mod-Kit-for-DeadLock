using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

internal static class ComponentCandidateIdentity
{
    internal static string ComputeMaterialCandidateId(
        ComponentModelIdentity model,
        string materialPath,
        ComponentCandidateLod[] lods)
    {
        var builder = CandidateIdentityBuilder(model, ComponentDiscoveryV2Contract.MaterialGroupKind)
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

    internal static string ComputeLineageCandidateId(
        ComponentModelIdentity model,
        string lineageKey,
        string sourceLabel,
        MeshLineageCandidateLod[] lods)
    {
        var builder = CandidateIdentityBuilder(model, ComponentDiscoveryV2Contract.MeshLineageKind)
            .Add(lineageKey)
            .Add(sourceLabel)
            .Add(lods.Length);
        foreach (var lod in lods)
        {
            builder.Add("lod")
                .Add(lod.Lod)
                .Add(lod.ResourcePath)
                .Add(lod.MeshOrdinal)
                .Add(lod.ResourceBlockIndex)
                .Add(lod.ImmutableSemanticHash.ToString())
                .Add(lod.SourceName)
                .Add(lod.MaterialPaths.Count);
            foreach (var material in lod.MaterialPaths)
            {
                builder.Add(material);
            }

            builder.Add(lod.DrawCallCount);
            foreach (var id in lod.DrawCallIds)
            {
                builder.Add(id);
            }
        }

        return $"cmp_{builder.ComputeHash().Value[..24]}";
    }

    private static CanonicalTextBuilder CandidateIdentityBuilder(ComponentModelIdentity model, string kind) =>
        new CanonicalTextBuilder()
            .Add("component_candidate")
            .Add(ComponentDiscoveryV2Contract.SchemaVersion)
            .Add(model.LogicalPath)
            .Add(model.ContentHash.ToString())
            .Add(kind);

    internal static ContentHash ComputeDiscoveryFingerprint(
        ComponentModelIdentity model,
        ComponentCapabilityAnalyzerIdentity analyzer,
        ComponentCandidateV2[] candidates,
        IReadOnlyList<ComponentLineageDiagnostic> diagnostics)
    {
        var builder = new CanonicalTextBuilder()
            .Add("component_discovery")
            .Add(ComponentDiscoveryV2Contract.SchemaVersion)
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
            builder.Add(candidate.CandidateId)
                .Add(candidate.Kind)
                .Add(candidate.DisplayLabel);
            switch (candidate)
            {
                case MaterialGroupComponentCandidateV2 material:
                    builder.Add(material.MaterialPath).Add(material.Lods.Count);
                    foreach (var lod in material.Lods)
                    {
                        builder.Add(lod.Lod).Add(lod.DrawCallCount);
                        foreach (var id in lod.DrawCallIds)
                        {
                            builder.Add(id);
                        }
                    }

                    break;
                case MeshLineageComponentCandidateV2 lineage:
                    builder.Add(lineage.LineageKey).Add(lineage.SourceLabel).Add(lineage.Lods.Count);
                    foreach (var lod in lineage.Lods)
                    {
                        builder.Add(lod.Lod)
                            .Add(lod.ResourcePath)
                            .Add(lod.MeshOrdinal)
                            .Add(lod.ResourceBlockIndex)
                            .Add(lod.ImmutableSemanticHash.ToString())
                            .Add(lod.SourceName)
                            .Add(lod.DrawCallCount);
                        foreach (var materialPath in lod.MaterialPaths)
                        {
                            builder.Add(materialPath);
                        }

                        foreach (var id in lod.DrawCallIds)
                        {
                            builder.Add(id);
                        }
                    }

                    break;
            }

            AddCapabilities(builder, candidate.Capabilities);
        }

        builder.Add(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            builder.Add(diagnostic.LineageKey ?? string.Empty)
                .Add(diagnostic.Code)
                .Add(diagnostic.Summary)
                .Add(diagnostic.ObservedLods.Count);
            foreach (var lod in diagnostic.ObservedLods)
            {
                builder.Add(lod);
            }
        }

        return builder.ComputeHash();
    }

    private static void AddCapabilities(CanonicalTextBuilder builder, IReadOnlyList<ComponentCapability> capabilities)
    {
        builder.Add(capabilities.Count);
        foreach (var capability in capabilities)
        {
            builder.Add(capability.OperationKind)
                .Add(capability.OperationVersion)
                .Add(capability.Availability)
                .Add(capability.Reasons.Count);
            foreach (var reason in capability.Reasons)
            {
                builder.Add(reason.Code).Add(reason.Summary);
            }

            builder.Add(capability.GeometryByLod.Count);
            foreach (var geometry in capability.GeometryByLod)
            {
                builder.Add(geometry.Lod)
                    .Add(geometry.SelectedVertexCount)
                    .Add(geometry.VertexSetHash.ToString())
                    .Add(geometry.ExclusivelyOwned);
            }
        }
    }

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

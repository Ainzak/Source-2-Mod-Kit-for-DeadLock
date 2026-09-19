using System.CommandLine;
using System.Globalization;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static void WriteFailure(string command, S2Error failure, bool json, TextWriter output, TextWriter error)
    {
        if (json)
        {
            output.WriteLine(JsonDefaults.Serialize(new CliFailure(command, failure)));
            return;
        }

        error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ERROR {failure.Code} [{failure.Boundary}]: {failure.Summary}"));
        error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Remediation: {failure.Remediation}"));
    }

    private static void RenderDoctor(TextWriter writer, DoctorResult result)
    {
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"S2ModKit doctor: {result.Status}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Product version: {result.ProductVersion}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Runtime: {result.RuntimeIdentifier}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Source 2 adapter: {result.AdapterName} {result.AdapterVersion}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"External verifier: {result.ExternalVerifier}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Geometry codec: {result.GeometryCodec}"));
        writer.WriteLine("Proof boundary: offline_static");
    }

    private static void RenderCatalogue(TextWriter writer, HeroCatalogueVerification catalogue)
    {
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Heroes: {catalogue.Heroes.Count}; catalogue={catalogue.CatalogueId}; revision={catalogue.Revision}"));
        foreach (var hero in catalogue.Heroes)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{hero.DisplayName} [{hero.HeroId}] — {hero.RosterStatus}"));
            foreach (var resource in hero.Resources)
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {resource.DisplayName} [{resource.ResourceId}] — {resource.Role}; {resource.QualificationStatus}; {resource.VerificationStatus}"));
            }
        }
    }

    private static void RenderCatalogueResolution(TextWriter writer, HeroCatalogueResolution resolution)
    {
        var hero = resolution.Hero;
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Hero: {hero.DisplayName} [{hero.HeroId}] — {hero.RosterStatus}"));
        if (hero.Aliases.Count > 0)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Aliases: {string.Join(", ", hero.Aliases)}"));
        }

        foreach (var resource in hero.Resources)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{resource.DisplayName} [{resource.ResourceId}] — {resource.Role}; {resource.QualificationStatus}; {resource.VerificationStatus}"));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Locator: {resource.LogicalPath}"));
        }
    }

    private static void RenderCompatibilityPublication(
        TextWriter writer,
        CompatibilityReportPublication publication)
    {
        writer.Write(CompatibilityReports.RenderText(publication.Report));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"JSON report: {publication.JsonPath}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Markdown report: {publication.MarkdownPath}"));
    }

    private static void RenderInspection(TextWriter writer, InspectionRunResult inspection)
    {
        var model = inspection.Model;
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Model: {model.Artifact.LogicalPath}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SHA-256: {model.Artifact.ContentHash}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Direct dependencies: {inspection.Project.Dependencies.Count}; graph edges: {inspection.Project.DependencyEdges.Count}"));
        foreach (var dependency in inspection.Project.Dependencies.OrderBy(item => item.LogicalPath, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Dependency: {dependency.LogicalPath} {dependency.ContentHash}"));
        }

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Blocks: {model.Artifact.Blocks.Count}; LODs: {model.Lods.Count}"));
        foreach (var lod in model.Lods.OrderBy(item => item.Level))
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"LOD {lod.Level}: {lod.Meshes.Count} meshes"));
            foreach (var mesh in lod.Meshes.OrderBy(item => item.MeshOrdinal))
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Mesh {mesh.MeshOrdinal}, MDAT {mesh.ResourceBlockIndex}: {mesh.DrawCalls.Count} draw calls"));
                if (mesh.Geometry is { } geometry)
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    Geometry: {geometry.Status} — {geometry.Summary}"));
                }

                var geometryByDrawCall = mesh.Geometry?.DrawCalls.ToDictionary(item => item.DrawCallId, StringComparer.Ordinal);
                foreach (var drawCall in mesh.DrawCalls.OrderBy(item => item.DrawCallOrdinal))
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {drawCall.Id} {drawCall.MaterialPath} [{drawCall.IndexStart}, {drawCall.IndexCount}]"));
                    if (geometryByDrawCall?.TryGetValue(drawCall.Id, out var drawGeometry) == true)
                    {
                        writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"      vertices={drawGeometry.UniqueVertexCount}; exclusive-from-other-draw-calls={drawGeometry.ExclusivelyOwned}; buffers=MVTX[{drawGeometry.VertexBufferOrdinal}]/MIDX[{drawGeometry.IndexBufferOrdinal}]; bounds={FormatPoint(drawGeometry.Bounds.Min)}..{FormatPoint(drawGeometry.Bounds.Max)}"));
                    }
                }
            }
        }
    }

    private static void RenderComponents(TextWriter writer, ComponentDiscoveryResultV2 discovery)
    {
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Model: {discovery.Model.LogicalPath} ({discovery.Model.ContentHash})"));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Discovery: {discovery.DiscoveryFingerprint}; analyzer={discovery.Analyzer.Name}@{discovery.Analyzer.Version}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Candidates: {discovery.Candidates.Count}"));
        foreach (var candidate in discovery.Candidates
            .OrderBy(item => item.Kind == ComponentDiscoveryV2Contract.MaterialGroupKind ? 0 : 1)
            .ThenBy(item => item switch
            {
                MaterialGroupComponentCandidateV2 material => material.MaterialPath,
                MeshLineageComponentCandidateV2 lineage => lineage.LineageKey,
                _ => string.Empty,
            }, StringComparer.Ordinal)
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{candidate.DisplayLabel} [{candidate.CandidateId}]"));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Kind: {candidate.Kind}"));
            switch (candidate)
            {
                case MaterialGroupComponentCandidateV2 material:
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Material: {material.MaterialPath}"));
                    foreach (var lod in material.Lods.OrderBy(item => item.Lod))
                    {
                        writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"  LOD {lod.Lod}: {lod.DrawCallCount} draw call(s) — {string.Join(", ", lod.DrawCallIds)}"));
                    }

                    break;
                case MeshLineageComponentCandidateV2 lineage:
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Source: {lineage.SourceLabel} (key={lineage.LineageKey})"));
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Materials: {string.Join(", ", lineage.MaterialPaths)}"));
                    foreach (var lod in lineage.Lods.OrderBy(item => item.Lod))
                    {
                        writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"  LOD {lod.Lod}: mesh={lod.ResourcePath}#{lod.MeshOrdinal}; block={lod.ResourceBlockIndex}; source={lod.SourceName}"));
                        writer.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"    materials={string.Join(", ", lod.MaterialPaths)}; draw calls ({lod.DrawCallCount})={string.Join(", ", lod.DrawCallIds)}"));
                    }

                    break;
            }

            foreach (var capability in candidate.Capabilities
                .OrderBy(item => item.OperationKind, StringComparer.Ordinal)
                .ThenBy(item => item.OperationVersion))
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {capability.OperationKind}@{capability.OperationVersion}: {capability.Availability}"));
                foreach (var geometry in capability.GeometryByLod.OrderBy(item => item.Lod))
                {
                    writer.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    LOD {geometry.Lod}: vertices={geometry.SelectedVertexCount}; vertex-set={geometry.VertexSetHash}; exclusive={geometry.ExclusivelyOwned}"));
                }

                foreach (var reason in capability.Reasons
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.Summary, StringComparer.Ordinal))
                {
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {reason.Code}: {reason.Summary}"));
                }
            }
        }

        if (discovery.LineageDiagnostics.Count > 0)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Rejected lineages: {discovery.LineageDiagnostics.Count}"));
            foreach (var diagnostic in discovery.LineageDiagnostics
                .OrderBy(item => item.LineageKey, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Summary, StringComparer.Ordinal))
            {
                var lineageKey = diagnostic.LineageKey ?? "<unidentified>";
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {lineageKey}: {diagnostic.Code} — {diagnostic.Summary}; LODs={string.Join(",", diagnostic.ObservedLods.Order())}"));
            }
        }
    }

    private static string FormatPoint(TransformVector3 point) => string.Create(
        CultureInfo.InvariantCulture,
        $"({point.X:R},{point.Y:R},{point.Z:R})");

    private static void RenderPlan(TextWriter writer, PlanRunResult result)
    {
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Plan fingerprint: {result.Plan.Fingerprint}"));
        foreach (var operation in result.Plan.Operations)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{operation.OperationId}: {operation.SelectedDrawCalls.Count} draw calls in {operation.TargetBlocks.Count} target blocks"));
        }

        writer.WriteLine("Dry-run only: no candidate model was created.");
    }
}

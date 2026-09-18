using System.Text;
using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Reporting;

public sealed class EvidenceReportRenderer : IReportRenderer, IVpkPackageReportRenderer, IRuntimeObservationRenderer
{
    public string RenderJson(EvidenceReport report) => JsonDefaults.Serialize(report);

    public string RenderMarkdown(EvidenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        AppendInvariantLine(text, $"# S2ModKit evidence — {Escape(report.ReportId)}");
        text.AppendLine();
        AppendInvariantLine(text, $"- Status: **{Escape(report.Status)}**");
        AppendInvariantLine(text, $"- Command: `{Escape(report.Command)}`");
        AppendInvariantLine(text, $"- Created UTC: `{report.CreatedUtc:O}`");
        AppendInvariantLine(text, $"- Proof level: `{Escape(report.ProofLevel)}`");
        AppendInvariantLine(text, $"- Plan fingerprint: `{report.PlanFingerprint}`");
        text.AppendLine().AppendLine("## Artifacts").AppendLine();
        AppendInvariantLine(text, $"- Input: `{Escape(report.Input.LogicalPath)}` — `{report.Input.ContentHash}` ({report.Input.Size} bytes)");

        AppendInvariantLine(text, $"- Immutable dependencies: {report.Dependencies.Count}");
        foreach (var dependency in report.Dependencies)
        {
            AppendInvariantLine(text, $"  - `{Escape(dependency.LogicalPath)}` — `{dependency.ContentHash}` ({dependency.Size} bytes)");
        }

        if (report.Output is not null)
        {
            AppendInvariantLine(text, $"- Output: `{Escape(report.Output.LogicalPath)}` — `{report.Output.ContentHash}` ({report.Output.Size} bytes)");
        }

        text.AppendLine().AppendLine("## Operations").AppendLine();
        foreach (var operation in report.Operations)
        {
            AppendInvariantLine(text, $"### {Escape(operation.OperationId)}");
            text.AppendLine();
            AppendInvariantLine(text, $"- Contract: `{operation.Kind}@{operation.Version}`");
            AppendInvariantLine(text, $"- Selected draw calls: {operation.SelectedDrawCallIds.Count}");
            foreach (var id in operation.SelectedDrawCallIds)
            {
                AppendInvariantLine(text, $"  - `{Escape(id)}`");
            }

            AppendInvariantLine(text, $"- Affected mesh resources: {operation.ChangedResources.Count}");
            foreach (var resource in operation.ChangedResources)
            {
                AppendInvariantLine(text, $"  - `{Escape(resource)}`");
            }

            foreach (var change in operation.GeometryChanges)
            {
                AppendInvariantLine(text, $"- Geometry LOD {change.Lod}, mesh {change.MeshOrdinal}, block {change.ResourceBlockIndex}:");
                AppendInvariantLine(text, $"  - Resource: `{Escape(change.ResourcePath)}`");
                AppendInvariantLine(text, $"  - Vertices: {change.VertexCount}; set `{change.VertexSetHash}`");
                AppendInvariantLine(text, $"  - Pivot: {Format(change.Pivot)}; scale: {change.UniformScale:R}; translation: {Format(change.Translation)}");
                AppendInvariantLine(text, $"  - Bounds: {Format(change.BeforeBounds)} -> {Format(change.AfterBounds)}");
                AppendInvariantLine(text, $"  - Maximum displacement: {change.MaximumDisplacement:R}");
                AppendInvariantLine(text, $"  - Changed attributes: `{Escape(string.Join(",", change.ChangedAttributes))}`");
                var outputVertexHash = change.OutputVertexBlockHash is null ? "not-produced" : change.OutputVertexBlockHash.Value.ToString();
                AppendInvariantLine(text, $"  - MVTX: `{change.InputVertexBlockHash}` -> `{outputVertexHash}`");
                if (change.InputDecodedVertexBufferHash is { } inputDecoded
                    && change.ExpectedDecodedVertexBufferHash is { } expectedDecoded)
                {
                    AppendInvariantLine(text, $"  - Decoded vertex buffer: `{inputDecoded}` -> expected `{expectedDecoded}`");
                }

                AppendInvariantLine(text, $"  - Codec: `{Escape(change.Codec.Name)}` / `{Escape(change.Codec.ApiProfile)}` / `{change.Codec.BinaryHash}`");
            }

            if (operation.CoupledTransform is { } coupled)
            {
                AppendInvariantLine(text, $"- Coupled policy: `{Escape(coupled.PhysicsPolicy)}`; pivot {Format(coupled.Pivot)}; scale {coupled.UniformScale:R}");
                AppendInvariantLine(text, $"  - Visual: {coupled.Visual.VertexCount} vertices; displacement {coupled.Visual.MaximumDisplacement:R}/{coupled.Visual.DisplacementLimit:R}");
                AppendInvariantLine(text, $"  - Collision: {coupled.Collision.VertexCount} vertices; displacement {coupled.Collision.MaximumDisplacement:R}/{coupled.Collision.DisplacementLimit:R}");
            }
        }

        text.AppendLine().AppendLine("## Resource blocks").AppendLine();
        foreach (var block in report.Blocks)
        {
            var outputHash = block.OutputHash is null ? "not-produced" : block.OutputHash.Value.ToString();
            AppendInvariantLine(
                text,
                $"- `{block.Index}:{Escape(block.Type)}` — `{Escape(block.Disposition)}`; input `{block.InputHash}`; output `{outputHash}`");
        }

        text.AppendLine().AppendLine("## Validation boundaries").AppendLine();
        foreach (var boundary in report.Boundaries)
        {
            AppendInvariantLine(text, $"- **{Escape(boundary.Name)} — {Escape(boundary.Status)}:** {Escape(boundary.Summary)}");
        }

        text.AppendLine().AppendLine("## Tool versions").AppendLine();
        foreach (var tool in report.ToolVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            AppendInvariantLine(text, $"- `{Escape(tool.Key)}`: `{Escape(tool.Value)}`");
        }

        text.AppendLine().AppendLine("## Limitations").AppendLine()
            .AppendLine("This report contains offline static evidence only. It is not proof of live Deadlock animation, procedural physics, camera, load order, or runtime behavior.");
        if (report.Warnings.Count > 0)
        {
            text.AppendLine();
        }

        foreach (var warning in report.Warnings)
        {
            AppendInvariantLine(text, $"- {Escape(warning)}");
        }

        return text.ToString();
    }

    public string RenderJson(VpkPackageEvidence report) => JsonDefaults.Serialize(report);

    public string RenderMarkdown(VpkPackageEvidence report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        AppendInvariantLine(text, $"# S2ModKit VPK evidence — {Escape(report.ReportId)}");
        text.AppendLine();
        AppendInvariantLine(text, $"- Status: **{Escape(report.Status)}**");
        AppendInvariantLine(text, $"- Command: `{Escape(report.Command)}`");
        AppendInvariantLine(text, $"- Created UTC: `{report.CreatedUtc:O}`");
        AppendInvariantLine(text, $"- Proof level: `{Escape(report.ProofLevel)}`");
        AppendInvariantLine(text, $"- Package id: `{Escape(report.PackageId)}`");
        AppendInvariantLine(text, $"- Model build id: `{Escape(report.BuildId)}`");
        AppendInvariantLine(text, $"- Package mode: `{Escape(report.Mode)}`");
        text.AppendLine().AppendLine("## Archives").AppendLine();
        if (report.SourceArchive is not null)
        {
            AppendInvariantLine(text, $"- Source: `{Escape(report.SourceArchive.Path)}` — `{report.SourceArchive.ContentHash}` ({report.SourceArchive.Size} bytes, {report.SourceArchive.EntryCount} entries)");
        }

        AppendInvariantLine(text, $"- Output: `{Escape(report.OutputArchive.Path)}` — `{report.OutputArchive.ContentHash}` ({report.OutputArchive.Size} bytes, {report.OutputArchive.EntryCount} entries)");
        if (report.ReplacedEntry is not null)
        {
            text.AppendLine().AppendLine("## Replaced entry").AppendLine();
            AppendInvariantLine(text, $"- Path: `{Escape(report.ReplacedEntry.LogicalPath)}`");
            AppendInvariantLine(text, $"- Source: `{report.ReplacedEntry.SourceContentHash}`; CRC32 `{report.ReplacedEntry.SourceCrc32:x8}`; {report.ReplacedEntry.SourceSize} bytes");
            AppendInvariantLine(text, $"- Output: `{report.ReplacedEntry.OutputContentHash}`; CRC32 `{report.ReplacedEntry.OutputCrc32:x8}`; {report.ReplacedEntry.OutputSize} bytes");
        }

        if (report.PackagedEntry is not null)
        {
            text.AppendLine().AppendLine("## Packaged entry").AppendLine();
            AppendInvariantLine(text, $"- Path: `{Escape(report.PackagedEntry.LogicalPath)}`");
            AppendInvariantLine(text, $"- Content: `{report.PackagedEntry.ContentHash}`; CRC32 `{report.PackagedEntry.Crc32:x8}`; {report.PackagedEntry.Size} bytes");
        }

        AppendInvariantLine(text, $"- Unchanged entries: {report.UnchangedEntryCount}");
        text.AppendLine().AppendLine("## Validation boundaries").AppendLine();
        foreach (var boundary in report.Boundaries)
        {
            AppendInvariantLine(text, $"- **{Escape(boundary.Name)} — {Escape(boundary.Status)}:** {Escape(boundary.Summary)}");
        }

        text.AppendLine().AppendLine("## Tool versions").AppendLine();
        foreach (var tool in report.ToolVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            AppendInvariantLine(text, $"- `{Escape(tool.Key)}`: `{Escape(tool.Value)}`");
        }

        text.AppendLine().AppendLine("## Limitations").AppendLine();
        foreach (var warning in report.Warnings)
        {
            AppendInvariantLine(text, $"- {Escape(warning)}");
        }

        return text.ToString();
    }

    public string RenderJson(RuntimeObservation observation) => JsonDefaults.Serialize(observation);

    public string RenderMarkdown(RuntimeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var text = new StringBuilder();
        AppendInvariantLine(text, $"# S2ModKit player observation — {Escape(observation.ObservationId)}");
        text.AppendLine();
        AppendInvariantLine(text, $"- Status: **{Escape(observation.Status)}**");
        AppendInvariantLine(text, $"- Created UTC: `{observation.CreatedUtc:O}`");
        AppendInvariantLine(text, $"- Proof level: `{Escape(observation.ProofLevel)}`");
        AppendInvariantLine(text, $"- Project: `{Escape(observation.ProjectId)}`");
        AppendInvariantLine(text, $"- Package: `{Escape(observation.PackageId)}`");
        AppendInvariantLine(text, $"- Installation: `{Escape(observation.InstallationId)}`");
        AppendInvariantLine(text, $"- Installed SHA-256: `{observation.InstalledHash}`");
        text.AppendLine().AppendLine("## Player checks").AppendLine();
        AppendInvariantLine(text, $"- Target component: **{Escape(observation.Checks.TargetComponent)}**");
        AppendInvariantLine(text, $"- Preserved materials and parts: **{Escape(observation.Checks.PreservedMaterialsAndParts)}**");
        AppendInvariantLine(text, $"- Animations: **{Escape(observation.Checks.Animations)}**");
        AppendInvariantLine(text, $"- LOD transitions: **{Escape(observation.Checks.LodTransitions)}**");
        AppendInvariantLine(text, $"- Menu preview: **{Escape(observation.Checks.MenuPreview)}**");
        AppendInvariantLine(text, $"- Death and respawn: **{Escape(observation.Checks.DeathAndRespawn)}**");
        text.AppendLine().AppendLine("## Verification boundaries").AppendLine();
        foreach (var boundary in observation.Boundaries)
        {
            AppendInvariantLine(text, $"- **{Escape(boundary.Name)} — {Escape(boundary.Status)}:** {Escape(boundary.Summary)}");
        }

        text.AppendLine().AppendLine("## Notes").AppendLine();
        foreach (var note in observation.Notes)
        {
            AppendInvariantLine(text, $"- {Escape(note)}");
        }

        text.AppendLine().AppendLine("This is a player observation linked to one exact installed hash. It does not generalize to other game builds, load orders, or packages.");
        return text.ToString();
    }

    private static void AppendInvariantLine(StringBuilder builder, FormattableString value) =>
        builder.AppendLine(value.ToString(CultureInfo.InvariantCulture));

    private static string Format(TransformVector3 value) =>
        FormattableString.Invariant($"({value.X:R}, {value.Y:R}, {value.Z:R})");

    private static string Format(GeometryBounds value) =>
        $"{Format(value.Min)}..{Format(value.Max)}";

    private static string Escape(string value) => value.Replace("`", "\\`", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
}

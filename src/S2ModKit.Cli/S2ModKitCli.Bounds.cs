using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private const long MaximumBoundsDiagnosticInputBytes = 512L * 1024 * 1024;

    private static Command CreateBoundsCommand(TextWriter output, TextWriter error)
    {
        var bounds = new Command("bounds", "Run read-only geometry-bound diagnostics.");
        var diagnose = new Command("diagnose", "Compare stored bone bounds with every decoded vertex buffer.");
        var input = RequiredStringOption("--input", "Read-only compiled .vmdl_c input file.");
        var resourcePath = RequiredStringOption("--resource-path", "Logical Source 2 resource path for the report.");
        var outputRoot = RequiredStringOption("--output-root", "Ignored or otherwise configured directory for diagnostic reports.");
        var format = CreateFormatOption();
        diagnose.Options.Add(input);
        diagnose.Options.Add(resourcePath);
        diagnose.Options.Add(outputRoot);
        diagnose.Options.Add(format);
        diagnose.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "bounds.diagnose",
            IsJson(parseResult.GetRequiredValue(format)),
            output,
            error,
            () => RunBoundsDiagnosticAsync(
                parseResult.GetRequiredValue(input),
                parseResult.GetRequiredValue(resourcePath),
                parseResult.GetRequiredValue(outputRoot),
                cancellationToken),
            RenderBoundsDiagnosticPublication,
            cancellationToken));
        bounds.Subcommands.Add(diagnose);
        return bounds;
    }

    private static async Task<BoundsDiagnosticPublication> RunBoundsDiagnosticAsync(
        string inputPath,
        string resourcePath,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
        {
            throw Errors.Input(
                "BOUNDS_DIAGNOSTIC_INPUT_NOT_FOUND",
                $"Compiled model input '{fullInputPath}' does not exist.",
                "Provide an existing read-only .vmdl_c file.");
        }

        var length = new FileInfo(fullInputPath).Length;
        if (length is < 16 or > MaximumBoundsDiagnosticInputBytes)
        {
            throw Errors.Input(
                "BOUNDS_DIAGNOSTIC_INPUT_SIZE_UNSUPPORTED",
                $"Compiled model input size {length} is outside the supported range.",
                "Provide a compiled model between 16 bytes and 512 MiB.");
        }

        var normalizedResourcePath = StableIdentity.NormalizePath(resourcePath);
        if (!normalizedResourcePath.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
        {
            throw Errors.Input(
                "BOUNDS_DIAGNOSTIC_RESOURCE_PATH_INVALID",
                $"Logical resource path '{resourcePath}' is not a compiled model path.",
                "Provide the original logical .vmdl_c resource path.");
        }

        var bytes = await File.ReadAllBytesAsync(fullInputPath, cancellationToken).ConfigureAwait(false);
        var artifact = new ArtifactContent(normalizedResourcePath, ContentHash.Compute(bytes), bytes);
        var adapter = new Source2CompiledModelAdapter(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));
        var report = adapter.DiagnoseBounds(artifact);
        var outputDirectory = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(outputDirectory);
        var stem = Regex.Replace(
            Path.GetFileNameWithoutExtension(Path.GetFileName(normalizedResourcePath)),
            "[^A-Za-z0-9._-]",
            "-");
        var prefix = $"{stem}-{artifact.ContentHash.Value[..12]}-bounds";
        var jsonPath = Path.Combine(outputDirectory, $"{prefix}.json");
        var markdownPath = Path.Combine(outputDirectory, $"{prefix}.md");
        await File.WriteAllTextAsync(jsonPath, JsonDefaults.Serialize(report), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, RenderBoundsDiagnosticMarkdown(report), cancellationToken).ConfigureAwait(false);

        return new BoundsDiagnosticPublication(
            report.ResourcePath,
            report.InputSha256,
            jsonPath,
            markdownPath,
            report.Meshes.Length,
            report.Meshes.Count(mesh => mesh.Status == "analyzed"),
            report.Meshes.Sum(mesh => mesh.Bones.Count(bone => !bone.BoundsMatchExistingAffineTolerance)),
            report.Meshes.Sum(mesh => mesh.Bones.Count(bone => !bone.SphereMatchesExistingAffineTolerance)),
            report.Meshes.Sum(mesh => mesh.Bones.Count(bone => !bone.StoredBoundsContainAllVertices)),
            report.Meshes.Sum(mesh => mesh.Bones.Count(bone => !bone.StoredSphereContainsAllVertices)),
            report.CullingInventory.Status,
            report.CullingInventory.Fields.Count(field => field.Status == "verified"),
            report.CullingInventory.Fields.Count(field => field.Status == "absent"),
            report.CullingInventory.Fields.Count(field => field.Status == "unsupported"));
    }

    private static void RenderBoundsDiagnosticPublication(TextWriter output, BoundsDiagnosticPublication publication)
    {
        output.WriteLine($"Resource: {publication.ResourcePath}");
        output.WriteLine($"Meshes/LOD rows: {publication.MeshRows}; analyzed: {publication.AnalyzedMeshRows}");
        output.WriteLine($"Bounds failures: {publication.BoundsFailures}; sphere failures: {publication.SphereFailures}");
        output.WriteLine($"Strict containment misses: boxes {publication.BoundsContainmentFailures}; spheres {publication.SphereContainmentFailures}");
        output.WriteLine($"Culling inventory: {publication.InventoryStatus}; verified {publication.VerifiedInventoryFields}, absent {publication.AbsentInventoryFields}, unsupported {publication.UnsupportedInventoryFields}");
        output.WriteLine($"JSON: {publication.JsonPath}");
        output.WriteLine($"Markdown: {publication.MarkdownPath}");
    }

    private static string RenderBoundsDiagnosticMarkdown(Source2BoundsDiagnosticReport report)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# Read-only bone bounds diagnostic");
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Resource: `{report.ResourcePath}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Input SHA-256: `{report.InputSha256}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Geometry codec: `{report.GeometryCodecIdentity}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Mesh/LOD rows: {report.Meshes.Length}; analyzed: {report.Meshes.Count(mesh => mesh.Status == "analyzed")}");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Culling inventory schema: {report.CullingInventory.SchemaVersion}; status: `{report.CullingInventory.Status}`; fields: {report.CullingInventory.Fields.Count}");
        markdown.AppendLine();

        markdown.AppendLine("## Culling field inventory");
        markdown.AppendLine();
        markdown.AppendLine("Contributor identities hash every member using the declared algorithm and list every participating source buffer. Unsupported coordinate spaces or remaps stay explicit.");
        markdown.AppendLine();
        markdown.AppendLine("| Field | Status | Raw value | Coordinate space / matrix | Contributors | Covered LODs |");
        markdown.AppendLine("|---|---|---|---|---:|---|");
        foreach (var field in report.CullingInventory.Fields)
        {
            var raw = field.RawOriginalValue is null ? "unavailable" : DescribeRawValue(field.RawOriginalValue);
            var space = field.CoordinateSpace is null
                ? "absent"
                : field.CoordinateSpace.Status == "verified"
                    ? $"{field.CoordinateSpace.SpaceId} / {field.CoordinateSpace.MatrixIdentity}"
                    : $"{field.CoordinateSpace.Status} ({field.CoordinateSpace.ReasonCode})";
            var contributors = field.Contributors is { Status: "verified" } set
                ? $"{set.ContributorCount} / {set.IdentityHash} ({set.Sources?.Count ?? 0} buffers)"
                : field.Contributors is null ? "absent" : $"{field.Contributors.Status} ({field.Contributors.ReasonCode})";
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| `{field.Identity.ResourceBlockType}[{field.Identity.ResourceBlockIndex}].{field.Identity.FieldPath}` | {field.Status} | `{raw}` | `{space}` | `{contributors}` | {string.Join(",", field.Identity.CoveredLods)} |");
        }

        markdown.AppendLine();

        foreach (var mesh in report.Meshes)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture, $"## LOD {mesh.Lod}, mesh {mesh.MeshOrdinal}, MDAT block {mesh.ResourceBlockIndex}");
            markdown.AppendLine();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"Status: `{mesh.Status}`; LOD mask: `{mesh.LodMask}`; declared influences: {mesh.DeclaredInfluenceCount}; remap status: `{mesh.BoneRemapStatus}` ({mesh.BoneRemapSource}) `{string.Join(",", mesh.BoneRemap)}`; model bone names: `{mesh.ModelBoneNamesStatus}`");
            if (mesh.Failure is not null)
            {
                markdown.AppendLine();
                markdown.AppendLine(CultureInfo.InvariantCulture, $"Unsupported details: {mesh.Failure}");
                markdown.AppendLine();
                continue;
            }

            markdown.AppendLine();
            markdown.AppendLine("### Participating buffers");
            markdown.AppendLine();
            markdown.AppendLine("| Vertex buffer | Index buffer | VB block | IB block | Vertices | Indices | Stride | Blend indices | Blend weights | Active influences per vertex |");
            markdown.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var buffer in mesh.Buffers)
            {
                var weights = buffer.BlendWeightFormat is null
                    ? "none (rigid)"
                    : $"{buffer.BlendWeightFormat}@{buffer.BlendWeightOffset}";
                var counts = string.Join(", ", buffer.InfluenceCounts.Select(item => $"{item.ActiveInfluenceCount}: {item.VertexCount}"));
                markdown.AppendLine(CultureInfo.InvariantCulture, $"| {buffer.VertexBufferOrdinal} | {buffer.IndexBufferOrdinal} | {buffer.VertexResourceBlockIndex} | {buffer.IndexResourceBlockIndex} | {buffer.VertexCount} | {buffer.IndexCount} | {buffer.Stride} | {buffer.BlendIndexFormat}@{buffer.BlendIndexOffset} | {weights} | {counts} |");
            }

            markdown.AppendLine();
            markdown.AppendLine("### Per-bone results");
            markdown.AppendLine();
            markdown.AppendLine("| Mesh bone → model bone | Influenced vertices | Sphere stored / required / delta | Sphere contains / tolerance match | Bbox stored min..max | Bbox calculated min..max | Contains / exact / tolerance match |");
            markdown.AppendLine("|---|---:|---:|---|---|---|---|");
            foreach (var bone in mesh.Bones)
            {
                markdown.AppendLine(CultureInfo.InvariantCulture, $"| `{bone.BoneName}` ({bone.BoneIndex}) → `{bone.ResolvedModelBoneName}` ({bone.ResolvedModelBoneIndex}) | {bone.InfluencedVertexCount} | {F(bone.StoredSphereRadius)} / {F(bone.CalculatedRequiredSphereRadius)} / {F(bone.SphereDelta)} | {bone.StoredSphereContainsAllVertices} / {bone.SphereMatchesExistingAffineTolerance} | `{Bounds(bone.StoredBounds)}` | `{Bounds(bone.CalculatedBounds)}` | {bone.StoredBoundsContainAllVertices} / {bone.BoundsExactlyEqual} / {bone.BoundsMatchExistingAffineTolerance} |");
            }

            foreach (var bone in mesh.Bones.Where(item =>
                         !item.StoredBoundsContainAllVertices
                         || !item.StoredSphereContainsAllVertices
                         || !item.BoundsMatchExistingAffineTolerance
                         || !item.SphereMatchesExistingAffineTolerance))
            {
                markdown.AppendLine();
                markdown.AppendLine(CultureInfo.InvariantCulture, $"#### Failure witnesses: `{bone.BoneName}` ({bone.BoneIndex})");
                markdown.AppendLine();
                if ((!bone.StoredSphereContainsAllVertices || !bone.SphereMatchesExistingAffineTolerance)
                    && bone.SphereExtremalVertex is { } sphereVertex)
                {
                    markdown.AppendLine(CultureInfo.InvariantCulture, $"Sphere: required {F(bone.CalculatedRequiredSphereRadius)}, stored {F(bone.StoredSphereRadius)}, delta {F(bone.SphereDelta)}; stored sphere contains all influenced vertices: {bone.StoredSphereContainsAllVertices}; existing affine tolerance match: {bone.SphereMatchesExistingAffineTolerance}. Extremal vertex: {DescribeVertex(sphereVertex)}");
                    markdown.AppendLine();
                }

                foreach (var extremum in bone.Extrema.Where(item =>
                             !item.StoredContainsExtremum || !item.MatchesExistingAffineTolerance))
                {
                    markdown.AppendLine(CultureInfo.InvariantCulture, $"Bbox {extremum.Axis} {extremum.Side}: calculated {F(extremum.CalculatedValue)}, stored {F(extremum.StoredValue)}, delta {F(extremum.Delta)}; stored bound contains extremum: {extremum.StoredContainsExtremum}; existing affine tolerance match: {extremum.MatchesExistingAffineTolerance}. Vertex: {DescribeVertex(extremum.Vertex)}");
                    markdown.AppendLine();
                }
            }
        }

        return markdown.ToString();
    }

    private static string DescribeVertex(Source2BoundsDiagnosticVertex vertex)
    {
        var resolved = string.Join(", ", vertex.ResolvedInfluences.Select(item =>
            $"slot {item.Slot}: raw {item.RawBlendIndex}, weight {item.RawBlendWeight} -> mesh bone {item.MeshBoneIndex} '{item.MeshBoneName}' -> model bone {item.ResolvedBoneIndex} '{item.BoneName}'"));
        return $"VB{vertex.VertexBufferOrdinal} vertex {vertex.VertexOrdinal}; format {vertex.BlendIndexFormat}; raw indices [{string.Join(",", vertex.RawBlendIndices)}]; raw weights [{string.Join(",", vertex.RawBlendWeights)}]; active {vertex.ActiveInfluenceCount}; responsible mesh bone {vertex.MeshBoneIndex} '{vertex.MeshBoneName}', resolved model bone {vertex.ResolvedBoneIndex} '{vertex.BoneName}'; original {Vector(vertex.OriginalPosition)}; bone-local {Vector(vertex.CalculatedBoneLocalPosition)}; resolved [{resolved}]";
    }

    private static string Bounds(GeometryBounds bounds) => $"{Vector(bounds.Min)}..{Vector(bounds.Max)}";

    private static string DescribeRawValue(CullingRawFieldValue value) => value.Kind switch
    {
        "aabb_min_max" => $"min {Vector(value.Minimum!)} max {Vector(value.Maximum!)}",
        "aabb_center_half_extents" => $"center {Vector(value.Center!)} half-extents {Vector(value.HalfExtents!)}",
        "sphere_radius" => $"radius {F(value.Radius!.Value)}",
        _ => value.Kind,
    };

    private static string Vector(TransformVector3 vector) =>
        $"({F(vector.X)}, {F(vector.Y)}, {F(vector.Z)})";

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record BoundsDiagnosticPublication(
        string ResourcePath,
        string InputSha256,
        string JsonPath,
        string MarkdownPath,
        int MeshRows,
        int AnalyzedMeshRows,
        int BoundsFailures,
        int SphereFailures,
        int BoundsContainmentFailures,
        int SphereContainmentFailures,
        string InventoryStatus,
        int VerifiedInventoryFields,
        int AbsentInventoryFields,
        int UnsupportedInventoryFields);
}

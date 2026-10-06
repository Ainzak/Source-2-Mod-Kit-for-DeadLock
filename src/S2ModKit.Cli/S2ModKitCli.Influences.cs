using System.CommandLine;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static Command CreateInfluencesCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("influences", "Report read-only skinning and dependency facts; no mutation permission.");
        var diagnose = new Command("diagnose", "Summarize complete-buffer influences and optional hypothetical position-field effects.");
        var input = RequiredStringOption("--input", "Immutable compiled .vmdl_c source file.");
        var resource = RequiredStringOption("--resource-path", "Original logical .vmdl_c resource path.");
        var root = RequiredStringOption("--output-root", "Configured directory for an immutable diagnostic JSON report.");
        var field = new Option<string?>("--field-options") { Description = "Optional existing version-1 common-field options; probes all reported buffers, without selecting a mutation." };
        var format = CreateFormatOption();
        diagnose.Options.Add(input);
        diagnose.Options.Add(resource);
        diagnose.Options.Add(root);
        diagnose.Options.Add(field);
        diagnose.Options.Add(format);
        diagnose.SetAction((parse, token) => ExecuteAsync("influences.diagnose", IsJson(parse.GetRequiredValue(format)), output, error,
            () => RunInfluenceDiagnosticAsync(parse.GetRequiredValue(input), parse.GetRequiredValue(resource),
                parse.GetRequiredValue(root), parse.GetValue(field), token),
            (writer, result) =>
            {
                writer.WriteLine($"Influences: {result.ReportedMeshes}/{result.MeshRows} mesh/LOD rows reported; {result.BufferRows} buffers.");
                writer.WriteLine($"Procedural vertex memberships: {result.ProceduralVertexMemberships}; changed by position probe: {result.ChangedProceduralVertexMemberships?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not probed"}.");
                writer.WriteLine("Advisory only; dependency closure and frame effects are unverified. Use the exact planner for admission.");
                writer.WriteLine($"JSON: {result.JsonPath}");
            }, token));
        command.Subcommands.Add(diagnose);
        return command;
    }

    private static async Task<InfluenceDiagnosticPublication> RunInfluenceDiagnosticAsync(
        string inputPath, string resourcePath, string outputRoot, string? optionsPath, CancellationToken token)
    {
        var sourcePath = Path.GetFullPath(inputPath);
        if (!File.Exists(sourcePath))
            throw Errors.Input("INFLUENCE_INPUT_NOT_FOUND", "Compiled input does not exist.", "Supply an immutable .vmdl_c source.");
        if (new FileInfo(sourcePath).Length is < 16 or > MaximumBoundsDiagnosticInputBytes)
            throw Errors.Input("INFLUENCE_INPUT_SIZE_UNSUPPORTED", "Input must be between 16 bytes and 512 MiB.", "Supply a bounded source.");
        var logical = StableIdentity.NormalizePath(resourcePath);
        if (!logical.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase))
            throw Errors.Input("INFLUENCE_RESOURCE_PATH_INVALID", "The logical path is not a compiled model.", "Supply the original .vmdl_c path.");
        CoordinatedScaffoldOptions? options = null;
        if (optionsPath is not null)
        {
            if (!File.Exists(optionsPath) || new FileInfo(optionsPath).Length > 2 * 1024 * 1024)
                throw Errors.Input("INFLUENCE_OPTIONS_INPUT_INVALID", "Field options are missing or exceed 2 MiB.", "Supply existing typed common-field options.");
            options = CoordinatedSelection.ReadOptions(await File.ReadAllBytesAsync(optionsPath, token).ConfigureAwait(false));
        }
        var bytes = await File.ReadAllBytesAsync(sourcePath, token).ConfigureAwait(false);
        var artifact = new ArtifactContent(logical, ContentHash.Compute(bytes), bytes);
        var adapter = new Source2CompiledModelAdapter(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));
        var report = adapter.DiagnoseInfluences(artifact, options);
        var json = JsonDefaults.Serialize(report);
        var destination = Path.Combine(Path.GetFullPath(outputRoot), $"influences-{ContentHash.Compute(Encoding.UTF8.GetBytes(json)).Value}.json");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        var encoded = Encoding.UTF8.GetBytes(json);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(encoded, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(encoded))
                    throw Errors.Input("INFLUENCE_REPORT_OUTPUT_CONFLICT", "An existing report differs from the content-addressed output.", "Choose a separate output root; existing data is preserved.");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var buffers = report.Meshes.SelectMany(m => m.Buffers).ToArray();
        return new(destination, report.InputHash, report.FieldOptionsHash, report.Meshes.Count,
            report.Meshes.Count(m => m.Status == "reported"), buffers.Length, buffers.Sum(b => b.ProceduralVertices),
            options is null ? null : buffers.Sum(b => b.FieldEffects!.ChangedProceduralVertices), false);
    }

    private sealed record InfluenceDiagnosticPublication(string JsonPath, ContentHash InputHash, ContentHash? FieldOptionsHash,
        int MeshRows, int ReportedMeshes, int BufferRows, int ProceduralVertexMemberships,
        int? ChangedProceduralVertexMemberships, bool MutationPermission);
}

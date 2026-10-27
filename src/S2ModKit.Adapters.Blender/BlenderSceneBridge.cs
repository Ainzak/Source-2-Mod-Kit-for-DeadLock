using System.Diagnostics;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Blender;

public sealed record BlenderSceneResult(string OutputPath, ContentHash OutputHash, string Status);

/// <summary>Runs the explicit installed Blender Python bridge; no shell or compiled-resource writing.</summary>
public static class BlenderSceneBridge
{
    public static async Task<BlenderSceneResult> RunAsync(string executable, string bridgeScript, string action, string bundlePath,
        ContentHash bundleHash, string? scenePath, string outputPath, CancellationToken token = default)
    {
        if (action is not ("create" or "extract") || !File.Exists(executable) || !File.Exists(bridgeScript))
            throw Errors.Input("BLENDER_CONFIGURATION_INVALID", "Require installed Blender and the bridge script.", "Pass explicit readable executable/script paths.");
        if (new FileInfo(bundlePath).Length > PositionAuthoringJson.MaximumArtifactBytes) throw PositionAuthoringJson.Invalid("IMPORT_JSON_BUDGET", "Bundle exceeds budget.");
        var bundleBytes = await File.ReadAllBytesAsync(bundlePath, token).ConfigureAwait(false);
        if (ContentHash.Compute(bundleBytes) != bundleHash) throw PositionAuthoringJson.Invalid("IMPORT_BUNDLE_HASH", "Trusted bundle hash differs.");
        _ = PositionAuthoringJson.Read<PositionAuthoringBundle>(bundleBytes);
        var output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw Errors.Input("IMPORT_OUTPUT_EXISTS", "Scene/return output already exists.", "Choose an absent output file.");
        if (action == "extract" && (scenePath is null || !File.Exists(scenePath))) throw Errors.Input("BLENDER_SCENE_MISSING", "Extraction needs a readable scene.", "Pass --scene.");
        ContentHash? sceneHash = action == "extract" ? HashFile(scenePath!) : null;
        var parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".s2mod-scene-" + Guid.NewGuid().ToString("N") + (action == "create" ? ".blend" : ".json"));
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--background");
        if (action == "create") start.ArgumentList.Add("--factory-startup"); else start.ArgumentList.Add(Path.GetFullPath(scenePath!));
        foreach (var argument in new[] { "--python-exit-code", "3", "--python", Path.GetFullPath(bridgeScript), "--", action, "--bundle", Path.GetFullPath(bundlePath),
            "--bundle-sha256", bundleHash.Value, "--output", temporary }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Blender did not start.");
            using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var stdout = Tail(process.StandardOutput); var stderr = Tail(process.StandardError);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var logs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temporary))
                throw Errors.Verification("BLENDER_SCENE_REJECTED", string.Join("\n", logs), "Correct the scene; no output was published.");
            if (HashFile(bundlePath) != bundleHash || (sceneHash is not null && HashFile(scenePath!) != sceneHash))
                throw Errors.Verification("BLENDER_INPUT_DRIFT", "Scene or trusted bundle changed during automation.", "Reject output and use immutable inputs.");
            if (action == "extract")
            {
                if (new FileInfo(temporary).Length > PositionAuthoringJson.MaximumArtifactBytes) throw PositionAuthoringJson.Invalid("IMPORT_JSON_BUDGET", "Return exceeds budget.");
                _ = PositionAuthoringJson.Read<PositionAuthoringDelta>(await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false));
            }
            var hash = HashFile(temporary); token.ThrowIfCancellationRequested(); File.Move(temporary, output);
            return new(output, hash, action == "create" ? "source_bound_scene_created" : "positions_extracted_requires_independent_validation");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static ContentHash HashFile(string path) { using var stream = File.OpenRead(path); return ContentHash.Compute(stream); }
    private static async Task<string> Tail(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[1024]; int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        { text.Append(buffer, 0, read); if (text.Length > 8192) text.Remove(0, text.Length - 8192); }
        return text.ToString();
    }
}

using System.Diagnostics;
using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>A draw call's colour texture: mesh, material, the material's <c>g_tColor</c> path and the archive it resolves from.</summary>
public sealed record ColorTextureUse(string Mesh, string Material, string Texture, string Archive);

/// <summary>
/// Texture work with ValveResourceFormat (decode, the compiler definition). Recolored pixels are written back in one of
/// two ways:
/// <list type="bullet">
/// <item>in place (default, no Valve tools): the original compiled texture is kept and only its pixel data is replaced,
/// every mip level re-encoded in the texture's own format with BCnEncoder.NET;</item>
/// <item>with the Source 2 SDK resource compiler (<paramref name="resourceCompiler"/>, with the SDK's <c>game</c> folder
/// <paramref name="sdkGame"/> mounted for its citadel and core files): the texture is rebuilt from its own definition
/// (TextureExtract.ToValveTexture) under its own name.</item>
/// </list>
/// </summary>
public sealed class VrfTextureWorkshop(string? resourceCompiler = null, string? sdkGame = null) : ITextureWorkshop
{
    public Task<DecodedTexture> DecodeAsync(IReadOnlyList<string> archives, string texturePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var logical = texturePath + "_c";
        var (bytes, archive) = Find(archives, logical)
            ?? throw Errors.Input("RECOLOR_TEXTURE_MISSING", $"'{logical}' is not in the archives.", "Check the texture path and the --game/--addon archives.");
        using var resource = new Resource { FileName = logical };
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not Texture texture)
        {
            throw Errors.Input("RECOLOR_NOT_A_TEXTURE", $"'{logical}' is not a texture.", "Pass a colour texture (.vtex).");
        }

        using var bitmap = texture.GenerateBitmap();
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.BytesSize];
        unsafe
        {
            fixed (byte* target = rgba)
            {
                if (!bitmap.PeekPixels().ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0))     // RGBA8, alpha not premultiplied
                {
                    throw Errors.Unsupported("RECOLOR_TEXTURE_DECODE", $"'{logical}' could not be converted to RGBA.", "Report the texture.");
                }
            }
        }

        if (bitmap.Width != texture.Width || bitmap.Height != texture.Height)
        {
            rgba = Fill(rgba, bitmap.Width, bitmap.Height, texture.Width, texture.Height);   // a small picture in a larger block (1x1 in 4x4)
        }

        var definition = new TextureExtract(resource) { IgnoreVtexFile = false }.ToValveTexture();
        return Task.FromResult(new DecodedTexture(texturePath, texture.Width, texture.Height, rgba, definition, texture.Format.ToString(),
            texture.NumMipLevels, archive, ContentHash.Compute(bytes)));
    }

    /// <summary>The picture stretched (nearest texel) to the texture's stored size, so its pixels match the size the texture declares.</summary>
    private static byte[] Fill(byte[] rgba, int width, int height, int toWidth, int toHeight)
    {
        var output = new byte[toWidth * toHeight * 4];
        for (var y = 0; y < toHeight; y++)
        {
            for (var x = 0; x < toWidth; x++)
            {
                var source = ((Math.Min(height - 1, y * height / toHeight) * width) + Math.Min(width - 1, x * width / toWidth)) * 4;
                Array.Copy(rgba, source, output, ((y * toWidth) + x) * 4, 4);
            }
        }

        return output;
    }

    public Task<float[]> ReadMaskAsync(string path, int width, int height, CancellationToken cancellationToken = default)
    {
        using var source = SKBitmap.Decode(path) ?? throw Errors.Input("RECOLOR_MASK_UNREADABLE", $"'{path}' is not an image.", "Pass a PNG mask.");
        using var sized = source.Width == width && source.Height == height ? source.Copy() : source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        var weights = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                weights[y * width + x] = sized.GetPixel(x, y).Red / 255f;
            }
        }

        return Task.FromResult(weights);
    }

    public async Task<byte[]> CompileAsync(DecodedTexture texture, byte[] rgba, string workDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (resourceCompiler is null || sdkGame is null)
        {
            return InPlaceTexture.Write(Find([texture.Archive], texture.Path + "_c")?.Bytes
                ?? throw Errors.Input("RECOLOR_TEXTURE_MISSING", $"'{texture.Path}_c' is no longer in '{texture.Archive}'.", "Run the recolor again."),
                texture.Path + "_c", texture.Width, texture.Height, rgba);
        }

        var content = Path.Combine(workDirectory, "content", "lab");
        var game = Path.Combine(workDirectory, "game", "lab");
        Directory.CreateDirectory(game);
        var definitionPath = Path.Combine(content, texture.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
        await File.WriteAllTextAsync(definitionPath, texture.Definition, cancellationToken).ConfigureAwait(false);
        var image = InputImage(texture.Definition)
            ?? throw Errors.Unsupported("RECOLOR_DEFINITION", $"The definition of '{texture.Path}' names no input image.", "Report the texture.");
        var imagePath = Path.Combine(content, image.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, EncodePng(texture.Width, texture.Height, rgba), cancellationToken).ConfigureAwait(false);
        var mounts = new[] { Path.Combine(sdkGame, "citadel"), Path.Combine(sdkGame, "core") }.Select(path => Mount(Path.Combine(workDirectory, "game"), path));
        await File.WriteAllTextAsync(Path.Combine(game, "gameinfo.gi"),
            "\"GameInfo\" { game \"S2ModKit recolor\" FileSystem { SearchPaths { Mod \"lab\" Write \"lab\" Game \"lab\" "
            + string.Join(' ', mounts.Select(path => $"Game \"{path}\"")) + " } } }", cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(resourceCompiler) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(definitionPath);
        start.ArgumentList.Add("-game");
        start.ArgumentList.Add(game);
        using var process = Process.Start(start) ?? throw Errors.Verification("RECOLOR_COMPILER_START", "The resource compiler did not start.", "Check --resourcecompiler.");
        var log = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false) + await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "compile.log"), log, cancellationToken).ConfigureAwait(false);
        var compiled = Path.Combine(game, (texture.Path + "_c").Replace('/', Path.DirectorySeparatorChar));
        if (!log.Contains("1 compiled, 0 failed", StringComparison.Ordinal) || !File.Exists(compiled))
        {
            throw Errors.Verification("RECOLOR_COMPILE_FAILED", $"'{texture.Path}' did not compile (see {Path.Combine(workDirectory, "compile.log")}).", "Check the SDK paths.");
        }

        return await File.ReadAllBytesAsync(compiled, cancellationToken).ConfigureAwait(false);
    }

    public TextureFacts Inspect(byte[] compiled)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(compiled));
        var texture = resource.DataBlock as Texture ?? throw Errors.Verification("RECOLOR_OUTPUT_NOT_TEXTURE", "The compiled file is not a texture.", "Report the texture.");
        return new TextureFacts(texture.Width, texture.Height, texture.Format.ToString(), texture.NumMipLevels);
    }

    public byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Colour textures of a model's draw calls (material <c>g_tColor</c>), archives in search order.</summary>
    public static IReadOnlyList<ColorTextureUse> ListColorTextures(IReadOnlyList<string> archives, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var model = modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c";
        var (bytes, _) = Find(archives, model) ?? throw Errors.Input("RECOLOR_MODEL_MISSING", $"'{model}' is not in the archives.", "Check the model path.");
        using var resource = new Resource { FileName = model };
        resource.Read(new MemoryStream(bytes));
        var data = resource.DataBlock as Model ?? throw Errors.Input("RECOLOR_NOT_A_MODEL", $"'{model}' is not a model.", "Pass a .vmdl path.");
        var uses = new List<ColorTextureUse>();
        foreach (var (mesh, _, name, _) in data.GetEmbeddedMeshesForLod(0))
        {
            foreach (var sceneObject in Items(mesh.Data, "m_sceneObjects"))
            {
                foreach (var draw in Items(sceneObject, "m_drawCalls"))
                {
                    var material = draw["m_material"].ToString(CultureInfo.InvariantCulture);
                    if (Find(archives, material.EndsWith("_c", StringComparison.Ordinal) ? material : material + "_c") is not { } found)
                    {
                        continue;
                    }

                    using var matResource = new Resource();
                    matResource.Read(new MemoryStream(found.Bytes));
                    if (matResource.DataBlock is Material mat && mat.TextureParams.TryGetValue("g_tColor", out var color))
                    {
                        var archive = Find(archives, color + "_c")?.Archive ?? "-";
                        uses.Add(new ColorTextureUse(name, material, color, archive));
                    }
                }
            }
        }

        return uses;
    }

    private static IEnumerable<ValveKeyValue.KVObject> Items(ValveKeyValue.KVObject parent, string key)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            yield break;
        }

        for (var i = 0; i < value.Count; i++)
        {
            yield return value[i];
        }
    }

    private static (byte[] Bytes, string Archive)? Find(IReadOnlyList<string> archives, string logical)
    {
        foreach (var path in archives)
        {
            using var package = new Package();
            package.Read(path);
            if (package.FindEntry(logical) is { } entry)
            {
                package.ReadEntry(entry, out var bytes);
                return (bytes, path);
            }
        }

        return null;
    }

    private static string? InputImage(string definition)
    {
        const string key = "\"m_fileName\" \"string\" \"";
        var at = definition.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + key.Length;
        return definition[start..definition.IndexOf('"', start)];
    }

    /// <summary>A search path the compiler accepts: relative to the game folder on the same drive, else absolute.</summary>
    private static string Mount(string from, string to)
    {
        var relative = Path.GetRelativePath(from, to);
        return (Path.IsPathRooted(relative) ? Path.GetFullPath(to) : relative).Replace('\\', '/');
    }

    public override string ToString() => resourceCompiler is null ? "in place (BCnEncoder.NET)" : string.Create(CultureInfo.InvariantCulture, $"{resourceCompiler} ({sdkGame})");
}

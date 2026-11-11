using S2ModKit.Application;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// A part's recolor mask without a preview window (no OpenGL): the texels of its colour texture the part covers (from the level-0
/// meshes, as the preview draws them), optionally limited to texels near a colour (the Colour tab's "only this colour"). Used to
/// re-apply a saved recolor onto a new game version; the preview computes the same mask.
/// </summary>
public static class PartMasks
{
    /// <summary>Soft edge of a colour range (beyond the tolerance, the weight falls to 0 over this distance).</summary>
    public const float RangeSoftness = 0.05f;

    public static float[] Mask(byte[] compiledModel, string modelPath, int part, int width, int height, byte[]? rgba = null, RgbColor? target = null, float tolerance = 0,
        IReadOnlyCollection<int>? recoloredPieces = null)
    {
        var picker = Picker(compiledModel, modelPath);
        if (part < 0 || part >= picker.Total)
        {
            throw Domain.Errors.Input("MASK_PART_RANGE", $"The model has {picker.Total} parts and pieces; part {part + 1} is not one of them.", "The model changed; recolor the part again.");
        }

        var mask = picker.Mask(part, width, height);
        if (recoloredPieces is not null && part < picker.Count)
        {
            Exclude(mask, picker.PiecesOf(part).Where(recoloredPieces.Contains).Select(piece => picker.Mask(piece, width, height)));
        }

        if (target is { } colour && rgba is not null)
        {
            LimitToColour(mask, rgba, colour, tolerance);
        }

        return mask;
    }

    /// <summary>The level-0 draw calls (parts) of the model, and for a piece (index from that count up) its part and triangle numbers.</summary>
    public static (int Parts, int Part, int[]? Triangles) PieceOrPart(byte[] compiledModel, string modelPath, int index)
    {
        var picker = Picker(compiledModel, modelPath);
        if (index < 0 || index >= picker.Total)
        {
            throw Domain.Errors.Input("MASK_PART_RANGE", $"The model has {picker.Total} parts and pieces; part {index + 1} is not one of them.", "The model changed; set the part again.");
        }

        if (index < picker.Count)
        {
            return (picker.Count, index, null);
        }

        var (part, triangles) = picker.PieceTriangles(index);
        return (picker.Count, part, triangles);
    }

    /// <summary>The separate meshes of the model's parts (see <see cref="PreviewSession.Pieces"/>), without a window.</summary>
    public static IReadOnlyList<PartPiece> Pieces(byte[] compiledModel, string modelPath)
    {
        var picker = Picker(compiledModel, modelPath);
        return [.. Enumerable.Range(0, picker.Count).SelectMany(part => picker.PiecesOf(part).Select((piece, number) => new PartPiece(piece, part, number + 1, picker.TriangleCount(piece))))];
    }

    private static PartPicker Picker(byte[] compiledModel, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(compiledModel);
        using var resource = new Resource { FileName = modelPath };
        resource.Read(new MemoryStream(compiledModel));
        var model = resource.DataBlock as Model ?? throw Domain.Errors.Input("MASK_NOT_A_MODEL", $"'{modelPath}' is not a model.", "Pass a compiled .vmdl_c.");
        return new PartPicker(model, model.GetEmbeddedMeshesForLod(0).Select(item => item.MeshIndex).ToHashSet());
    }

    /// <summary>Takes the pieces' own texels out of their part's mask: a piece with its own colour is not recolored by its part too.</summary>
    public static void Exclude(float[] mask, IEnumerable<float[]> pieces)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(pieces);
        foreach (var piece in pieces)
        {
            for (var i = 0; i < mask.Length; i++)
            {
                mask[i] = Math.Max(0, mask[i] - piece[i]);
            }
        }
    }

    /// <summary>Keeps only texels whose original colour is within <paramref name="tolerance"/> of <paramref name="target"/> (average RGB
    /// distance, soft edge); <paramref name="rgba"/> is the texture at the mask's size.</summary>
    public static void LimitToColour(float[] mask, byte[] rgba, RgbColor target, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(rgba);
        for (var t = 0; t < mask.Length; t++)
        {
            if (mask[t] <= 0)
            {
                continue;
            }

            var (dr, dg, db) = ((rgba[t * 4] / 255f) - target.R, (rgba[(t * 4) + 1] / 255f) - target.G, (rgba[(t * 4) + 2] / 255f) - target.B);
            var distance = MathF.Sqrt(((dr * dr) + (dg * dg) + (db * db)) / 3);
            mask[t] *= Math.Clamp(1 - ((distance - tolerance) / RangeSoftness), 0, 1);
        }
    }
}

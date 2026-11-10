using S2ModKit.Adapters.Source2;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>One reshaped part for a build: its material, the change, the part's bind-pose box (the pivot the preview used), the largest
/// vertex movement the preview measured and the level the part allowed.</summary>
internal sealed record PartShapeEdit(int Part, string Material, PartShape Shape, System.Numerics.Vector3 BoundsMin, System.Numerics.Vector3 BoundsMax, float Displacement, ShapeLevel Level);

/// <summary>
/// Builds a reshaped model with the direct reshape (<see cref="CompiledPartReshape"/>, <c>shape_part@1</c>): every part's own vertices
/// get the same bind-pose change the preview showed (around the same pivot box), in every level of detail, and the model's bounds are
/// recomputed. One pass for all parts.
/// </summary>
internal static class ShapeModelBuilder
{
    public static byte[] Build(byte[] model, string logicalPath, IReadOnlyList<PartShapeEdit> shapes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(shapes);
        return CompiledPartReshape.Apply(model, logicalPath,
            [.. shapes.Select(edit => new PartReshape(edit.Part, edit.Shape.Matrix(edit.BoundsMin, edit.BoundsMax)))], ShapeCapabilities.CodecPath());
    }
}

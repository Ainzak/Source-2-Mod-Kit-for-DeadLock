using System.Numerics;
using OpenTK.Graphics.OpenGL;
using S2ModKit.Application;

namespace S2ModKit.Adapters.Preview;

/// <summary>Live whole-part shape changes in the preview: the part's own vertices are transformed in bind pose (positions, and
/// normals stored as plain floats) and written into the renderer's vertex buffer; skinning then animates them as before. The model
/// and the archives are not changed.</summary>
public sealed partial class PreviewSession
{
    private readonly Dictionary<int, PartShape> partShapes = [];

    /// <summary>The parts reshaped in the preview, by index.</summary>
    public IReadOnlyDictionary<int, PartShape> PartShapes => partShapes;

    /// <summary>Null when part <paramref name="index"/> can be reshaped in the preview, else why not, in plain words.</summary>
    public string? PartShapeRefusal(int index) =>
        geometry.Count != Parts.Count || index < 0 || index >= geometry.Count ? "The preview cannot match this part to its vertices." : geometry[index].Refusal;

    /// <summary>Part <paramref name="index"/>'s bind-pose box (the pivot the preview and the build share); null when it cannot be reshaped.</summary>
    public (Vector3 Min, Vector3 Max)? PartBounds(int index) => PartShapeRefusal(index) is null ? geometry[index].Bounds() : null;

    /// <summary>The largest distance any of part <paramref name="index"/>'s vertices moves under <paramref name="shape"/> (model units).</summary>
    public float PartShapeDisplacement(int index, PartShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (PartBounds(index) is not { } bounds)
        {
            return 0;
        }

        var part = geometry[index];
        var matrix = shape.Matrix(bounds.Min, bounds.Max);
        var largest = 0f;
        for (var v = part.Min; v <= part.Max; v++)
        {
            var p = part.Position(part.Data, v);
            largest = MathF.Max(largest, Vector3.Distance(p, Vector3.Transform(p, matrix)));
        }

        return largest;
    }

    /// <summary>Reshapes part <paramref name="index"/> (null or identity: back to its compiled shape); returns the refusal, if any.
    /// Must run on the render thread.</summary>
    public string? SetPartShape(int index, PartShape? shape)
    {
        if (PartShapeRefusal(index) is { } refusal)
        {
            return refusal;
        }

        var part = geometry[index];
        var (min, max) = part.Bounds();
        var matrix = shape is null || shape.IsIdentity ? Matrix4x4.Identity : shape.Matrix(min, max);
        var normals = Matrix4x4.Invert(matrix, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
        var first = part.BindOffset + (part.Min * part.Stride);
        var bytes = part.Data.AsSpan(first, (part.Max - part.Min + 1) * part.Stride).ToArray();
        if (!matrix.IsIdentity)
        {
            for (var v = 0; v <= part.Max - part.Min; v++)
            {
                var at = v * part.Stride;
                PartGeometry.Part.WriteVector(bytes, at + part.PositionOffset, Vector3.Transform(PartGeometry.Part.ReadVector(bytes, at + part.PositionOffset), matrix));
                if (part.NormalPacked)
                {
                    var packed = BitConverter.ToUInt32(bytes, at + part.NormalOffset);
                    BitConverter.TryWriteBytes(bytes.AsSpan(at + part.NormalOffset, 4), S2ModKit.Application.PackedNormalTangent.Turn(packed, normals, matrix));
                }
                else if (part.NormalOffset >= 0)
                {
                    var n = Vector3.TransformNormal(PartGeometry.Part.ReadVector(bytes, at + part.NormalOffset), normals);
                    PartGeometry.Part.WriteVector(bytes, at + part.NormalOffset, n.LengthSquared() > 0 ? Vector3.Normalize(n) : n);
                }
            }
        }

        var target = Draw(index).VertexBuffers.FirstOrDefault(buffer => buffer.BufferIndex == part.BufferIndex);
        if (target.Handle == 0)
        {
            return "The preview cannot find this part's vertex buffer.";
        }

        GL.NamedBufferSubData(target.Handle, checked((IntPtr)(target.Offset + (part.Min * part.Stride))), bytes.Length, bytes);
        picker.SetShape(index, matrix);
        ink.SetShape(index, matrix);                                        // the outline shell follows the part
        if (matrix.IsIdentity)
        {
            partShapes.Remove(index);
        }
        else
        {
            partShapes[index] = shape!;
        }

        return null;
    }
}

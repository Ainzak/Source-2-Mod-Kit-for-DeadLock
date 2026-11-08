using System.Globalization;
using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Where each part's own vertices live (draw calls of LOD 0, in <see cref="PreviewSession.Parts"/> order): the vertex buffer, its
/// bind offset and stride, the range of vertices the part's triangles use, and the position and normal fields. A part whose range
/// overlaps another part's in the same buffer cannot be reshaped alone; positions must be stored as plain floats to be rewritten.
/// </summary>
internal sealed class PartGeometry
{
    private readonly List<Part> parts = [];

    public PartGeometry(Model model, IReadOnlySet<int> activeMeshes)
    {
        foreach (var (mesh, meshIndex, _, _) in model.GetEmbeddedMeshesForLod(0))
        {
            if (!activeMeshes.Contains(meshIndex))
            {
                continue;
            }

            var meshParts = new List<Part>();
            foreach (var sceneObject in Items(mesh.Data, "m_sceneObjects"))
            {
                foreach (var draw in Items(sceneObject, "m_drawCalls"))
                {
                    meshParts.Add(Part.Read(mesh.VBIB, draw));
                }
            }

            foreach (var part in meshParts)
            {
                part.Shared = meshParts.Any(other => other != part && other.BufferIndex == part.BufferIndex && part.Min <= other.Max && other.Min <= part.Max);
            }

            parts.AddRange(meshParts);
        }
    }

    public int Count => parts.Count;

    public Part this[int index] => parts[index];

    private static IEnumerable<KVObject> Items(KVObject parent, string key)
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

    internal sealed class Part
    {
        public int BufferIndex { get; private init; }

        public byte[] Data { get; private init; } = [];

        public int BindOffset { get; private init; }

        public int Stride { get; private init; }

        public int Min { get; private init; }

        public int Max { get; private init; }

        public int PositionOffset { get; private init; } = -1;

        public bool PositionIsFloat { get; private init; }

        public int NormalOffset { get; private init; } = -1;

        public bool Shared { get; set; }

        /// <summary>Null when the part can be reshaped in the preview, else the reason in plain words.</summary>
        public string? Refusal => PositionOffset < 0 || Max < Min ? "This part has no vertices the preview can move."
            : Shared ? "This part shares its vertices with another part."
            : !PositionIsFloat ? "This part's vertices are stored in a packed format the preview cannot change yet."
            : null;

        /// <summary>The bind-pose box of the part's own vertices.</summary>
        public (Vector3 Min, Vector3 Max) Bounds()
        {
            var (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            for (var v = Min; v <= Max; v++)
            {
                var p = Position(Data, v);
                (lo, hi) = (Vector3.Min(lo, p), Vector3.Max(hi, p));
            }

            return (lo, hi);
        }

        public Vector3 Position(byte[] data, int v) => ReadVector(data, BindOffset + (v * Stride) + PositionOffset);

        public static Vector3 ReadVector(byte[] data, int at) =>
            new(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8));

        public static void WriteVector(byte[] data, int at, Vector3 value)
        {
            BitConverter.TryWriteBytes(data.AsSpan(at), value.X);
            BitConverter.TryWriteBytes(data.AsSpan(at + 4), value.Y);
            BitConverter.TryWriteBytes(data.AsSpan(at + 8), value.Z);
        }

        public static Part Read(VBIB vbib, KVObject draw)
        {
            var vertexRef = draw["m_vertexBuffers"][0];
            var bufferIndex = I(vertexRef, "m_hBuffer");
            var buffer = vbib.VertexBuffers[bufferIndex];
            var indexRef = draw["m_indexBuffer"];
            var indexBuffer = vbib.IndexBuffers[I(indexRef, "m_hBuffer")];
            var indexBind = indexRef.TryGetValue("m_nBindOffsetBytes", out var ib) ? (int)ib.ToInt64(CultureInfo.InvariantCulture) : 0;
            var (start, count, baseVertex, size) = (I(draw, "m_nStartIndex"), I(draw, "m_nIndexCount"), I(draw, "m_nBaseVertex"), (int)indexBuffer.ElementSizeInBytes);
            var (min, max) = (int.MaxValue, -1);
            for (var i = 0; i < count; i++)
            {
                var at = indexBind + ((start + i) * size);
                var v = baseVertex + (size == 2 ? BitConverter.ToUInt16(indexBuffer.Data, at) : (int)BitConverter.ToUInt32(indexBuffer.Data, at));
                (min, max) = (Math.Min(min, v), Math.Max(max, v));
            }

            var position = buffer.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "POSITION" && f.SemanticIndex == 0);
            var normal = buffer.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "NORMAL" && f.SemanticIndex == 0);
            static bool Float3(VBIB.RenderInputLayoutField field) => field.Format.ToString() is "R32G32B32_FLOAT" or "R32G32B32A32_FLOAT";
            return new Part
            {
                BufferIndex = bufferIndex,
                Data = buffer.Data,
                BindOffset = vertexRef.TryGetValue("m_nBindOffsetBytes", out var vb) ? (int)vb.ToInt64(CultureInfo.InvariantCulture) : 0,
                Stride = (int)buffer.ElementSizeInBytes,
                Min = min,
                Max = max,
                PositionOffset = position.SemanticName is null ? -1 : (int)position.Offset,
                PositionIsFloat = position.SemanticName is not null && Float3(position),
                NormalOffset = normal.SemanticName is not null && Float3(normal) ? (int)normal.Offset : -1,   // packed normals keep their direction
            };
        }

        private static int I(KVObject o, string key) => (int)o[key].ToInt64(CultureInfo.InvariantCulture);
    }
}

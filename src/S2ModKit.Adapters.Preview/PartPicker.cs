using System.Globalization;
using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Finds the part (draw call, in <see cref="PreviewSession.Parts"/> order) under a screen point: every draw call's triangles
/// are skinned on the CPU in the current pose and hit-tested against the camera ray; the nearest visible hit wins.
/// </summary>
internal sealed class PartPicker
{
    private readonly List<Triangles> parts = [];
    private readonly List<(int Parent, Triangles Piece)> pieces = [];

    public PartPicker(Model model, IReadOnlySet<int> activeMeshes)
    {
        foreach (var (mesh, meshIndex, _, _) in model.GetEmbeddedMeshesForLod(0))
        {
            if (!activeMeshes.Contains(meshIndex))
            {
                continue;
            }

            var remap = model.GetRemapTable(meshIndex) ?? [];
            foreach (var sceneObject in Items(mesh.Data, "m_sceneObjects"))
            {
                foreach (var draw in Items(sceneObject, "m_drawCalls"))
                {
                    parts.Add(Triangles.Read(mesh.VBIB, draw, remap));
                }
            }
        }

        for (var part = 0; part < parts.Count; part++)
        {
            pieces.AddRange(parts[part].Pieces().Select(piece => (part, piece)));
        }
    }

    /// <summary>The model's parts (draw calls, LOD 0).</summary>
    public int Count => parts.Count;

    /// <summary>
    /// Parts and pieces: a part made of several separate meshes (no shared vertex position, such as a belt drawn with a jacket) has
    /// its larger pieces after every part, at indices <see cref="Count"/> and up, in a fixed order (by part, then by size), so a saved
    /// piece index finds the same piece again in the same model.
    /// </summary>
    public int Total => parts.Count + pieces.Count;

    /// <summary>The part a piece belongs to (a part: itself).</summary>
    public int ParentOf(int index) => index < parts.Count ? index : pieces[index - parts.Count].Parent;

    /// <summary>The pieces (indices from <see cref="Count"/> up) of part <paramref name="part"/>; none when it is one mesh.</summary>
    public IEnumerable<int> PiecesOf(int part) => Enumerable.Range(0, pieces.Count).Where(piece => pieces[piece].Parent == part).Select(piece => parts.Count + piece);

    /// <summary>How many triangles part or piece <paramref name="index"/> has.</summary>
    public int TriangleCount(int index) => Get(index).TriangleCount;

    private Triangles Get(int index) => index < parts.Count ? parts[index] : pieces[index - parts.Count].Piece;

    /// <summary>
    /// The texels part <paramref name="index"/> covers in a <paramref name="width"/> x <paramref name="height"/> texture: its
    /// triangles drawn in UV space (wrapped into 0-1), grown by <paramref name="grow"/> texels so seams and mip levels keep the
    /// colour. 1 inside, 0 outside; rows top-down like the decoded texture. All zero when the part has no UVs.
    /// </summary>
    public float[] Mask(int index, int width, int height, int grow = 2) => Get(index).Mask(width, height, grow);

    /// <summary>The nearest part (or piece of it, when it has pieces) the ray hits, or null. <paramref name="hidden"/> parts are skipped.</summary>
    public int? Pick(Vector3 origin, Vector3 direction, Matrix4x4[] skin, Matrix4x4 transform, IReadOnlySet<int> hidden) =>
        PickWithUv(origin, direction, skin, transform, hidden)?.Part;

    /// <summary>Where the ray first hits a visible part (in the current pose) and the bone that moves that spot most, or null.</summary>
    public (Vector3 Point, int Bone)? PickPoint(Vector3 origin, Vector3 direction, Matrix4x4[] skin, Matrix4x4 transform, IReadOnlySet<int> hidden)
    {
        (Vector3, int)? hit = null;
        var best = float.MaxValue;
        for (var index = 0; index < parts.Count; index++)
        {
            if (!hidden.Contains(index) && parts[index].Nearest(origin, direction, skin, transform) is { } found && found.Distance < best)
            {
                best = found.Distance;
                hit = (origin + (direction * found.Distance), parts[index].MainBone(found.Vertex));
            }
        }

        return hit;
    }

    /// <summary>Sets part <paramref name="index"/>'s shape change (bind pose) so picking and the highlight follow the preview.</summary>
    public void SetShape(int index, Matrix4x4 shape)
    {
        parts[index].Shape = shape;
        foreach (var piece in PiecesOf(index))
        {
            Get(piece).Shape = shape;
        }
    }

    /// <summary>Part <paramref name="index"/>'s triangles in the current pose (three positions per triangle) for the highlight overlay.</summary>
    public Vector3[] Posed(int index, Matrix4x4[] skin, Matrix4x4 transform) => Get(index).Posed(skin, transform);

    /// <summary>The nearest visible part (or its piece) the ray hits and the texture coordinate at the hit (wrapped into 0-1), or null.</summary>
    public (int Part, Vector2 Uv)? PickWithUv(Vector3 origin, Vector3 direction, Matrix4x4[] skin, Matrix4x4 transform, IReadOnlySet<int> hidden)
    {
        var best = float.MaxValue;
        (int, Vector2)? hit = null;
        for (var index = 0; index < parts.Count; index++)
        {
            if (hidden.Contains(index) || parts[index].Nearest(origin, direction, skin, transform) is not { } found || found.Distance >= best)
            {
                continue;
            }

            best = found.Distance;
            hit = (index, new Vector2(found.Uv.X - MathF.Floor(found.Uv.X), found.Uv.Y - MathF.Floor(found.Uv.Y)));
        }

        if (hit is { } part)
        {
            foreach (var piece in PiecesOf(part.Item1))                     // the piece that holds the nearest triangle
            {
                if (Get(piece).Nearest(origin, direction, skin, transform) is { } found && found.Distance <= best * 1.0001f)
                {
                    return (piece, part.Item2);
                }
            }
        }

        return hit;
    }

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

    private sealed class Triangles
    {
        private Vector3[] positions = [];
        private Vector2[] uvs = [];
        private ushort[] bones = [];
        private float[] weights = [];                                     // perVertex per vertex (SkinWeights)
        private int perVertex;
        private int[] indices = [];

        public static Triangles Read(VBIB vbib, KVObject draw, int[] remap)
        {
            var buffer = vbib.VertexBuffers[(int)draw["m_vertexBuffers"][0]["m_hBuffer"].ToInt64(CultureInfo.InvariantCulture)];
            var baseVertex = (int)draw["m_nBaseVertex"].ToInt64(CultureInfo.InvariantCulture);
            VBIB.RenderInputLayoutField Field(string name) => buffer.InputLayoutFields.FirstOrDefault(f => f.SemanticName == name && f.SemanticIndex == 0);
            var position = Field("POSITION");
            if (position.SemanticName is null)
            {
                return new Triangles();
            }

            var all = VBIB.GetVector3AttributeArray(buffer, position);
            var blend = Field("BLENDINDICES");
            var weightField = Field("BLENDWEIGHT").SemanticName is null ? Field("BLENDWEIGHTS") : Field("BLENDWEIGHT");
            var allBones = blend.SemanticName is null ? new ushort[all.Length * 4] : VBIB.GetBlendIndicesArray(buffer, blend, remap);
            var indexRef = draw["m_indexBuffer"];
            var indexBuffer = vbib.IndexBuffers[(int)indexRef["m_hBuffer"].ToInt64(CultureInfo.InvariantCulture)];
            var start = (int)draw["m_nStartIndex"].ToInt64(CultureInfo.InvariantCulture);
            var count = (int)draw["m_nIndexCount"].ToInt64(CultureInfo.InvariantCulture);
            var bindOffset = indexRef.TryGetValue("m_nBindOffsetBytes", out var o) ? (int)o.ToInt64(CultureInfo.InvariantCulture) : 0;
            var size = (int)indexBuffer.ElementSizeInBytes;
            var indices = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = bindOffset + (start + i) * size;
                indices[i] = baseVertex + (size == 2 ? BitConverter.ToUInt16(indexBuffer.Data, at) : (int)BitConverter.ToUInt32(indexBuffer.Data, at));
            }

            var texcoord = Field("TEXCOORD");
            return new Triangles
            {
                positions = all,
                uvs = texcoord.SemanticName is null ? [] : VBIB.GetVector2AttributeArray(buffer, texcoord),
                bones = allBones,
                weights = SkinWeights.Read(buffer, weightField, all.Length, Math.Max(1, allBones.Length / Math.Max(1, all.Length))),
                perVertex = Math.Max(1, allBones.Length / Math.Max(1, all.Length)),
                indices = indices,
            };
        }

        public int TriangleCount => indices.Length / 3;

        /// <summary>
        /// The separate meshes of this part: triangles joined through vertex positions (welded to 0.01 units, so texture seams do not
        /// split a mesh), largest first. Only pieces with at least 4% of the triangles count, at most 12; none when fewer than two.
        /// </summary>
        public IReadOnlyList<Triangles> Pieces()
        {
            var triangles = indices.Length / 3;
            if (triangles < 16)
            {
                return [];
            }

            var weld = new Dictionary<(int, int, int), int>();
            var welded = new int[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                var p = positions[indices[i]];
                var key = ((int)MathF.Round(p.X * 100), (int)MathF.Round(p.Y * 100), (int)MathF.Round(p.Z * 100));
                if (!weld.TryGetValue(key, out var id))
                {
                    weld[key] = id = weld.Count;
                }

                welded[i] = id;
            }

            var root = Enumerable.Range(0, weld.Count).ToArray();
            int Find(int x)
            {
                while (root[x] != x)
                {
                    x = root[x] = root[root[x]];
                }

                return x;
            }

            for (var t = 0; t < triangles; t++)
            {
                var a = Find(welded[t * 3]);
                root[Find(welded[(t * 3) + 1])] = a;
                root[Find(welded[(t * 3) + 2])] = a;
            }

            var groups = Enumerable.Range(0, triangles).GroupBy(t => Find(welded[t * 3]))
                .Select(group => group.ToArray()).Where(group => group.Length >= triangles * 0.04)
                .OrderByDescending(group => group.Length).ThenBy(group => group[0]).Take(12).ToArray();
            if (groups.Length < 2)
            {
                return [];
            }

            return [.. groups.Select(group => new Triangles
            {
                positions = positions,
                uvs = uvs,
                bones = bones,
                weights = weights,
                perVertex = perVertex,
                indices = [.. group.SelectMany(t => new[] { indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2] })],
            })];
        }

        /// <summary>The bone with the most weight on vertex <paramref name="v"/> (an index into the skinning matrices).</summary>
        public int MainBone(int v)
        {
            var best = 0;
            for (var k = 1; k < perVertex; k++)
            {
                if (weights[(v * perVertex) + k] > weights[(v * perVertex) + best])
                {
                    best = k;
                }
            }

            return bones.Length == 0 ? 0 : bones[(v * perVertex) + best];
        }

        /// <summary>The part's shape change in bind pose (identity: as compiled); picking and the highlight follow it.</summary>
        public Matrix4x4 Shape { get; set; } = Matrix4x4.Identity;

        private Vector3 Shaped(int v) => Shape.IsIdentity ? positions[v] : Vector3.Transform(positions[v], Shape);

        public Vector3[] Posed(Matrix4x4[] skin, Matrix4x4 transform)
        {
            var posed = new Vector3[positions.Length];
            var done = new bool[positions.Length];
            var output = new Vector3[indices.Length];
            for (var i = 0; i < indices.Length; i++)
            {
                var v = indices[i];
                if (!done[v])
                {
                    posed[v] = Pose(v, skin, transform);
                    done[v] = true;
                }

                output[i] = posed[v];
            }

            return output;
        }

        private Vector3 Pose(int v, Matrix4x4[] skin, Matrix4x4 transform)
        {
            var p = Vector3.Zero;
            var total = 0f;
            for (var k = 0; k < perVertex; k++)
            {
                var w = weights[(v * perVertex) + k];
                var bone = bones[(v * perVertex) + k];
                if (w > 0)
                {
                    p += w * Vector3.Transform(Shaped(v), bone < skin.Length ? skin[bone] : Matrix4x4.Identity);
                    total += w;
                }
            }

            return Vector3.Transform(total > 0 ? p / total : Shaped(v), transform);
        }

        public float[] Mask(int width, int height, int grow)
        {
            var covered = new bool[width * height];
            if (uvs.Length == 0)
            {
                return new float[width * height];
            }

            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (Texel(uvs[indices[t]]), Texel(uvs[indices[t + 1]]), Texel(uvs[indices[t + 2]]));
                var (minX, maxX) = ((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                var (minY, maxY) = ((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                var area = Edge(a, b, c);
                if (MathF.Abs(area) < 1e-6f)
                {
                    continue;
                }

                for (var y = Math.Max(0, minY); y <= Math.Min(height - 1, maxY); y++)
                {
                    for (var x = Math.Max(0, minX); x <= Math.Min(width - 1, maxX); x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        var (w0, w1, w2) = (Edge(b, c, p) / area, Edge(c, a, p) / area, Edge(a, b, p) / area);
                        if (w0 >= -0.01f && w1 >= -0.01f && w2 >= -0.01f)
                        {
                            covered[(y * width) + x] = true;
                        }
                    }
                }
            }

            for (var step = 0; step < grow; step++)                         // grow by one texel per step (4-neighbourhood)
            {
                var next = (bool[])covered.Clone();
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var i = (y * width) + x;
                        if (!covered[i] && ((x > 0 && covered[i - 1]) || (x < width - 1 && covered[i + 1]) || (y > 0 && covered[i - width]) || (y < height - 1 && covered[i + width])))
                        {
                            next[i] = true;
                        }
                    }
                }

                covered = next;
            }

            return [.. covered.Select(inside => inside ? 1f : 0f)];

            Vector2 Texel(Vector2 uv) => new((uv.X - MathF.Floor(uv.X)) * width, (uv.Y - MathF.Floor(uv.Y)) * height);
            static float Edge(Vector2 a, Vector2 b, Vector2 p) => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
        }

        /// <summary>Distance along the ray to the nearest triangle of this part in the current pose, or null.</summary>
        public (float Distance, Vector2 Uv, int Vertex)? Nearest(Vector3 origin, Vector3 direction, Matrix4x4[] skin, Matrix4x4 transform)
        {
            var posed = new Dictionary<int, Vector3>();
            Vector3 Pose(int v)
            {
                if (posed.TryGetValue(v, out var known))
                {
                    return known;
                }

                var p = Vector3.Zero;
                var total = 0f;
                for (var k = 0; k < perVertex; k++)
                {
                    var w = weights[(v * perVertex) + k];
                    var bone = bones[v * perVertex + k];
                    if (w > 0)
                    {
                        p += w * Vector3.Transform(Shaped(v), bone < skin.Length ? skin[bone] : Matrix4x4.Identity);
                        total += w;
                    }
                }

                return posed[v] = Vector3.Transform(total > 0 ? p / total : Shaped(v), transform);
            }

            (float Distance, Vector2 Uv, int Vertex)? best = null;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                if (Hit(origin, direction, Pose(indices[t]), Pose(indices[t + 1]), Pose(indices[t + 2])) is { } h && (best is null || h.Distance < best.Value.Distance))
                {
                    var uv = uvs.Length == 0 ? Vector2.Zero
                        : ((1 - h.U - h.V) * uvs[indices[t]]) + (h.U * uvs[indices[t + 1]]) + (h.V * uvs[indices[t + 2]]);
                    var corner = 1 - h.U - h.V;                                // the hit's nearest corner
                    best = (h.Distance, uv, indices[t + (h.U > corner && h.U >= h.V ? 1 : h.V > corner ? 2 : 0)]);
                }
            }

            return best;
        }

        // Möller–Trumbore ray/triangle intersection, both faces.
        private static (float Distance, float U, float V)? Hit(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f)
            {
                return null;
            }

            var inv = 1f / det;
            var s = origin - a;
            var u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1)
            {
                return null;
            }

            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(direction, q) * inv;
            if (v < 0 || u + v > 1)
            {
                return null;
            }

            var distance = Vector3.Dot(e2, q) * inv;
            return distance > 0 ? (distance, u, v) : null;
        }
    }
}

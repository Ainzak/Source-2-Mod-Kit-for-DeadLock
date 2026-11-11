using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>One reshaped part: its index among the level-0 draw calls (the preview's part order) and its bind-pose change.</summary>
public sealed record PartReshape(int Part, Matrix4x4 Matrix);

/// <summary>
/// Reshapes parts of a compiled model directly (<c>shape_part@1</c>): the vertices a part's triangles use are transformed in bind
/// pose (positions, normals and tangents, plain floats or packed in one 32-bit value) in its own vertex buffer, in every level of detail
/// of the same mesh (meshes named <c>name</c>, <c>name_lod1</c>, ... with a draw call of the part's material). The vertex blocks are
/// re-encoded in their own format (meshoptimizer when they were) and checked by decoding them again. The bounds the game reads for
/// those meshes are recomputed from the vertices, as the compiler stores them (checked first against the file's own values): scene
/// bounds in model space, and per bone the box (centre and half size) and sphere radius of the weighted vertices in that bone's space.
/// Skinning (bones, weights) is unchanged, so the part animates as before. Everything else stays byte for byte.
/// </summary>
public static partial class CompiledPartReshape
{
    /// <summary>For each level-0 part that cannot be reshaped this way, why not in plain words (absent: it can).</summary>
    public static IReadOnlyDictionary<int, string> Refusals(byte[] compiled, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        using var resource = Open(compiled, logicalPath);
        var layout = Layout.Read(resource);
        var refusals = new Dictionary<int, string>();
        for (var part = 0; part < layout.Parts.Count; part++)
        {
            try
            {
                foreach (var target in layout.Targets(part))
                {
                    _ = target.Check();
                }
            }
            catch (S2ModKitException exception)
            {
                refusals[part] = exception.Error.Summary;
            }
        }

        return refusals;
    }

    /// <summary>The model with <paramref name="parts"/> reshaped. <paramref name="codecPath"/> is the native meshoptimizer the
    /// compressed vertex blocks are re-encoded with.</summary>
    public static byte[] Apply(byte[] compiled, string logicalPath, IReadOnlyList<PartReshape> parts, string? codecPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return compiled;
        }

        using var resource = Open(compiled, logicalPath);
        var layout = Layout.Read(resource);
        var buffers = new Dictionary<(int Mesh, int Buffer), (Target Target, byte[] Data, List<(int Min, int Max)> Ranges)>();
        foreach (var part in parts)
        {
            if (part.Part < 0 || part.Part >= layout.Parts.Count)
            {
                throw Errors.Input("SHAPE_PART_RANGE", $"The model has {layout.Parts.Count} parts; part {part.Part + 1} is not one of them.", "Open the preview again.");
            }

            foreach (var target in layout.Targets(part.Part))
            {
                var buffer = target.Check();
                var key = (target.Mesh.Index, target.Buffer);
                if (!buffers.TryGetValue(key, out var entry))
                {
                    entry = (target, (byte[])buffer.Data.Clone(), []);
                    buffers[key] = entry;
                }

                if (entry.Ranges.Any(range => range.Min <= target.Max && target.Min <= range.Max))
                {
                    throw Errors.Input("SHAPE_PART_TWICE", $"Part {part.Part + 1} is reshaped twice.", "Reshape each part once.");
                }

                Transform(entry.Data, buffer, target.Min, target.Max, part.Matrix);
                entry.Ranges.Add((target.Min, target.Max));
            }
        }

        var payloads = new Dictionary<int, byte[]>();
        NativeMeshOptimizerCodec? codec = null;
        try
        {
            foreach (var ((_, _), (target, data, _)) in buffers)
            {
                payloads[target.Block] = target.Compressed
                    ? Encode(codec ??= OpenCodec(codecPath), data, target, resource)
                    : data;
            }

            foreach (var mesh in buffers.Keys.Select(key => key.Mesh).Distinct())
            {
                var info = layout.Meshes.First(item => item.Index == mesh);
                var edited = buffers.Where(pair => pair.Key.Mesh == mesh).ToDictionary(pair => pair.Key.Buffer, pair => pair.Value.Data);
                UpdateBounds(info, edited);
                payloads[resource.Blocks.IndexOf(info.Block)] = Serialize(info.Block);
            }
        }
        finally
        {
            codec?.Dispose();
        }

        var result = CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
        Verify(result, logicalPath, buffers.ToDictionary(pair => pair.Key, pair => pair.Value.Data));
        return result;
    }

    private static Resource Open(byte[] compiled, string logicalPath)
    {
        var resource = new Resource { FileName = logicalPath };
        try
        {
            resource.Read(new MemoryStream(compiled));
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or EndOfStreamException or ArgumentException)
        {
            resource.Dispose();
            throw Errors.Unsupported("SHAPE_MODEL_UNREADABLE", "This model's file cannot be read for reshaping.", "Report the model.");
        }

        if (resource.DataBlock is not Model)
        {
            resource.Dispose();
            throw Errors.Input("SHAPE_NOT_A_MODEL", $"'{logicalPath}' is not a model.", "Pass a compiled .vmdl_c.");
        }

        return resource;
    }

    /// <summary>Positions, normals and tangents of vertices <paramref name="min"/>..<paramref name="max"/> through <paramref name="matrix"/>
    /// (normals by its inverse transpose, so a turned or stretched part is lit as it now faces).</summary>
    private static void Transform(byte[] data, VBIB.OnDiskBufferData buffer, int min, int max, Matrix4x4 matrix)
    {
        var stride = (int)buffer.ElementSizeInBytes;
        var position = Field(buffer, "POSITION");
        var normal = Field(buffer, "NORMAL");
        var tangent = Field(buffer, "TANGENT");
        var floatNormal = normal is { } n && n.Format.ToString() is "R32G32B32_FLOAT" or "R32G32B32A32_FLOAT";
        var packedNormal = normal is { } p && p.Format.ToString() == "R32_UINT";
        var floatTangent = tangent is { } t && t.Format.ToString() is "R32G32B32_FLOAT" or "R32G32B32A32_FLOAT";
        var normals = Matrix4x4.Invert(matrix, out var inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
        for (var v = min; v <= max; v++)
        {
            var at = v * stride;
            Write(data, at + (int)position!.Value.Offset, Vector3.Transform(Read(data, at + (int)position.Value.Offset), matrix));
            if (floatNormal)
            {
                var turned = Vector3.TransformNormal(Read(data, at + (int)normal!.Value.Offset), normals);
                Write(data, at + (int)normal.Value.Offset, turned.LengthSquared() > 0 ? Vector3.Normalize(turned) : turned);
            }
            else if (packedNormal)
            {
                var offset = at + (int)normal!.Value.Offset;
                var packed = BitConverter.ToUInt32(data, offset);
                BitConverter.TryWriteBytes(data.AsSpan(offset, 4), S2ModKit.Application.PackedNormalTangent.Turn(packed, normals, matrix));
            }

            if (floatTangent)
            {
                var turned = Vector3.TransformNormal(Read(data, at + (int)tangent!.Value.Offset), matrix);   // its fourth value (the sign) stays
                Write(data, at + (int)tangent.Value.Offset, turned.LengthSquared() > 0 ? Vector3.Normalize(turned) : turned);
            }
        }
    }

    private static byte[] Encode(NativeMeshOptimizerCodec codec, byte[] decoded, Target target, Resource resource)
    {
        var original = resource.Blocks[target.Block];
        var stride = (int)target.Mesh.Mesh.VBIB.VertexBuffers[target.Buffer].ElementSizeInBytes;
        var count = (int)target.Mesh.Mesh.VBIB.VertexBuffers[target.Buffer].ElementCount;
        var encoded = codec.EncodeVertexBuffer(decoded, count, stride, version: 1);
        if (!codec.DecodeVertexBuffer(encoded, count, stride).AsSpan().SequenceEqual(decoded))
        {
            throw Errors.Verification("SHAPE_ENCODE_MISMATCH", $"Vertex block {target.Block} did not decode back to the reshaped vertices.", "Report the model.");
        }

        _ = original;
        return encoded;
    }

    private static NativeMeshOptimizerCodec OpenCodec(string? path) =>
        path is not null && File.Exists(path)
            ? NativeMeshOptimizerCodec.Open(path)
            : throw Errors.Unsupported("SHAPE_CODEC_MISSING", "The mesh codec (meshoptimizer.dll) is missing beside the app.", "Reinstall the app.");

    /// <summary>Scene bounds and bone boxes of a mesh from its vertices (edited buffers replaced), as the compiler stores them.</summary>
    private static void UpdateBounds(MeshInfo info, IReadOnlyDictionary<int, byte[]> edited)
    {
        var vbib = info.Mesh.VBIB;
        var before = Bounds(info, vbib, new Dictionary<int, byte[]>());
        var after = Bounds(info, vbib, edited);
        var sceneObjects = Items(info.Mesh.Data, "m_sceneObjects").ToArray();
        for (var s = 0; s < sceneObjects.Length; s++)
        {
            var (storedMin, storedMax) = (Vector(sceneObjects[s], "m_vMinBounds"), Vector(sceneObjects[s], "m_vMaxBounds"));
            var exact = Near(storedMin, before.Scenes[s].Min) && Near(storedMax, before.Scenes[s].Max);
            var (min, max) = exact ? after.Scenes[s] : (Vector3.Min(storedMin, after.Scenes[s].Min), Vector3.Max(storedMax, after.Scenes[s].Max));   // unknown rule: only grow
            SetVector(sceneObjects[s], "m_vMinBounds", storedMin, min);
            SetVector(sceneObjects[s], "m_vMaxBounds", storedMax, max);
        }

        if (!info.Mesh.Data.TryGetValue("m_skeleton", out var skeleton) || skeleton is null || !skeleton.TryGetValue("m_bones", out var bones) || bones is null || !bones.IsArray)
        {
            return;
        }

        for (var b = 0; b < bones.Count && b < before.Bones.Length; b++)
        {
            if (after.Bones[b].Count == 0 || before.Bones[b] == after.Bones[b])
            {
                continue;
            }

            var bone = bones[b];
            var box = bone["m_bbox"];
            var (centre, size, radius) = (Vector(box, "m_vecCenter"), Vector(box, "m_vecSize"), Single(bone, "m_flSphereRadius"));
            var exact = before.Bones[b].Count > 0 && Near(centre, before.Bones[b].Centre) && Near(size, before.Bones[b].Half) && MathF.Abs(radius - before.Bones[b].Radius) < 0.01f;
            Vector3 newCentre, newSize;
            float newRadius;
            if (exact)
            {
                (newCentre, newSize, newRadius) = (after.Bones[b].Centre, after.Bones[b].Half, after.Bones[b].Radius);
            }
            else
            {
                var lo = Vector3.Min(centre - size, after.Bones[b].Centre - after.Bones[b].Half);    // unknown rule: only grow
                var hi = Vector3.Max(centre + size, after.Bones[b].Centre + after.Bones[b].Half);
                (newCentre, newSize, newRadius) = ((lo + hi) / 2, (hi - lo) / 2, MathF.Max(radius, after.Bones[b].Radius));
            }

            SetVector(box, "m_vecCenter", centre, newCentre);
            SetVector(box, "m_vecSize", size, newSize);
            KvNumericMutation.ReplaceSingle(bone, "m_flSphereRadius", radius, newRadius, $"bone {b}");
        }
    }

    private static (List<(Vector3 Min, Vector3 Max)> Scenes, BoneBounds[] Bones) Bounds(MeshInfo info, VBIB vbib, IReadOnlyDictionary<int, byte[]> edited)
    {
        var bones = Items(info.Mesh.Data.TryGetValue("m_skeleton", out var skeleton) && skeleton is not null ? skeleton : new KVObject(), "m_bones").ToArray();
        var inverse = bones.Select(bone => InverseBind(bone)).ToArray();
        var boneBox = bones.Select(_ => (Lo: new Vector3(float.MaxValue), Hi: new Vector3(float.MinValue), Radius: 0f, Count: 0)).ToArray();
        var positions = new Dictionary<int, Vector3[]>();
        Vector3[] PositionsOf(int buffer)
        {
            if (!positions.TryGetValue(buffer, out var list))
            {
                var vb = vbib.VertexBuffers[buffer];
                var data = edited.TryGetValue(buffer, out var changed) ? changed : vb.Data;
                var offset = (int)Field(vb, "POSITION")!.Value.Offset;
                list = [.. Enumerable.Range(0, (int)vb.ElementCount).Select(v => Read(data, (v * (int)vb.ElementSizeInBytes) + offset))];
                positions[buffer] = list;
            }

            return list;
        }

        for (var buffer = 0; buffer < vbib.VertexBuffers.Count; buffer++)
        {
            var vb = vbib.VertexBuffers[buffer];
            if (Field(vb, "POSITION") is null || Field(vb, "BLENDINDICES") is not { } blend || bones.Length == 0)
            {
                continue;
            }

            var points = PositionsOf(buffer);
            var indices = VBIB.GetBlendIndicesArray(vb, blend, Identity);
            var weightField = Field(vb, "BLENDWEIGHT") ?? Field(vb, "BLENDWEIGHTS");
            var perVertex = points.Length == 0 ? 0 : indices.Length / points.Length;
            var weights = Weights(vb, weightField, points.Length, perVertex);
            for (var v = 0; v < points.Length; v++)
            {
                for (var k = 0; k < perVertex; k++)
                {
                    var weight = weights[(v * perVertex) + k];
                    var bone = indices[(v * perVertex) + k];
                    if (weight <= 0 || bone >= bones.Length)
                    {
                        continue;
                    }

                    var local = Vector3.Transform(points[v], inverse[bone]);
                    var current = boneBox[bone];
                    boneBox[bone] = (Vector3.Min(current.Lo, local), Vector3.Max(current.Hi, local), MathF.Max(current.Radius, local.Length()), current.Count + 1);
                }
            }
        }

        var scenes = new List<(Vector3, Vector3)>();
        foreach (var sceneObject in Items(info.Mesh.Data, "m_sceneObjects"))
        {
            var (lo, hi) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (var draw in Items(sceneObject, "m_drawCalls"))
            {
                var buffer = Int(draw["m_vertexBuffers"][0], "m_hBuffer");
                foreach (var point in PositionsOf(buffer))
                {
                    (lo, hi) = (Vector3.Min(lo, point), Vector3.Max(hi, point));
                }
            }

            scenes.Add((lo, hi));
        }

        return (scenes, [.. boneBox.Select(box => box.Count == 0 ? new BoneBounds(Vector3.Zero, Vector3.Zero, 0, 0) : new BoneBounds((box.Lo + box.Hi) / 2, (box.Hi - box.Lo) / 2, box.Radius, box.Count))]);
    }

    private static void Verify(byte[] result, string logicalPath, IReadOnlyDictionary<(int Mesh, int Buffer), byte[]> intended)
    {
        using var reopened = Open(result, logicalPath);
        var layout = Layout.Read(reopened);
        foreach (var ((mesh, buffer), data) in intended)
        {
            if (!layout.Meshes.First(item => item.Index == mesh).Mesh.VBIB.VertexBuffers[buffer].Data.AsSpan().SequenceEqual(data))
            {
                throw Errors.Verification("SHAPE_REOPEN_MISMATCH", $"The rebuilt model's vertex buffer {buffer} of mesh {mesh} differs from the reshaped vertices.", "Report the model.");
            }
        }
    }

    private static readonly int[] Identity = [.. Enumerable.Range(0, 65536)];

    /// <summary>Blend weights aligned with the blend indices, <paramref name="perVertex"/> per vertex: VRF returns two weight vectors
    /// per vertex for eight influences (Abrams, Apollo), one for four; without weights the first influence has all of it.</summary>
    private static float[] Weights(VBIB.OnDiskBufferData buffer, VBIB.RenderInputLayoutField? field, int vertexCount, int perVertex)
    {
        var flat = new float[vertexCount * perVertex];
        if (field is not { } weightField)
        {
            for (var v = 0; v < vertexCount && perVertex > 0; v++)
            {
                flat[v * perVertex] = 1;
            }

            return flat;
        }

        var vectors = VBIB.GetBlendWeightsArray(buffer, weightField);
        var groups = Math.Max(1, vectors.Length / Math.Max(1, vertexCount));
        for (var v = 0; v < vertexCount; v++)
        {
            for (var k = 0; k < perVertex && k / 4 < groups; k++)
            {
                var vector = vectors[(v * groups) + (k / 4)];
                flat[(v * perVertex) + k] = (k % 4) switch { 0 => vector.X, 1 => vector.Y, 2 => vector.Z, _ => vector.W };
            }
        }

        return flat;
    }

    private static Matrix4x4 InverseBind(KVObject bone)
    {
        var a = bone["m_invBindPose"];
        var f = Enumerable.Range(0, 12).Select(i => a[i].ToSingle(CultureInfo.InvariantCulture)).ToArray();
        return new Matrix4x4(f[0], f[4], f[8], 0, f[1], f[5], f[9], 0, f[2], f[6], f[10], 0, f[3], f[7], f[11], 1);
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.01f;

    private static Vector3 Vector(KVObject parent, string key)
    {
        var value = parent[key];
        return new Vector3(value[0].ToSingle(CultureInfo.InvariantCulture), value[1].ToSingle(CultureInfo.InvariantCulture), value[2].ToSingle(CultureInfo.InvariantCulture));
    }

    private static float Single(KVObject parent, string key) => parent[key].ToSingle(CultureInfo.InvariantCulture);

    private static void SetVector(KVObject parent, string key, Vector3 before, Vector3 after) =>
        KvNumericMutation.ReplaceVector3(parent, key, new TransformVector3 { X = before.X, Y = before.Y, Z = before.Z }, new TransformVector3 { X = after.X, Y = after.Y, Z = after.Z }, key);

    private static VBIB.RenderInputLayoutField? Field(VBIB.OnDiskBufferData buffer, string semantic)
    {
        var field = buffer.InputLayoutFields.FirstOrDefault(f => f.SemanticName == semantic && f.SemanticIndex == 0);
        return field.SemanticName is null ? null : field;
    }

    private static Vector3 Read(byte[] data, int at) => new(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8));

    private static void Write(byte[] data, int at, Vector3 value)
    {
        BitConverter.TryWriteBytes(data.AsSpan(at), value.X);
        BitConverter.TryWriteBytes(data.AsSpan(at + 4), value.Y);
        BitConverter.TryWriteBytes(data.AsSpan(at + 8), value.Z);
    }

    private static byte[] Serialize(Block block)
    {
        using var stream = new MemoryStream();
        block.Serialize(stream);
        return stream.ToArray();
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

    private static int Int(KVObject parent, string key) => (int)parent[key].ToInt64(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"_lod\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LodSuffix();

    private sealed record BoneBounds(Vector3 Centre, Vector3 Half, float Radius, int Count);

    private sealed record MeshInfo(int Index, string Name, string Family, Mesh Mesh, KVObject Control, Block Block)
    {
        /// <summary>Drawn at level of detail 0 (a part's own mesh); other meshes of the family are its lower levels.</summary>
        public bool Lod0 { get; set; }
    }

    private sealed record PartInfo(MeshInfo Mesh, string Material, int Occurrence, int Occurrences);

    /// <summary>A part's draw call in one mesh: its vertex buffer, that buffer's block and the vertex range its triangles use.</summary>
    private sealed record Target(MeshInfo Mesh, int Buffer, int Block, bool Compressed, int Min, int Max, string? Problem)
    {
        public VBIB.OnDiskBufferData Check() => Problem is null
            ? Mesh.Mesh.VBIB.VertexBuffers[Buffer]
            : throw Errors.Unsupported("SHAPE_PART_UNSUPPORTED", Problem, "Pick another part.");
    }

    private sealed class Layout
    {
        public List<MeshInfo> Meshes { get; } = [];

        public List<PartInfo> Parts { get; } = [];

        public static Layout Read(Resource resource)
        {
            try
            {
                return ReadCore(resource);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidCastException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or FormatException)
            {
                throw Errors.Unsupported("SHAPE_MODEL_LAYOUT", "This model's meshes are stored in a way the app cannot rewrite yet.", "Pick another model.");
            }
        }

        private static Layout ReadCore(Resource resource)
        {
            var model = (Model)resource.DataBlock!;
            var control = resource.Blocks.OfType<BinaryKV3>().FirstOrDefault(block => block.Type.ToString() == "CTRL" && block.Data.Root.ContainsKey("embedded_meshes"))
                ?? throw Errors.Unsupported("SHAPE_NO_EMBEDDED_MESHES", "This model keeps its meshes in other files.", "Only models with their own meshes can be reshaped.");
            var embedded = control.Data.Root["embedded_meshes"];
            var layout = new Layout();
            foreach (var (mesh, index, name) in model.GetEmbeddedMeshes())
            {
                if (index >= embedded.Count && !Enumerable.Range(0, embedded.Count).Any(i => embedded[i].ContainsKey("m_nMeshIndex")))
                {
                    throw Errors.Unsupported("SHAPE_MESH_TABLE", "This model's mesh table does not list all its meshes.", "Report the model.");
                }

                var entry = Enumerable.Range(0, embedded.Count).Select(i => embedded[i]).FirstOrDefault(item => item.ContainsKey("m_nMeshIndex") && Int(item, "m_nMeshIndex") == index)
                    ?? (index < embedded.Count ? embedded[index] : throw Errors.Unsupported("SHAPE_MESH_TABLE", "This model's mesh table does not list all its meshes.", "Report the model."));   // older files: in table order
                if (!entry.ContainsKey("m_nDataBlock"))
                {
                    throw Errors.Unsupported("SHAPE_MESH_TABLE", "This model stores its meshes in an older layout the app cannot rewrite yet.", "Pick another model.");
                }

                layout.Meshes.Add(new MeshInfo(index, name, LodSuffix().Replace(name, string.Empty), mesh, entry, resource.Blocks[Int(entry, "m_nDataBlock")]));
            }

            foreach (var (mesh, index, _, _) in model.GetEmbeddedMeshesForLod(0))
            {
                var info = layout.Meshes.First(item => item.Index == index);
                info.Lod0 = true;
                var materials = Items(mesh.Data, "m_sceneObjects").SelectMany(sceneObject => Items(sceneObject, "m_drawCalls")).Select(draw => draw["m_material"].ToString(CultureInfo.InvariantCulture)).ToArray();
                for (var d = 0; d < materials.Length; d++)
                {
                    layout.Parts.Add(new PartInfo(info, materials[d], materials.Take(d).Count(m => m == materials[d]), materials.Count(m => m == materials[d])));
                }
            }

            return layout;
        }

        /// <summary>The part's draw call in its mesh and in that mesh's other levels of detail (same material, same occurrence).</summary>
        public IEnumerable<Target> Targets(int part)
        {
            var info = Parts[part];
            foreach (var mesh in Meshes.Where(mesh => ReferenceEquals(mesh, info.Mesh) || (!mesh.Lod0 && string.Equals(mesh.Family, info.Mesh.Family, StringComparison.OrdinalIgnoreCase))))
            {
                var draws = Items(mesh.Mesh.Data, "m_sceneObjects").SelectMany(sceneObject => Items(sceneObject, "m_drawCalls")).ToArray();
                var same = draws.Where(draw => draw["m_material"].ToString(CultureInfo.InvariantCulture) == info.Material).ToArray();
                if (same.Length == 0)
                {
                    continue;                                               // this level of detail leaves the part out
                }

                if (same.Length != info.Occurrences)
                {
                    yield return new Target(mesh, 0, 0, false, 0, -1, "This part's material is drawn a different number of times in a lower level of detail, so the matching pieces are unclear.");
                    yield break;
                }

                yield return TargetOf(mesh, draws, same[info.Occurrence]);
            }
        }

        private static Target TargetOf(MeshInfo mesh, KVObject[] draws, KVObject draw)
        {
            var buffer = Int(draw["m_vertexBuffers"][0], "m_hBuffer");
            var (min, max) = Range(mesh.Mesh.VBIB, draw);
            var descriptors = mesh.Control.TryGetValue("m_vertexBuffers", out var list) && list is not null && list.IsArray ? list : null;
            var vb = mesh.Mesh.VBIB.VertexBuffers[buffer];
            var position = Field(vb, "POSITION");
            string? problem = descriptors is null || buffer >= descriptors.Count ? "This model keeps its vertices in an older layout the app cannot rewrite yet."
                : position is null || position.Value.Format.ToString() is not ("R32G32B32_FLOAT" or "R32G32B32A32_FLOAT") ? "This part's vertices are stored in a packed format the app cannot change yet."
                : descriptors[buffer].TryGetValue("m_bCompressedZSTD", out var zstd) && zstd is not null && zstd.ToBoolean(CultureInfo.InvariantCulture) ? "This part's vertices are stored with a compression the app cannot write yet."
                : max < min ? "This part has no vertices."
                : draws.Any(other => !ReferenceEquals(other, draw) && Int(other["m_vertexBuffers"][0], "m_hBuffer") == buffer && Range(mesh.Mesh.VBIB, other) is var o && o.Min <= max && min <= o.Max)
                    ? "This part shares its vertices with another part."
                : null;
            var block = problem is null ? Int(descriptors![buffer], "m_nBlockIndex") : 0;
            var compressed = problem is null && descriptors![buffer].TryGetValue("m_bMeshoptCompressed", out var meshopt) && meshopt is not null && meshopt.ToBoolean(CultureInfo.InvariantCulture);
            return new Target(mesh, buffer, block, compressed, min, max, problem);
        }

        private static (int Min, int Max) Range(VBIB vbib, KVObject draw)
        {
            var indexRef = draw["m_indexBuffer"];
            var indexBuffer = vbib.IndexBuffers[Int(indexRef, "m_hBuffer")];
            var bind = indexRef.TryGetValue("m_nBindOffsetBytes", out var offset) ? (int)offset.ToInt64(CultureInfo.InvariantCulture) : 0;
            var (start, count, baseVertex, size) = (Int(draw, "m_nStartIndex"), Int(draw, "m_nIndexCount"), Int(draw, "m_nBaseVertex"), (int)indexBuffer.ElementSizeInBytes);
            var (min, max) = (int.MaxValue, -1);
            for (var i = 0; i < count; i++)
            {
                var at = bind + ((start + i) * size);
                var v = baseVertex + (size == 2 ? BitConverter.ToUInt16(indexBuffer.Data, at) : (int)BitConverter.ToUInt32(indexBuffer.Data, at));
                (min, max) = (Math.Min(min, v), Math.Max(max, v));
            }

            return (min, max);
        }
    }
}

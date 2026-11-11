using System.Globalization;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>A part (level-0 draw call, in the preview's order) or one piece of it (its triangles' numbers in that draw call) to draw
/// with <paramref name="Material"/> (<c>.vmat</c> path without <c>_c</c>) instead of its own.</summary>
public sealed record SeeThroughSplit(int Part, IReadOnlyList<int>? Triangles, string Material);

/// <summary>
/// Gives single parts or pieces their own material (<c>split_draw_material@1</c>), so one of them can be drawn see-through while
/// everything else keeps its material: a whole part only changes its draw call's material; for a piece, the draw call's triangles
/// are put in a new order (the rest first, the piece after, each in its old order) inside the draw call's own range of its index
/// block, and a copy of the draw call draws the piece's range with the new material. Vertices, bounds, skinning and every other
/// draw call stay as they are. Level of detail 0 only: lower levels keep the part solid. The index block is encoded again in its own
/// format (meshoptimizer when it was) and checked by decoding it again; the new materials join the model's list of the files it uses.
/// </summary>
public static class CompiledSeeThroughSplit
{
    public static byte[] Apply(byte[] compiled, string logicalPath, IReadOnlyList<SeeThroughSplit> splits, string? codecPath)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(splits);
        if (splits.Count == 0)
        {
            return compiled;
        }

        if (splits.GroupBy(split => (split.Part, Whole: split.Triangles is null)).Any(group => group.Key.Whole && group.Count() > 1))
        {
            throw Errors.Input("SEE_THROUGH_PART_TWICE", "A part is given two materials of its own.", "Change the part once.");
        }

        using var resource = CompiledAddedEffects.Open(compiled, logicalPath, out var model);
        var control = resource.Blocks.OfType<BinaryKV3>().FirstOrDefault(block => block.Type == BlockType.CTRL && block.Data.Root.ContainsKey("embedded_meshes"))
            ?? throw Errors.Unsupported("SEE_THROUGH_LAYOUT", "This model keeps its meshes in other files.", "Only models with their own meshes can be split.");
        var embedded = control.Data.Root["embedded_meshes"];
        var draws = new List<(Mesh Mesh, int MeshIndex, KVObject SceneObject, KVObject Draw)>();
        foreach (var (mesh, meshIndex, _, _) in model.GetEmbeddedMeshesForLod(0))
        {
            foreach (var sceneObject in mesh.Data["m_sceneObjects"].Values)
            {
                draws.AddRange(sceneObject["m_drawCalls"].Values.Select(draw => (mesh, meshIndex, sceneObject, draw)));
            }
        }

        var indexBlocks = new Dictionary<(int Mesh, int Buffer), (byte[] Data, int Size, KVObject Descriptor, int Vertices)>();
        var changedMeshes = new HashSet<Mesh>();
        var copies = new List<(KVObject SceneObject, KVObject After, KVObject Copy)>();
        foreach (var byPart in splits.GroupBy(split => split.Part))
        {
            if (byPart.Key < 0 || byPart.Key >= draws.Count)
            {
                throw Errors.Input("SEE_THROUGH_PART_RANGE", $"The model has {draws.Count} parts; part {byPart.Key + 1} is not one of them.", "Open the preview again.");
            }

            var (mesh, meshIndex, sceneObject, draw) = draws[byPart.Key];
            changedMeshes.Add(mesh);
            var pieces = byPart.Where(split => split.Triangles is not null).ToArray();
            if (pieces.Length > 0)
            {
                var buffer = Int(draw["m_indexBuffer"], "m_hBuffer");
                if (!indexBlocks.TryGetValue((meshIndex, buffer), out var block))
                {
                    var source = mesh.VBIB.IndexBuffers[buffer];
                    var entry = Enumerable.Range(0, embedded.Count).Select(i => embedded[i]).FirstOrDefault(item => item.ContainsKey("m_nMeshIndex") && Int(item, "m_nMeshIndex") == meshIndex)
                        ?? throw Errors.Unsupported("SEE_THROUGH_LAYOUT", "This model's mesh table does not list its meshes.", "Report the model.");
                    var descriptor = entry.TryGetValue("m_indexBuffers", out var list) && list.IsArray && buffer < list.Count ? list[buffer]
                        : throw Errors.Unsupported("SEE_THROUGH_LAYOUT", "This model keeps its triangles in an older layout.", "Pick another part.");
                    if (descriptor.TryGetValue("m_bCompressedZSTD", out var zstd) && zstd.ToBoolean(CultureInfo.InvariantCulture))
                    {
                        throw Errors.Unsupported("SEE_THROUGH_LAYOUT", "This part's triangles are stored with a compression the app cannot write yet.", "Change the whole part instead.");
                    }

                    block = ((byte[])source.Data.Clone(), (int)source.ElementSizeInBytes, descriptor, mesh.VBIB.VertexBuffers.Max(vertices => (int)vertices.ElementCount));
                    indexBlocks[(meshIndex, buffer)] = block;
                }

                var (start, count) = (Int(draw, "m_nStartIndex"), Int(draw, "m_nIndexCount"));
                var triangles = count / 3;
                var sets = pieces.Select(split => split.Triangles!.ToHashSet()).ToArray();
                var all = sets.SelectMany(set => set).ToArray();
                if (sets.Any(set => set.Count == 0) || all.Any(t => t < 0 || t >= triangles) || all.Distinct().Count() != all.Length || all.Length >= triangles)
                {
                    throw Errors.Input("SEE_THROUGH_PIECE", $"The pieces do not fit part {byPart.Key + 1} ({triangles} triangles).", "Open the preview again and pick the pieces.");
                }

                // the rest of the part first, then each piece, each in its old order
                var taken = all.ToHashSet();
                var order = Enumerable.Range(0, triangles).Where(t => !taken.Contains(t)).Concat(sets.SelectMany(set => Enumerable.Range(0, triangles).Where(set.Contains)));
                var size = block.Size;
                var range = block.Data.AsSpan(start * size, count * size).ToArray();
                var at = start * size;
                foreach (var t in order)
                {
                    range.AsSpan(t * 3 * size, 3 * size).CopyTo(block.Data.AsSpan(at));
                    at += 3 * size;
                }

                var next = start + ((triangles - taken.Count) * 3);
                draw["m_nIndexCount"] = CompiledAddedEffects.Integer(draw["m_nIndexCount"], (triangles - taken.Count) * 3);
                for (var i = 0; i < pieces.Length; i++)
                {
                    var copy = CompiledAddedEffects.Clone(draw);
                    copy["m_nStartIndex"] = CompiledAddedEffects.Integer(copy["m_nStartIndex"], next);
                    copy["m_nIndexCount"] = CompiledAddedEffects.Integer(copy["m_nIndexCount"], sets[i].Count * 3);
                    copy["m_material"] = CompiledAddedEffects.Text(copy["m_material"], pieces[i].Material);
                    copies.Add((sceneObject, draw, copy));
                    next += sets[i].Count * 3;
                }
            }

            if (byPart.FirstOrDefault(split => split.Triangles is null) is { } whole)
            {
                draw["m_material"] = CompiledAddedEffects.Text(draw["m_material"], whole.Material);   // the rest of the part
            }
        }

        foreach (var group in copies.GroupBy(item => item.SceneObject))
        {
            var original = group.Key["m_drawCalls"];
            var list = CompiledAddedEffects.Each(original, (item, _) => item);
            var rebuilt = KVObject.Array();
            rebuilt.Flag = original.Flag;
            foreach (var item in list.Values)
            {
                rebuilt.Add(item);
                foreach (var (_, after, copy) in group.Where(entry => ReferenceEquals(entry.After, item)))
                {
                    rebuilt.Add(copy);                                       // the piece draws right after the rest of its part
                }
            }

            group.Key["m_drawCalls"] = rebuilt;
        }

        var payloads = new Dictionary<int, byte[]>();
        NativeMeshOptimizerCodec? codec = null;
        try
        {
            foreach (var ((_, _), (data, size, descriptor, vertices)) in indexBlocks)
            {
                var blockIndex = Int(descriptor, "m_nBlockIndex");
                var compressed = descriptor.TryGetValue("m_bMeshoptCompressed", out var meshopt) && meshopt.ToBoolean(CultureInfo.InvariantCulture);
                if (!compressed)
                {
                    payloads[blockIndex] = data;
                    continue;
                }

                codec ??= codecPath is not null && File.Exists(codecPath) ? NativeMeshOptimizerCodec.Open(codecPath)
                    : throw Errors.Unsupported("SHAPE_CODEC_MISSING", "The mesh codec (meshoptimizer.dll) is missing beside the app.", "Reinstall the app.");
                var indices = new uint[data.Length / size];
                for (var i = 0; i < indices.Length; i++)
                {
                    indices[i] = size == 2 ? BitConverter.ToUInt16(data, i * 2) : BitConverter.ToUInt32(data, i * 4);
                }

                var encoded = codec.EncodeIndexBuffer(indices, vertices);
                if (!SameTriangles(codec.DecodeIndexBuffer(encoded, indices.Length, size), indices, size))
                {
                    throw Errors.Verification("SEE_THROUGH_ENCODE", $"Index block {blockIndex} did not decode back to the new triangle order.", "Report the model.");
                }

                payloads[blockIndex] = encoded;
            }
        }
        finally
        {
            codec?.Dispose();
        }

        foreach (var mesh in changedMeshes)
        {
            payloads[resource.Blocks.IndexOf(mesh)] = CompiledAddedEffects.Serialize(mesh);
        }

        var known = CompiledAddedEffects.References(compiled, logicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var materials = splits.Select(split => split.Material).Where(material => !known.Contains(material)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (materials.Length > 0)
        {
            var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
                ?? throw Errors.Unsupported("SEE_THROUGH_LAYOUT", "The model has no list of the files it uses.", "Report the model.");
            payloads[resource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(compiled.AsSpan((int)references.Offset, (int)references.Size), materials, replace: false);
        }

        return CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
    }

    /// <summary>The same triangles in the same order; the codec may start a triangle at another corner (same facing).</summary>
    private static bool SameTriangles(byte[] decoded, uint[] indices, int size)
    {
        uint At(int i) => size == 2 ? BitConverter.ToUInt16(decoded, i * 2) : BitConverter.ToUInt32(decoded, i * 4);
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            var (x, y, z) = (At(t), At(t + 1), At(t + 2));
            if (!((x == a && y == b && z == c) || (x == b && y == c && z == a) || (x == c && y == a && z == b)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The material (<c>.vmat</c> path) of level-0 draw call <paramref name="part"/>, in the preview's order.</summary>
    public static string DrawMaterial(byte[] compiled, string logicalPath, int part)
    {
        using var resource = CompiledAddedEffects.Open(compiled, logicalPath, out var model);
        var draws = model.GetEmbeddedMeshesForLod(0).SelectMany(item => item.Mesh.Data["m_sceneObjects"].Values.SelectMany(sceneObject => sceneObject["m_drawCalls"].Values)).ToArray();
        return part >= 0 && part < draws.Length ? draws[part]["m_material"].ToString(CultureInfo.InvariantCulture)
            : throw Errors.Input("SEE_THROUGH_PART_RANGE", $"The model has {draws.Length} parts; part {part + 1} is not one of them.", "Open the preview again.");
    }

    private static int Int(KVObject parent, string key) => (int)parent[key].ToInt64(CultureInfo.InvariantCulture);
}

using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

/// <summary>
/// Edits of a compiled model that leave everything else byte for byte: the changed blocks are re-serialized with
/// ValveResourceFormat (shown game-safe for a model's DATA block, X1 control test 2026-11-04) and spliced into the original
/// file; every other block keeps its original bytes.
/// </summary>
public static class CompiledModelEdit
{
    /// <summary>
    /// Hides draw calls of the level-0 embedded meshes (indices in draw order, as the preview lists parts) by giving them no
    /// triangles (<c>m_nIndexCount</c> 0). Physics, hit boxes, bones and other meshes are unchanged.
    /// </summary>
    public static byte[] HideDrawCalls(byte[] compiled, string logicalPath, IReadOnlySet<int> drawIndices)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(drawIndices);
        using var resource = new Resource { FileName = logicalPath };
        resource.Read(new MemoryStream(compiled));
        var model = resource.DataBlock as Model ?? throw Errors.Input("MODEL_EDIT_NOT_A_MODEL", $"'{logicalPath}' is not a model.", "Pass a compiled .vmdl_c.");
        var changed = new List<Block>();
        var index = 0;
        foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesForLod(0))
        {
            foreach (var sceneObject in mesh.Data["m_sceneObjects"].Values)
            {
                foreach (var draw in sceneObject["m_drawCalls"].Values)
                {
                    if (drawIndices.Contains(index++))
                    {
                        draw["m_nIndexCount"] = Zero(draw["m_nIndexCount"]);
                        if (!changed.Contains(mesh))
                        {
                            changed.Add(mesh);
                        }
                    }
                }
            }
        }

        if (drawIndices.Any(drawIndex => drawIndex < 0 || drawIndex >= index))
        {
            throw Errors.Input("MODEL_EDIT_PART_RANGE", $"The model has {index} parts; a hidden part is outside that range.", "Open the preview again and choose the parts.");
        }

        if (changed.Any(block => !resource.Blocks.Contains(block)))
        {
            throw Errors.Unsupported("MODEL_EDIT_EXTERNAL_MESH", "A part belongs to a mesh stored in another file.", "Only models with embedded meshes can be edited.");
        }

        return ReplaceBlocks(compiled, resource, changed.ToDictionary(block => resource.Blocks.IndexOf(block), Serialize));
    }

    /// <summary>
    /// Rebuilds a compiled resource with new payloads for some blocks (by index in the block table); the header and the other
    /// blocks are copied unchanged, blocks stay in their order and 16-byte aligned. Data after the last block (a texture's
    /// pixels, a sound's stream) is not supported here; zero padding up to 16 bytes is, and is not written back.
    /// </summary>
    public static byte[] ReplaceBlocks(byte[] original, Resource resource, IReadOnlyDictionary<int, byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(payloads);
        var table = 8 + (int)BitConverter.ToUInt32(original, 8);
        var count = (int)BitConverter.ToUInt32(original, 12);
        var last = resource.Blocks.Max(block => block.Offset + block.Size);
        var padding = original.Length - last;                               // some compilers pad the file to 16 bytes with zeros
        if (count != resource.Blocks.Count || padding < 0 || padding >= 16 || original.AsSpan((int)last).ContainsAnyExcept((byte)0))
        {
            throw Errors.Unsupported("MODEL_EDIT_LAYOUT", "The resource has data after its blocks or an unexpected block table.", "Report the file.");
        }

        var file = new MemoryStream();
        file.Write(original, 0, table + (count * 12));                      // header and block table, patched below
        var placed = new (long At, int Size)[count];
        for (var i = 0; i < count; i++)
        {
            while (file.Position % 16 != 0)
            {
                file.WriteByte(0);
            }

            var payload = payloads.TryGetValue(i, out var replaced) ? replaced : original.AsSpan((int)resource.Blocks[i].Offset, (int)resource.Blocks[i].Size).ToArray();
            placed[i] = (file.Position, payload.Length);
            file.Write(payload);
        }

        var bytes = file.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)bytes.Length);
        for (var i = 0; i < count; i++)
        {
            var at = table + (i * 12);
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 4, 4), (uint)(placed[i].At - (at + 4)));
            BitConverter.TryWriteBytes(bytes.AsSpan(at + 8, 4), (uint)placed[i].Size);
        }

        return bytes;
    }

    private static byte[] Serialize(Block block)
    {
        using var stream = new MemoryStream();
        block.Serialize(stream);
        return stream.ToArray();
    }

    /// <summary>Zero in the value's own integer type (the game reads typed counts).</summary>
    private static KVObject Zero(KVObject value) => value.ValueType switch
    {
        KVValueType.UInt64 => (KVObject)0UL,
        KVValueType.UInt32 => (KVObject)0U,
        KVValueType.Int64 => (KVObject)0L,
        KVValueType.UInt16 => (KVObject)(ushort)0,
        KVValueType.Int16 => (KVObject)(short)0,
        _ => (KVObject)0,
    };
}

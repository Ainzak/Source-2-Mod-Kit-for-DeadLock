using OpenTK.Graphics.OpenGL;
using S2ModKit.Application;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Materials;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Own copies in the preview, drawn as the build writes them: a see-through part, and every piece whose colour, shine or see-through
/// changes, gets its own copy of its material drawing its own copies of the textures it changes (colour: recolored, with the chosen
/// alpha when see-through; normal-roughness: shine). A piece also gets its own draw call: its part's triangles are put in the same new
/// order on the graphics card (the rest first, the piece after) and a copy of the draw call draws the piece's range. The shared material
/// and textures stay, so the part's other pieces (the other boot, painted from the same texture area) keep their look.
/// </summary>
public sealed partial class PreviewSession
{
    private readonly Dictionary<int, Own> owns = [];                                 // by part or piece index
    private readonly List<Resource> opacityResources = [];

    private sealed class Own
    {
        public required int Index { get; init; }

        public required DrawCall Target { get; init; }                            // the piece's own draw call, or a see-through part's own

        public required RenderMaterial Original { get; init; }

        public required RenderMaterial Solid { get; init; }

        public RenderMaterial? Translucent { get; set; }

        public required string ColourPath { get; init; }

        public RenderTexture? Colour { get; set; }

        public RenderTexture? Normal { get; set; }

        public float Opacity { get; set; } = 1f;

        public ColorAdjustment? Change { get; set; }

        public float Shine { get; set; }

        public DrawCall? PieceOf { get; init; }

        public int PieceCount { get; init; }

        public RenderMaterial Current => Opacity < 0.995f && Translucent is not null ? Translucent : Solid;
    }

    private bool IsPiece(int index) => index >= picker.Count;

    /// <summary>Shows part or piece <paramref name="index"/> see-through by <paramref name="amount"/> (0: as before); <paramref name="translucentMaterial"/>
    /// is the translucent compiled material copy the build writes (needed the first time). Returns why the preview cannot, or null.</summary>
    public string? SetPartSeeThrough(int index, float amount, byte[]? translucentMaterial)
    {
        try
        {
            if (!owns.TryGetValue(index, out var own) && amount <= 0.005f)
            {
                return null;
            }

            own ??= Create(index);
            if (own is null)
            {
                return "This part has no colour texture.";
            }

            if (amount > 0.005f && own.Translucent is null)
            {
                if (translucentMaterial is null)
                {
                    return "The see-through material is not ready yet.";
                }

                own.Translucent = LoadCopy(own.Original, translucentMaterial);
            }

            own.Opacity = 1f - amount;
            Show(own);
            return null;
        }
#pragma warning disable CA1031 // the renderer's material loader throws many kinds; reported to the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return $"The preview cannot show this part see-through ({exception.Message}).";
        }
    }

    /// <summary>A piece's recolor (null: its own colours back), on its own copies.</summary>
    private void SetOwnColour(int index, ColorAdjustment? change)
    {
        if ((owns.TryGetValue(index, out var own) ? own : change is null ? null : Create(index)) is { } target)
        {
            target.Change = change;
            Show(target);
        }
    }

    /// <summary>A piece's shine (0: as the game has it), on its own copy of the normal-roughness texture.</summary>
    private void SetOwnShine(int index, float shine)
    {
        if ((owns.TryGetValue(index, out var own) ? own : shine <= 0 ? null : Create(index)) is { } target)
        {
            target.Shine = shine;
            Show(target);
        }
    }

    /// <summary>Draws an own copy as its settings say: material (solid or see-through), textures, draw list, outline.</summary>
    private void Show(Own own)
    {
        var part = picker.ParentOf(own.Index);
        var (mesh, draw) = drawOrder[part];
        var material = own.Current;
        UpdateTextures(own);
        var see = ReferenceEquals(material, own.Translucent);
        if (own.PieceOf is null)
        {
            draw.SetNewMaterial(see ? material : own.Original);               // a whole part is only taken over while see-through
            var (from, to) = see ? (mesh.DrawCallsOpaque, mesh.DrawCallsBlended) : (mesh.DrawCallsBlended, mesh.DrawCallsOpaque);
            if (from.Remove(draw))
            {
                to.Add(draw);
            }

            ink.SetNoShell(part, see);
            return;
        }

        own.Target.SetNewMaterial(material);
        mesh.DrawCallsOpaque.Remove(own.Target);
        mesh.DrawCallsBlended.Remove(own.Target);
        (see ? mesh.DrawCallsBlended : mesh.DrawCallsOpaque).Add(own.Target);
        ink.SetPieceNoShell(part, (IReadOnlyCollection<int>)picker.Vertices(own.Index), see);   // its own shell would darken it; the build has none
    }

    /// <summary>A part's or piece's own copy: its material loaded again (with the same render arguments), and for a piece its own draw
    /// call. Null without a colour texture.</summary>
    private Own? Create(int index)
    {
        if (PartColorTexture(index) is not { } colourPath)
        {
            return null;
        }

        var part = picker.ParentOf(index);
        var (_, draw) = drawOrder[part];
        var original = OriginalMaterial(draw);
        if (!IsPiece(index))
        {
            var whole = new Own { Index = index, Target = draw, Original = original, Solid = original, ColourPath = colourPath };
            owns[index] = whole;
            return whole;
        }

        // a piece: the same triangle order as the build (the rest of the part first, then each piece with its own copy in turn), and a
        // draw call for the piece; the part's first order is kept so that more pieces can be taken out later
        var (_, triangles) = picker.PieceTriangles(index);
        var size = draw.IndexSizeInBytes;
        if (!splitParts.TryGetValue(part, out var split))
        {
            var count = hiddenIndexCounts.GetValueOrDefault(part, draw.IndexCount);
            var first = new byte[count * size];
            GL.GetNamedBufferSubData(draw.IndexBuffer.Handle, draw.StartIndex, first.Length, first);
            split = (first, []);
            splitParts[part] = split;
        }

        split.Pieces.Add(index);
        var solid = LoadCopy(original, null);
        var copy = new DrawCall
        {
            PrimitiveType = draw.PrimitiveType,
            BaseVertex = draw.BaseVertex,
            VertexCount = draw.VertexCount,
            StartIndex = draw.StartIndex,
            IndexCount = triangles.Length * 3,
            TintColor = draw.TintColor,
            DrawBounds = draw.DrawBounds,
            MeshId = draw.MeshId,
            MeshBuffers = draw.MeshBuffers,
            MeshName = draw.MeshName,
            VertexBuffers = draw.VertexBuffers,
            IndexType = draw.IndexType,
            IndexBuffer = draw.IndexBuffer,
            VertexIdOffset = draw.VertexIdOffset,
            Material = solid,
        };
        copy.UpdateVertexArrayObject();
        var own = new Own { Index = index, Target = copy, Original = original, Solid = solid, ColourPath = colourPath, PieceOf = draw, PieceCount = triangles.Length * 3 };
        owns[index] = own;
        Reorder(part, draw, split);
        return own;
    }

    private readonly Dictionary<int, (byte[] First, List<int> Pieces)> splitParts = [];

    /// <summary>Writes part <paramref name="part"/>'s triangles in the build's order (the rest, then each piece) and points the draw calls at their ranges.</summary>
    private void Reorder(int part, DrawCall draw, (byte[] First, List<int> Pieces) split)
    {
        var size = draw.IndexSizeInBytes;
        var triangles = split.First.Length / size / 3;
        var sets = split.Pieces.Select(piece => picker.PieceTriangles(piece).Triangles.ToHashSet()).ToArray();
        var taken = sets.SelectMany(set => set).ToHashSet();
        var ordered = new byte[split.First.Length];
        var at = 0;
        foreach (var t in Enumerable.Range(0, triangles).Where(t => !taken.Contains(t)).Concat(sets.SelectMany(set => Enumerable.Range(0, triangles).Where(set.Contains))))
        {
            split.First.AsSpan(t * 3 * size, 3 * size).CopyTo(ordered.AsSpan(at));
            at += 3 * size;
        }

        GL.NamedBufferSubData(draw.IndexBuffer.Handle, draw.StartIndex, ordered.Length, ordered);
        var rest = (triangles - taken.Count) * 3;
        SetCount(part, draw, rest);
        var next = rest;
        for (var i = 0; i < split.Pieces.Count; i++)
        {
            var target = owns[split.Pieces[i]].Target;
            target.StartIndex = draw.StartIndex + (next * size);
            target.IndexCount = sets[i].Count * 3;
            next += sets[i].Count * 3;
        }
    }

    /// <summary>A copy of <paramref name="original"/> from <paramref name="compiled"/> (null: its own file again), with the original's render
    /// arguments (skinning, compressed normals: without them the copy drew the part in bind pose) and its textures.</summary>
    private RenderMaterial LoadCopy(RenderMaterial original, byte[]? compiled)
    {
        var resource = compiled is null ? loader.LoadFileCompiled(original.Material.Name) : new Resource { FileName = original.Material.Name + "_c" };
        if (resource is null)
        {
            throw new InvalidDataException($"'{original.Material.Name}' cannot be read again.");
        }

        opacityResources.Add(resource);
        if (compiled is not null)
        {
            resource.Read(new MemoryStream(compiled));
        }

        var arguments = original.Shader.Parameters.Where(pair => pair.Key.StartsWith("D_", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var material = context.MaterialLoader.LoadMaterial(resource, arguments);
        foreach (var (name, value) in original.Textures)
        {
            material.Textures[name] = value;
        }

        return material;
    }

    /// <summary>A part's index count, kept for a hidden part until it shows again.</summary>
    private void SetCount(int part, DrawCall draw, int count)
    {
        if (hiddenIndexCounts.ContainsKey(part))
        {
            hiddenIndexCounts[part] = count;
        }
        else
        {
            draw.IndexCount = count;
        }
    }

    /// <summary>The own copy's textures: the colour texture (its recolor, its alpha when see-through) and the normal-roughness one (shine).</summary>
    private void UpdateTextures(Own own)
    {
        var see = own.Opacity < 0.995f;
        own.Colour?.Delete();
        own.Colour = null;
        if ((own.Change is not null || see || lastRgba.ContainsKey(own.ColourPath)) && Decode(own.ColourPath) is { } source)
        {
            var rgba = (byte[])(lastRgba.TryGetValue(own.ColourPath, out var shared) ? shared : source.PreviewRgba).Clone();   // the part's recolor too
            if (own.Change is { } change)
            {
                RecolorMath.Apply(rgba, Mask(own.Index, source.PreviewWidth, source.PreviewHeight), change);
            }

            if (see)
            {
                var translucent = own.Original.Material.IntParams.GetValueOrDefault("F_TRANSLUCENT") != 0;
                var keep = Math.Clamp(own.Opacity, 0f, 1f);
                for (var i = 3; i < rgba.Length; i += 4)
                {
                    rgba[i] = translucent ? (byte)Math.Round(rgba[i] * keep) : (byte)Math.Round(keep * 255);
                }
            }

            using var resource = new Resource { FileName = own.ColourPath + "_c" };
            resource.Read(new MemoryStream(RgbaTexture(rgba, source.PreviewWidth, source.PreviewHeight)));
            own.Colour = context.MaterialLoader.LoadTexture(resource, srgbRead: source.Srgb, isViewerRequest: false);
        }

        own.Normal?.Delete();
        own.Normal = null;
        if (own.Shine > 0 && own.Original.Material.TextureParams.TryGetValue(NormalRoughnessTexture, out var normalPath)
            && shineUnpacking.TryGetValue(normalPath, out var task) && task.GetAwaiter().GetResult() is { } pixels)
        {
            var rgba = (byte[])pixels.Rgba.Clone();
            ShineMath.Apply(rgba, pixels.Width * pixels.Height <= 16 ? null : picker.Mask(own.Index, pixels.Width, pixels.Height), own.Shine, pixels.Channel);
            using var resource = new Resource { FileName = normalPath + "_c" };
            resource.Read(new MemoryStream(RgbaTexture(rgba, pixels.Width, pixels.Height)));
            own.Normal = context.MaterialLoader.LoadTexture(resource, srgbRead: false, isViewerRequest: false);   // normals are linear
        }

        foreach (var copy in new[] { own.Solid, own.Translucent }.OfType<RenderMaterial>().Where(copy => !ReferenceEquals(copy, own.Original)))
        {
            if (own.Original.Textures.TryGetValue(ColorTexture, out var colour))
            {
                copy.Textures[ColorTexture] = own.Colour ?? colour;
            }

            if (own.Original.Textures.TryGetValue(NormalRoughnessTexture, out var normal))
            {
                copy.Textures[NormalRoughnessTexture] = own.Normal ?? normal;
            }

            foreach (var (name, value) in own.Original.Material.VectorParams.Where(pair => MaterialColors.Follows(pair.Key, pair.Value)))
            {
                copy.Material.VectorParams[name] = own.Change is null ? value : MaterialColors.Apply(value, own.Change);   // tints and outline follow
            }
        }
    }

    /// <summary>The compiled material path of part or piece <paramref name="index"/> (<c>.vmat</c>, without <c>_c</c>).</summary>
    public string PartMaterialPath(int index) => OriginalMaterial(Draw(index)).Material.Name;

    /// <summary>How many other parts are drawn with part <paramref name="index"/>'s material.</summary>
    public int PartMaterialSharers(int index)
    {
        var material = OriginalMaterial(Draw(index));
        return drawOrder.Count(item => ReferenceEquals(OriginalMaterial(item.Draw), material)) - 1;
    }

    /// <summary>The material a part was loaded with.</summary>
    private RenderMaterial OriginalMaterial(DrawCall draw) =>
        owns.Values.FirstOrDefault(item => ReferenceEquals(item.Translucent, draw.Material) || (ReferenceEquals(item.Solid, draw.Material) && !ReferenceEquals(item.Solid, item.Original)))?.Original
        ?? draw.Material;

    private void DisposeOpacity()
    {
        foreach (var own in owns.Values)
        {
            own.Colour?.Delete();
            own.Normal?.Delete();
        }

        owns.Clear();
        foreach (var resource in opacityResources)
        {
            resource.Dispose();
        }

        opacityResources.Clear();
    }
}

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Emulation of Deadlock's character ink outline (the renderer library has none): the posed mesh, skinned on the CPU,
/// every vertex pushed along its own normal by thickness x the red channel of the material's g_tNprOutlineMask, drawn
/// with front faces culled into the scene depth, multiplying the colour behind it by the material's solid outline
/// colour (g_vSolidOutlineTint + g_vSolidOutlineAdditive; near black otherwise). Materials with F_DISABLE_NPR_OUTLINE=1
/// get none. Alpha-tested materials (F_ALPHA_TEST) lose the triangles whose colour texture is transparent at all three
/// corners: mods hide clothes with a transparent material, and the game shows no outline there (a shell drawn from them
/// put black clothes with holes on Bottom Heavy Vindicta, 2026-11-03). Where the shell pokes through the surface
/// (creases, tight folds) it shows as dark lines, as in game.
/// </summary>
internal sealed class InkOutline : IDisposable
{
    private const string VertexSource = """
        #version 460 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uViewProjection;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = uViewProjection * vec4(aPosition, 1.0); }
        """;

    private const string FragmentSource = """
        #version 460 core
        in vec3 vColor;
        out vec4 outColor;
        void main() { outColor = vec4(vColor, 1.0); }
        """;

    private readonly List<Part> parts = [];
    private readonly ModelSceneNode node;
    private readonly int program;
    private readonly int vao;
    private readonly int vbo;
    private readonly int ebo;
    private readonly int viewProjection;
    private readonly int indexCount;
    private readonly int vertexCount;

    public InkOutline(GameFileLoader loader, Model model, ModelSceneNode node)
    {
        this.node = node;
        var active = node.RenderableMeshes.Select(mesh => mesh.MeshIndex).ToHashSet();
        var indices = new List<uint>();
        foreach (var (mesh, meshIndex, _, _) in model.GetEmbeddedMeshesForLod(0))
        {
            if (!active.Contains(meshIndex))
            {
                continue;
            }

            var remap = model.GetRemapTable(meshIndex) ?? [];
            foreach (var sceneObject in Items(mesh.Data, "m_sceneObjects"))
            {
                foreach (var draw in Items(sceneObject, "m_drawCalls"))
                {
                    var style = OutlineStyle.Read(loader, draw["m_material"].ToString(CultureInfo.InvariantCulture));
                    if (style is null)
                    {
                        continue;
                    }

                    var part = Part.Read(mesh.VBIB, draw, remap, style, vertexCount, indices);
                    parts.Add(part);
                    vertexCount += part.Positions.Length;
                }
            }
        }

        indexCount = indices.Count;
        program = Program();
        viewProjection = GL.GetUniformLocation(program, "uViewProjection");
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        ebo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexCount * 6 * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        var indexArray = indices.ToArray();
        GL.BufferData(BufferTarget.ElementArrayBuffer, indexArray.Length * sizeof(uint), indexArray, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    public int Parts => parts.Count;

    /// <summary>Skins the current pose, then draws the shell into <paramref name="framebuffer"/> (its depth is the scene's).</summary>
    public void Draw(int framebuffer, int width, int height, Matrix4x4 viewProjectionMatrix, float thickness)
    {
        if (indexCount == 0 || thickness <= 0)
        {
            return;
        }

        var skeleton = node.AnimationController.Skeleton;
        var bones = new Matrix4x4[Math.Max(1, skeleton.Bones.Length)];
        node.AnimationController.GetSkinningMatrices(bones);
        var transform = node.Transform;
        var data = new float[vertexCount * 6];
        var offset = 0;
        foreach (var part in parts)
        {
            part.Write(bones, transform, thickness, data, offset);
            offset += part.Positions.Length * 6;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.Viewport(0, 0, width, height);
        GL.UseProgram(program);
        GL.UniformMatrix4(viewProjection, 1, false, ref viewProjectionMatrix.M11);
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, data.Length * sizeof(float), data);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Front);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
        GL.ColorMask(true, true, true, false);
        GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, 0);
        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.Blend);
        GL.CullFace(TriangleFace.Back);
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static IEnumerable<KVObject> Items(KVObject parent, string key)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Domain.Errors.Unsupported("PREVIEW_MESH_LAYOUT", $"Mesh data has no '{key}' array.", "Preview a compiled model with embedded meshes.");
        }

        for (var i = 0; i < value.Count; i++)
        {
            yield return value[i];
        }
    }

    private static int Program()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
            {
                throw new InvalidOperationException("Ink outline shader: " + GL.GetShaderInfoLog(shader));
            }

            return shader;
        }

        var vs = Compile(ShaderType.VertexShader, VertexSource);
        var fs = Compile(ShaderType.FragmentShader, FragmentSource);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    public void Dispose()
    {
        GL.DeleteBuffer(vbo);
        GL.DeleteBuffer(ebo);
        GL.DeleteVertexArray(vao);
        GL.DeleteProgram(program);
    }

    private sealed record OutlineStyle(Vector3 Color, Func<Vector2, float> Mask, Func<Vector2, float>? Alpha, float AlphaReference)
    {
        public static OutlineStyle? Read(GameFileLoader loader, string materialPath)
        {
            using var resource = loader.LoadFileCompiled(materialPath.EndsWith("_c", StringComparison.Ordinal) ? materialPath[..^2] : materialPath);
            if (resource?.DataBlock is not Material material)
            {
                return new OutlineStyle(new Vector3(0.05f), _ => 1f, null, 0f);
            }

            if (material.IntParams.TryGetValue("F_DISABLE_NPR_OUTLINE", out var disabled) && disabled != 0)
            {
                return null;
            }

            var color = new Vector3(0.05f);
            if (material.IntParams.TryGetValue("F_SOLID_COLOR_OUTLINE", out var solid) && solid != 0)
            {
                var tint = material.VectorParams.TryGetValue("g_vSolidOutlineTint", out var t) ? t : Vector4.Zero;
                var add = material.VectorParams.TryGetValue("g_vSolidOutlineAdditive", out var a) ? a : Vector4.Zero;
                color = Vector3.Clamp(new Vector3(tint.X + add.X, tint.Y + add.Y, tint.Z + add.Z), Vector3.Zero, Vector3.One);
            }

            var alphaTest = material.IntParams.TryGetValue("F_ALPHA_TEST", out var test) && test != 0;
            var reference = material.FloatParams.TryGetValue("g_flAlphaTestReference", out var r) ? r : 0.5f;
            return new OutlineStyle(color, Sampler(loader, material, "g_tNprOutlineMask", alpha: false) ?? (_ => 1f),
                alphaTest ? Sampler(loader, material, "g_tColor", alpha: true) : null, reference);
        }

        /// <summary>Nearest-texel lookup of one channel (red, or alpha) of a material texture; null without it.</summary>
        private static Func<Vector2, float>? Sampler(GameFileLoader loader, Material material, string parameter, bool alpha)
        {
            if (!material.TextureParams.TryGetValue(parameter, out var path))
            {
                return null;
            }

            using var resource = loader.LoadFileCompiled(path);
            if (resource?.DataBlock is not Texture texture)
            {
                return null;
            }

            using var bitmap = texture.GenerateBitmap();
            var (w, h) = (bitmap.Width, bitmap.Height);
            var channel = new float[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    channel[y * w + x] = (alpha ? pixel.Alpha : pixel.Red) / 255f;
                }
            }

            return uv =>
            {
                var x = (int)Math.Clamp(MathF.Floor((uv.X - MathF.Floor(uv.X)) * w), 0, w - 1);
                var y = (int)Math.Clamp(MathF.Floor((uv.Y - MathF.Floor(uv.Y)) * h), 0, h - 1);
                return channel[y * w + x];
            };
        }
    }

    private sealed class Part
    {
        public required Vector3[] Positions { get; init; }
        public required Vector3[] Normals { get; init; }
        public required float[] Masks { get; init; }
        public required ushort[] Bones { get; init; }
        public required Vector4[] Weights { get; init; }
        public required int BonesPerVertex { get; init; }
        public required Vector3 Color { get; init; }

        public static Part Read(VBIB vbib, KVObject draw, int[] remap, OutlineStyle style, int vertexBase, List<uint> indices)
        {
            var bufferRef = draw["m_vertexBuffers"][0];
            var buffer = vbib.VertexBuffers[(int)bufferRef["m_hBuffer"].ToInt64(CultureInfo.InvariantCulture)];
            var baseVertex = (int)draw["m_nBaseVertex"].ToInt64(CultureInfo.InvariantCulture);
            var fields = buffer.InputLayoutFields;
            VBIB.RenderInputLayoutField Field(string name) => fields.FirstOrDefault(f => f.SemanticName == name && f.SemanticIndex == 0);
            var position = Field("POSITION");
            if (position.SemanticName is null)
            {
                throw Domain.Errors.Unsupported("PREVIEW_MESH_LAYOUT", "A vertex buffer has no POSITION.", "Preview a standard compiled character model.");
            }

            var allPositions = VBIB.GetVector3AttributeArray(buffer, position);
            var allNormals = VBIB.GetNormalTangentArray(buffer, Field("NORMAL")).Item1;
            var texcoord = Field("TEXCOORD");
            var uvs = texcoord.SemanticName is null ? null : VBIB.GetVector2AttributeArray(buffer, texcoord);
                var blend = Field("BLENDINDICES");
            var weightField = Field("BLENDWEIGHT");
            if (weightField.SemanticName is null)
            {
                weightField = Field("BLENDWEIGHTS");                         // the name some mod tools write (Beach Vindicta): without
            }                                                               // it every vertex followed its first bone only
            var allBones = blend.SemanticName is null ? new ushort[allPositions.Length * 4] : VBIB.GetBlendIndicesArray(buffer, blend, remap);
            var perVertex = Math.Max(1, allBones.Length / allPositions.Length);
            var allWeights = weightField.SemanticName is null ? null : VBIB.GetBlendWeightsArray(buffer, weightField);

            var indexRef = draw["m_indexBuffer"];
            var indexBuffer = vbib.IndexBuffers[(int)indexRef["m_hBuffer"].ToInt64(CultureInfo.InvariantCulture)];
            var start = (int)draw["m_nStartIndex"].ToInt64(CultureInfo.InvariantCulture);
            var count = (int)draw["m_nIndexCount"].ToInt64(CultureInfo.InvariantCulture);
            var bindOffset = indexRef.TryGetValue("m_nBindOffsetBytes", out var o) ? (int)o.ToInt64(CultureInfo.InvariantCulture) : 0;
            var size = (int)indexBuffer.ElementSizeInBytes;
            var raw = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = bindOffset + (start + i) * size;
                raw[i] = baseVertex + (size == 2 ? BitConverter.ToUInt16(indexBuffer.Data, at) : (int)BitConverter.ToUInt32(indexBuffer.Data, at));
            }

            if (style.Alpha is { } alphaAt && uvs is not null)                // transparent triangles get no shell
            {
                var kept = new List<int>(raw.Length);
                for (var t = 0; t + 2 < raw.Length; t += 3)
                {
                    if (alphaAt(uvs[raw[t]]) >= style.AlphaReference || alphaAt(uvs[raw[t + 1]]) >= style.AlphaReference || alphaAt(uvs[raw[t + 2]]) >= style.AlphaReference)
                    {
                        kept.AddRange([raw[t], raw[t + 1], raw[t + 2]]);
                    }
                }

                raw = [.. kept];
            }

            var used = raw.Distinct().Order().ToArray();
            var local = new Dictionary<int, int>(used.Length);
            for (var i = 0; i < used.Length; i++)
            {
                local[used[i]] = i;
            }

            indices.AddRange(raw.Select(i => (uint)(vertexBase + local[i])));
            return new Part
            {
                Positions = used.Select(i => allPositions[i]).ToArray(),
                Normals = used.Select(i => allNormals[i]).ToArray(),
                Masks = used.Select(i => uvs is null ? 1f : style.Mask(uvs[i])).ToArray(),
                Bones = used.SelectMany(i => allBones.AsSpan(i * perVertex, perVertex).ToArray()).ToArray(),
                Weights = used.Select(i => allWeights is null ? new Vector4(1, 0, 0, 0) : allWeights[i]).ToArray(),
                BonesPerVertex = perVertex,
                Color = style.Color,
            };
        }

        public void Write(Matrix4x4[] skin, Matrix4x4 transform, float thickness, float[] data, int offset)
        {
            for (var v = 0; v < Positions.Length; v++)
            {
                var weights = Weights[v];
                var p = Vector3.Zero;
                var n = Vector3.Zero;
                var total = 0f;
                for (var k = 0; k < Math.Min(4, BonesPerVertex); k++)
                {
                    var w = k switch { 0 => weights.X, 1 => weights.Y, 2 => weights.Z, _ => weights.W };
                    if (w <= 0)
                    {
                        continue;
                    }

                    var bone = Bones[v * BonesPerVertex + k];
                    var m = bone < skin.Length ? skin[bone] : Matrix4x4.Identity;
                    p += w * Vector3.Transform(Positions[v], m);
                    n += w * Vector3.TransformNormal(Normals[v], m);
                    total += w;
                }

                if (total <= 0)
                {
                    p = Positions[v];
                    n = Normals[v];
                }
                else
                {
                    p /= total;
                }

                n = Vector3.TransformNormal(n, transform);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                p = Vector3.Transform(p, transform) + n * thickness * Masks[v];
                var at = offset + v * 6;
                data[at] = p.X;
                data[at + 1] = p.Y;
                data[at + 2] = p.Z;
                data[at + 3] = Color.X;
                data[at + 4] = Color.Y;
                data[at + 5] = Color.Z;
            }
        }
    }
}

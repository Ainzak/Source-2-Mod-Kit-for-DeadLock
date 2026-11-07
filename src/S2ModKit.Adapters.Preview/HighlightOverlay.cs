using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Draws one part over the rendered model in a pulsing orange, from its CPU-posed triangles (the same data as part picking),
/// with the scene's depth so only its visible surface lights up. Unlike a material tint it works for every shader.
/// </summary>
internal sealed class HighlightOverlay : IDisposable
{
    private const string VertexSource = """
        #version 460 core
        layout(location = 0) in vec3 aPosition;
        uniform mat4 uViewProjection;
        void main() { gl_Position = uViewProjection * vec4(aPosition, 1.0); }
        """;

    private const string FragmentSource = """
        #version 460 core
        uniform vec4 uColor;
        out vec4 outColor;
        void main() { outColor = uColor; }
        """;

    private readonly int program;
    private readonly int vao;
    private readonly int vbo;
    private readonly int viewProjection;
    private readonly int color;

    public HighlightOverlay()
    {
        program = Program();
        viewProjection = GL.GetUniformLocation(program, "uViewProjection");
        color = GL.GetUniformLocation(program, "uColor");
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws <paramref name="part"/> (three positions per triangle) into <paramref name="framebuffer"/> after the scene. The
    /// scene's depth comes from GPU skinning, which differs slightly from the CPU pose, so testing against it lost whole parts;
    /// instead the depth is rebuilt from <paramref name="occluders"/> (every visible part, posed the same way) and the part
    /// passes exactly where it is the nearest surface. Called just before post-processing, which reads colour only.
    /// </summary>
    public void Draw(int framebuffer, int width, int height, Matrix4x4 viewProjectionMatrix, Vector3[] part, Vector3[] occluders, float pulse)
    {
        if (part.Length < 3)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.Viewport(0, 0, width, height);
        GL.UseProgram(program);
        GL.UniformMatrix4(viewProjection, 1, false, ref viewProjectionMatrix.M11);
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.GetInteger(GetPName.DepthFunc, out int sceneTest);
        GL.GetFloat(GetPName.DepthClearValue, out float sceneClear);
        var reversed = (DepthFunction)sceneTest is DepthFunction.Greater or DepthFunction.Gequal;   // reversed depth: nearer is larger
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.ClearDepth(reversed ? 0.0 : 1.0);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.DepthFunc(reversed ? DepthFunction.Gequal : DepthFunction.Lequal);

        GL.ColorMask(false, false, false, false);                            // depth of the CPU-posed model
        GL.BufferData(BufferTarget.ArrayBuffer, occluders.Length * 3 * sizeof(float), occluders, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, occluders.Length);

        GL.ColorMask(true, true, true, false);                               // the part where it is in front
        GL.DepthMask(false);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);          // additive glow
        GL.Uniform4(color, 1.0f, 0.55f, 0.1f, 0.25f + (0.3f * pulse));
        GL.BufferData(BufferTarget.ArrayBuffer, part.Length * 3 * sizeof(float), part, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, part.Length);

        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.Blend);
        GL.DepthFunc((DepthFunction)sceneTest);
        GL.ClearDepth(sceneClear);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(vbo);
        GL.DeleteVertexArray(vao);
        GL.DeleteProgram(program);
    }

    private static int Program()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            return ok != 0 ? shader : throw new InvalidOperationException("Highlight shader: " + GL.GetShaderInfoLog(shader));
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
}

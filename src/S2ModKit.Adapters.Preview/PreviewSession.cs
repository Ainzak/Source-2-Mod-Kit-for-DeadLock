using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using S2ModKit.Application;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.World;
using ValveResourceFormat.ResourceTypes;
using VRenderer = ValveResourceFormat.Renderer.Renderer;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Where the session loads files from: archives in search order (first wins), the model, and the lighting: a map whose
/// lights, probes and post-processing surround the model at the origin (for example the game's hero-select scene,
/// <c>maps/ui/hero_select.vmap</c>; its archive must be in <c>Archives</c>), or else a light probe cubemap.
/// </summary>
public sealed record PreviewSessionOptions(IReadOnlyList<string> Archives, string ModelPath, string? EnvironmentMap = null, string? SceneMap = null);

/// <summary>
/// A live preview of one compiled model with the Source 2 Viewer renderer (ValveResourceFormat.Renderer): clip, time,
/// camera, then <see cref="Capture"/>. Create, use and dispose it on the thread whose OpenGL context is current.
/// Root motion is not applied (the model runs in place).
/// </summary>
public sealed partial class PreviewSession : IDisposable
{
    public const string DefaultEnvironmentMap = "materials/default/default_cube_pfm_2a7085f.vtex";
    private const int Samples = 4;

    private readonly GameFileLoader loader;
    private readonly List<Package> packages = [];
    private readonly RendererContext context;
    private readonly VRenderer renderer;
    private readonly Resource modelResource;
    private Resource? environment;
    private readonly ModelSceneNode node;
    private readonly TextRenderer text;
    private readonly InkOutline ink;
    private Framebuffer? main;
    private Framebuffer? display;
    private float time;

    private PreviewSession(PreviewSessionOptions options)
    {
        loader = new GameFileLoader(null, null);
        foreach (var path in options.Archives)
        {
            var package = new Package();
            package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
            package.Read(path);
            packages.Add(package);
            loader.AddPackageToSearch(package);
        }

        context = new RendererContext(loader, NullLogger.Instance) { MaxTextureSize = 4096 };
        GLEnvironment.SetDefaultRenderState(context);
        renderer = new VRenderer(context);
        renderer.Initialize();
        var scene = renderer.Scene;

        EnsureTargets(64, 64);
        if (options.SceneMap is { } map)
        {
            var mapPath = map.EndsWith("_c", StringComparison.Ordinal) ? map : map + "_c";
            var world = WorldLoader.LoadMap(mapPath, scene)
                ?? throw Domain.Errors.Input("PREVIEW_SCENE_MISSING", $"Scene map '{mapPath}' did not load.", "Add the map's VPK to the archives.");
            foreach (var mapNode in scene.AllNodes.ToArray())
            {
                mapNode.LayerEnabled = false;                              // keep the map's lights, probes and post-processing, not its props or helpers
            }
        }
        else
        {
            LoadProbe(options.EnvironmentMap ?? DefaultEnvironmentMap, scene);
        }

        var modelPath = options.ModelPath.EndsWith("_c", StringComparison.Ordinal) ? options.ModelPath[..^2] : options.ModelPath;
        modelResource = loader.LoadFileCompiled(modelPath)
            ?? throw Domain.Errors.Input("PREVIEW_MODEL_MISSING", $"'{modelPath}_c' is not in the archives.", "Check the model path and the --game/--addon archives.");
        if (modelResource.DataBlock is not Model model)
        {
            throw Domain.Errors.Input("PREVIEW_MODEL_TYPE", $"'{modelPath}_c' is not a model.", "Pass a .vmdl path.");
        }

        node = new ModelSceneNode(scene, model);
        node.SetOverrideLod(0);
        scene.Add(node, true);
        ink = new InkOutline(loader, model, node);
        scene.Initialize();
        text = new TextRenderer(context, renderer.Camera);
        text.Load();

        var state = renderer.Postprocess.State;
        var exposure = state.ExposureSettings;
        exposure.AutoExposureEnabled = false;
        state.ExposureSettings = exposure;
        renderer.Postprocess.State = state;
        Bounds = node.BoundingBox;

        Parts = node.RenderableMeshes
            .SelectMany(mesh => mesh.DrawCalls.Select(draw => new PreviewPart(mesh.Name, draw.Material.Material.Name, draw.Material.ShaderName, draw.IndexCount)))
            .ToArray();
        if (Parts.FirstOrDefault(part => part.Shader == "error") is { } broken)
        {
            throw Domain.Errors.Verification("PREVIEW_MATERIAL_ERROR", $"Material '{broken.Material}' of '{broken.Mesh}' did not load (error shader).", "Add the archive that ships this material.");
        }
    }

    public static string RendererName => $"ValveResourceFormat.Renderer {typeof(VRenderer).Assembly.GetName().Version}; {GLEnvironment.GpuRendererAndDriver}";

    public IReadOnlyList<PreviewPart> Parts { get; }

    /// <summary>Rest-pose bounds of the model (camera framing).</summary>
    public AABB Bounds { get; }

    /// <summary>Length of the active clip in seconds (0 without a clip).</summary>
    public float ClipSeconds { get; private set; }

    public string? Clip { get; private set; }

    /// <summary>Ink outline shell thickness in model units before the material mask (0 = no outline).</summary>
    public float OutlineThickness { get; set; } = DefaultOutlineThickness;

    public const float DefaultOutlineThickness = 0.15f;

    /// <summary>Opens a session; an OpenGL 4.6 context must be current (see <see cref="InitializeGl"/>).</summary>
    public static PreviewSession Open(PreviewSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!glInitialized)
        {
            throw new InvalidOperationException("Call PreviewSession.InitializeGl with the host's current context first.");
        }

        return new PreviewSession(options);
    }

    /// <summary>Selects a clip (.vnmclip path in the archives, or a clip name embedded in the model); paused at time 0.</summary>
    public void SetClip(string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);
        var path = clipPath.EndsWith("_c", StringComparison.Ordinal) ? clipPath[..^2] : clipPath;
        string? key = null;
        if (path.EndsWith(".vnmclip", StringComparison.OrdinalIgnoreCase))
        {
            if (!node.LoadAnimationClip(path))
            {
                throw Domain.Errors.Input("PREVIEW_CLIP_MISSING", $"Clip '{path}_c' did not load.", "Check the clip path; addon clips must be in an --addon archive.");
            }

            key = node.Animations.Keys.FirstOrDefault(name => name.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? node.Animations.Keys.FirstOrDefault(name => Path.GetFileName(name).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            key = node.Animations.Keys.FirstOrDefault(name => name.Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        if (key is null)
        {
            throw Domain.Errors.Input("PREVIEW_CLIP_MISSING", $"Clip '{clipPath}' is not available for this model.", "Pass a .vnmclip path or an animation name of the model.");
        }

        node.SetAnimationByName(key);
        var animation = node.AnimationController.ActiveAnimation
            ?? throw Domain.Errors.Input("PREVIEW_CLIP_MISSING", $"Clip '{clipPath}' could not be played on this model.", "Pass a clip made for this model's skeleton.");
        ClipSeconds = animation.FrameCount <= 1 || animation.Fps <= 0 ? 0f : (animation.FrameCount - 1) / animation.Fps;
        node.AnimationController.IsPaused = true;
        Clip = key;
        SetTime(0);
    }

    public void SetTime(float seconds)
    {
        time = ClipSeconds <= 0 ? 0 : Math.Clamp(seconds, 0, ClipSeconds);
        node.AnimationController.Time = time;
    }

    /// <summary>Renders one frame from the camera and returns top-down RGBA8 pixels.</summary>
    public PreviewImage Capture(PreviewView view, int width, int height, float fieldOfView = 30f)
    {
        RenderFrame(view, width, height, fieldOfView);
        GL.Finish();

        var bytes = new byte[width * height * 4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, display!.FboHandle);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw Domain.Errors.Verification("PREVIEW_GL_ERROR", $"OpenGL reported {error} while reading the frame.", "Update the graphics driver or lower --size.");
        }

        var flipped = new byte[bytes.Length];
        var row = width * 4;
        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(bytes, (height - 1 - y) * row, flipped, y * row, row);
        }

        for (var i = 3; i < flipped.Length; i += 4)
        {
            flipped[i] = 255;
        }

        return new PreviewImage(width, height, flipped);
    }

    /// <summary>
    /// Renders one frame and copies it into <paramref name="targetFramebuffer"/> (a host control's framebuffer, bottom-up
    /// like OpenGL); for a live view. Leaves that framebuffer bound.
    /// </summary>
    public void Present(int targetFramebuffer, PreviewView view, int width, int height, float fieldOfView = 30f, Vector2 pan = default)
    {
        RenderFrame(view, width, height, fieldOfView, pan);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, display!.FboHandle);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFramebuffer);
        GL.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFramebuffer);
    }

    /// <summary>Animation names the model can play (embedded clips and loaded .vnmclip files).</summary>
    public IReadOnlyList<string> Animations => [.. node.Animations.Keys];

    /// <summary>Multiplies part <paramref name="index"/> (order of <see cref="Parts"/>) by an RGBA tint; (1,1,1,1) clears it.</summary>
    public void SetPartTint(int index, Vector4 tint)
    {
        var draw = node.RenderableMeshes.SelectMany(mesh => mesh.DrawCalls).ElementAt(index);
        draw.TintColor = tint;
    }

    private void RenderFrame(PreviewView view, int width, int height, float fieldOfView, Vector2 pan = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        EnsureTargets(width, height);
        var camera = renderer.Camera;
        camera.FieldOfView = fieldOfView;
        camera.SetViewportSize(width, height);
        var center = new Vector3(Bounds.Center.X, Bounds.Center.Y, Bounds.Min.Z + Bounds.Size.Z * view.Height);
        var size = Bounds.Size;
        var vertical = camera.GetFOV();                                   // effective vertical angle (radians) after the viewport is set
        var horizontal = 2 * MathF.Atan(MathF.Tan(vertical / 2) * width / height);
        var fit = 0.72f * MathF.Max(size.Z / MathF.Tan(vertical / 2), MathF.Max(size.X, size.Y) / MathF.Tan(horizontal / 2));
        var yaw = view.YawDegrees * MathF.PI / 180f;
        var pitch = view.PitchDegrees * MathF.PI / 180f;
        var direction = new Vector3(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        var right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitZ));
        var up = Vector3.Cross(right, direction);
        center += (right * pan.X + up * pan.Y) * size.Z;                  // pan in model heights, along the screen axes
        camera.SetLocation(center + direction * fit / view.Zoom);
        camera.LookAt(center);

        var update = new Scene.UpdateContext { Camera = camera, TextRenderer = text, Timestep = 0, Uptime = time };
        renderer.Uptime = time;
        renderer.DeltaTime = 0;
        node.AnimationController.Time = time;
        renderer.Update(update);
        renderer.Render(main!);
        ink.Draw(main!.FboHandle, width, height, camera.ViewProjectionMatrix, OutlineThickness);
        renderer.PostprocessRender(main!, display!, false);
    }

    private void EnsureTargets(int width, int height)
    {
        if (main is not null && main.Width == width && main.Height == height)
        {
            return;
        }

        if (main is not null && display is not null)                   // a live view resizes often: keep the post-processing
        {                                                               // and renderer resources (reloading them took about 1 s)
            main.Resize(width, height);
            display.Resize(width, height);
            return;
        }

        main = Framebuffer.Prepare("S2ModKit preview HDR", width, height, Samples, ImageFormat.RGBA16161616F, ImageFormat.D32);
        main.Initialize();
        renderer.MainFramebuffer = main;
        renderer.Postprocess.Load(Samples);
        renderer.LoadRendererResources();
        display = Framebuffer.Prepare("S2ModKit preview display", width, height, 0, ImageFormat.RGBA8888, null);
        display.Initialize();
    }

    private void LoadProbe(string envPath, Scene scene)
    {
        environment = File.Exists(envPath) ? ReadFile(envPath) : loader.LoadFileCompiled(envPath.EndsWith("_c", StringComparison.Ordinal) ? envPath[..^2] : envPath);
        if (environment is null)
        {
            throw Domain.Errors.Input("PREVIEW_ENVMAP_MISSING", $"Light probe '{envPath}' was not found.", "Pass --envmap with a .vtex_c file or a texture path inside the archives.");
        }

        VRenderer.LoadDefaultLighting(scene, environment);
    }

    private static Resource ReadFile(string path)
    {
        var resource = new Resource { FileName = Path.GetFullPath(path) };
        resource.Read(resource.FileName);
        return resource;
    }

    public void Dispose()
    {
        ink.Dispose();
        main?.Delete();
        display?.Delete();
        modelResource.Dispose();
        environment?.Dispose();
        context.Dispose();
        loader.Dispose();
        foreach (var package in packages)
        {
            package.Dispose();
        }
    }
}

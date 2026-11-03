using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL;
using S2ModKit.Adapters.Preview;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>A camera preset: yaw and pitch in degrees, zoom, and the height (0 feet - 1 head) the camera looks at.</summary>
internal sealed record CameraPreset(string Name, float Yaw, float Pitch, float Zoom, float Height);

/// <summary>
/// A live preview: Avalonia's OpenGL control hosts one <see cref="PreviewSession"/> at a time. <see cref="Open"/> swaps
/// the session (another mod, vanilla) and keeps the camera, the clip and the time. The host forwards pointer input:
/// left drag orbits, right or middle drag pans, the wheel zooms. Events are raised on the UI thread.
/// </summary>
internal sealed class PreviewControl : OpenGlControlBase
{
    public static readonly CameraPreset[] Presets =
    [
        new("Front", 0, 5, 1.3f, 0.5f), new("Side", 90, 5, 1.3f, 0.5f), new("Back", 180, 5, 1.3f, 0.5f),
        new("Below", 0, -55, 1.3f, 0.45f), new("Close-up", 15, 5, 3.2f, 0.75f),
    ];

    private readonly Stopwatch clock = new();
    private PreviewSession? session;
    private PreviewSessionOptions? pending;
    private bool loadingFrameShown, closeRequested, clipChanged, panning;
    private Point? drag;
    private float yaw, pitch = 5, zoom = 1.3f, height = 0.5f, time;
    private Vector2 pan;
    private string? clip;
    private double lastFrame, lastProgress, fpsWindowStart;
    private int frames, highlighted = -1;

    /// <summary>A session opened (parts, animations, open time) or failed (message).</summary>
    public event Action<PreviewSession?, TimeSpan, string?>? Opened;

    /// <summary>Clip time and length, about ten times a second.</summary>
    public event Action<float, float>? Progress;

    /// <summary>Frame rate about once a second, with the frame size.</summary>
    public event Action<double, int, int>? FrameStats;

    public bool Playing { get; set; } = true;

    public string? GlInfo { get; private set; }

    /// <summary>Whether Avalonia calls the OpenGL callbacks on the UI thread (a load then blocks the window).</summary>
    public bool? RendersOnUiThread { get; private set; }

    public int Highlight { get; set; } = -1;

    /// <summary>Emulated ink outline thickness (model units before the material mask); 0 = off.</summary>
    public float Outline { get; set; } = PreviewSession.DefaultOutlineThickness;

    public string? ClipName => clip;

    /// <summary>Render size in pixels instead of the control's size (measurements; the frame is clipped to the control).</summary>
    public (int Width, int Height)? ForcedSize { get; set; }

    /// <summary>Called on the render thread after each presented frame (self-test).</summary>
    public Action<PreviewSession, PreviewView, int, int, double>? AfterFrame { get; set; }

    /// <summary>Opens (or swaps to) a session on the next frame; the camera, clip and time are kept where possible.</summary>
    public void Open(PreviewSessionOptions options)
    {
        pending = options;
        loadingFrameShown = false;
        RequestNextFrameRendering();
    }

    /// <summary>Disposes the session on the next frame (releasing its archive files) and completes after that.</summary>
    public Task CloseAsync()
    {
        if (session is null)                                           // nothing open (the control may not even be shown)
        {
            pending = null;
            return Task.CompletedTask;
        }

        pending = null;
        closeRequested = true;
        closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestNextFrameRendering();
        return closed.Task;
    }

    private TaskCompletionSource? closed;

    public void BeginDrag(Point at, bool pan)
    {
        drag = at;
        panning = pan;
    }

    public void DragTo(Point to, double viewHeight)
    {
        if (drag is not { } from)
        {
            return;
        }

        var (dx, dy) = ((float)(to.X - from.X), (float)(to.Y - from.Y));
        if (panning)
        {
            var perPixel = 1.2f / (float)Math.Max(viewHeight, 1) / zoom;
            pan += new Vector2(dx * perPixel, dy * perPixel);              // the character follows the pointer
        }
        else
        {
            yaw -= dx * 0.4f;
            pitch = Math.Clamp(pitch + dy * 0.3f, -85f, 85f);
        }

        drag = to;
    }

    public void EndDrag() => drag = null;

    public void Zoom(double wheel) => zoom = Math.Clamp(zoom * (wheel > 0 ? 1.12f : 1 / 1.12f), 0.3f, 12f);

    public void SetClip(string name)
    {
        clip = name;
        clipChanged = true;
        time = 0;
    }

    public void Seek(float seconds)
    {
        Playing = false;
        time = seconds;
    }

    public void SetCamera(CameraPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        (yaw, pitch, zoom, height, pan) = (preset.Yaw, preset.Pitch, preset.Zoom, preset.Height, Vector2.Zero);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        GlInfo = $"OpenGL {GlVersion.Major}.{GlVersion.Minor}";
        PreviewSession.InitializeGl(new AvaloniaBindings(gl));
        clock.Start();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        session?.Dispose();
        session = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        RendersOnUiThread ??= Dispatcher.UIThread.CheckAccess();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = ForcedSize?.Width ?? Math.Max(1, (int)(Bounds.Width * scale));
        var heightPx = ForcedSize?.Height ?? Math.Max(1, (int)(Bounds.Height * scale));
        if (closeRequested)
        {
            closeRequested = false;
            session?.Dispose();
            session = null;
            closed?.TrySetResult();
        }

        if (pending is not null && !loadingFrameShown)
        {
            loadingFrameShown = true;                                  // let the window show "loading" before the load
            ClearTarget(fb);
            RequestNextFrameRendering();
            return;
        }

        if (pending is { } options)
        {
            pending = null;
            OpenNow(options);
        }

        if (session is null)
        {
            ClearTarget(fb);
            return;
        }

        var now = clock.Elapsed.TotalSeconds;
        var delta = (float)Math.Min(now - lastFrame, 0.25);
        lastFrame = now;
        if (clipChanged && clip is not null)
        {
            clipChanged = false;
            TrySetClip(clip);
        }

        if (Playing && session.ClipSeconds > 0)
        {
            time = (time + delta) % session.ClipSeconds;
        }

        session.SetTime(time);
        session.OutlineThickness = Outline;
        ApplyHighlight(now);
        var view = new PreviewView("live", yaw, pitch, zoom, height);
        session.Present(fb, view, width, heightPx, pan: pan);
        AfterFrame?.Invoke(session, view, width, heightPx, now);

        frames++;
        if (now - fpsWindowStart >= 1.0)
        {
            var fps = frames / (now - fpsWindowStart);
            Post(() => FrameStats?.Invoke(fps, width, heightPx));
            frames = 0;
            fpsWindowStart = now;
        }

        if (now - lastProgress >= 0.1)
        {
            lastProgress = now;
            var (t, length) = (time, session.ClipSeconds);
            Post(() => Progress?.Invoke(t, length));
        }

        RequestNextFrameRendering();
    }

    private void OpenNow(PreviewSessionOptions options)
    {
        var started = Stopwatch.StartNew();
        session?.Dispose();
        session = null;
        highlighted = -1;
        try
        {
            session = PreviewSession.Open(options);
            if (clip is null || !TrySetClip(clip))
            {
                clip = DefaultClip(session.Animations);
                if (clip is not null)
                {
                    TrySetClip(clip);
                }
            }

            var opened = session;
            Post(() => Opened?.Invoke(opened, started.Elapsed, null));
        }
#pragma warning disable CA1031 // shown in the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            session?.Dispose();
            session = null;
            Post(() => Opened?.Invoke(null, started.Elapsed, exception.Message));
        }
    }

    private bool TrySetClip(string name)
    {
        if (session is null || !session.Animations.Contains(name))
        {
            return false;
        }

        var keep = time;
        session.SetClip(name);
        time = Math.Min(keep, session.ClipSeconds);
        return true;
    }

    /// <summary>A standing idle when the model has one: names with "stand" and "idle" first, then any "idle".</summary>
    public static string? DefaultClip(IReadOnlyList<string> animations) =>
        animations.Where(name => name.Contains("idle", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name.Contains("stand", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name.Contains("out_of_combat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name.Length).FirstOrDefault() ?? (animations.Count > 0 ? animations[0] : null);

    private void ApplyHighlight(double now)
    {
        if (session is null)
        {
            return;
        }

        if (Highlight != highlighted)
        {
            if (highlighted >= 0 && highlighted < session.Parts.Count)
            {
                session.SetPartTint(highlighted, Vector4.One);
            }

            highlighted = Highlight;
        }

        if (highlighted >= 0 && highlighted < session.Parts.Count)
        {
            var k = 0.5f + 0.5f * MathF.Sin((float)now * 6f);
            session.SetPartTint(highlighted, new Vector4(1f + k, 1f + 0.6f * k, 0.4f, 1f));
        }
    }

    private static void ClearTarget(int fb)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
        GL.ClearColor(0.12f, 0.13f, 0.15f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private sealed class AvaloniaBindings(GlInterface gl) : OpenTK.IBindingsContext
    {
        public IntPtr GetProcAddress(string procName) => gl.GetProcAddress(procName);
    }
}

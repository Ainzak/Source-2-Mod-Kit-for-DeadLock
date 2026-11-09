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
    private double nextDue;

    /// <summary>Frames per second the preview draws at most (a smooth preview needs no more; drawing faster only uses power).</summary>
    public int MaxFps { get; set; } = 60;

    /// <summary>Frames drawn with an open session since the control was created (checks).</summary>
    public long FramesDrawn { get; private set; }
    private PreviewSession? session;
    private PreviewSessionOptions? pending;
    private bool loadingFrameShown, closeRequested, clipChanged, panning;
    private Point? drag;
    private float yaw, pitch = 5, zoom = 1.3f, height = 0.5f, time;
    private Vector2 pan;
    private string? clip;
    private string? returnClip;                                        // after a clip played once, the clip shown before it
    private readonly Queue<string> nextClips = new();                  // the phases still to play before going back
    private bool sequence;                                             // an ability's phases are playing once
    private double lastFrame, lastProgress, fpsWindowStart;
    private int frames;

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

    /// <summary>Parts (indices into the session's parts) to hide; cleared when another model opens.</summary>
    public HashSet<int> Hidden { get; } = [];

    private (Point At, Action<int?> Done)? pick;
    private readonly Dictionary<int, S2ModKit.Application.ColorAdjustment?> colorRequests = [];

    /// <summary>Recolors a part in the preview at the next frame (null: original colours).</summary>
    public void RequestPartColor(int part, S2ModKit.Application.ColorAdjustment? change) => colorRequests[part] = change;

    private readonly HashSet<int> colorPrepare = [];

    /// <summary>Starts unpacking a part's colour texture before its first recolor (when the part is picked in the Colour tab).</summary>
    public void PrepareColor(int part)
    {
        colorPrepare.Add(part);
        RequestNextFrameRendering();
    }

    private readonly Dictionary<int, (S2ModKit.Application.RgbColor? Target, float Tolerance)> rangeRequests = [];
    private readonly Dictionary<int, (S2ModKit.Application.PartShape? Shape, Action<string?>? Done)> shapeRequests = [];

    /// <summary>Reshapes a part in the preview at the next frame (null: its compiled shape); <paramref name="done"/> gets the refusal, if any.</summary>
    public void RequestPartShape(int part, S2ModKit.Application.PartShape? shape, Action<string?>? done = null) => shapeRequests[part] = (shape, done);

    /// <summary>Parts reshaped in the preview, with their change.</summary>
    public Dictionary<int, S2ModKit.Application.PartShape> PartShapes { get; } = [];
    private (Point At, Action<(int Part, S2ModKit.Application.RgbColor Color)?> Done)? sample;

    /// <summary>Limits a part's recolor to colours near <paramref name="target"/> at the next frame (null: the whole part).</summary>
    public void RequestColorRange(int part, S2ModKit.Application.RgbColor? target, float tolerance) => rangeRequests[part] = (target, tolerance);

    /// <summary>Samples the part and its original texture colour under <paramref name="at"/> after the next frame.</summary>
    public void RequestSample(Point at, Action<(int Part, S2ModKit.Application.RgbColor Color)?> done) => sample = (at, done);

    /// <summary>Parts recolored in the preview, with their change.</summary>
    public Dictionary<int, S2ModKit.Application.ColorAdjustment> PartColors { get; } = [];

    /// <summary>The open session (UI thread: the app renders on it).</summary>
    public PreviewSession? Session => session;

    /// <summary>Finds the visible part under <paramref name="at"/> (control coordinates) after the next frame.</summary>
    public void RequestPick(Point at, Action<int?> done) => pick = (at, done);

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
        Hidden.Clear();
        PartColors.Clear();
        colorRequests.Clear();
        rangeRequests.Clear();
        shapeRequests.Clear();
        PartShapes.Clear();
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
        returnClip = null;
        nextClips.Clear();
        sequence = false;
        clip = name;
        clipChanged = true;
        time = 0;
    }

    /// <summary>Plays <paramref name="name"/> once from the start, then goes back to the clip shown before it (an ability, then idle).</summary>
    public void PlayOnce(string name) => PlaySequence([name]);

    /// <summary>Plays <paramref name="names"/> once each, in order (an ability's phases), then goes back to the clip shown before.</summary>
    public void PlaySequence(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return;
        }

        var back = returnClip ?? clip;
        nextClips.Clear();
        foreach (var next in names.Skip(1))
        {
            nextClips.Enqueue(next);
        }

        clip = names[0];
        clipChanged = true;
        time = 0;
        Playing = true;
        returnClip = back is not null && !names.Contains(back) ? back : null;
        sequence = true;
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
            TrySetClip(clip, abilityClip: sequence);
        }

        if (Playing && session.ClipSeconds > 0)
        {
            if (sequence && time + delta >= session.ClipSeconds)
            {
                time = 0;
                var advanced = false;
                while (!advanced && nextClips.TryDequeue(out var next))        // the next phase (a clip that does not load is skipped)
                {
                    advanced = TrySetClip(next, abilityClip: true);
                    clip = advanced ? next : clip;
                }

                if (!advanced)
                {
                    sequence = false;                                          // every phase played once: back to the clip before
                    if (returnClip is not null)
                    {
                        clip = returnClip;
                        TrySetClip(clip);
                    }

                    returnClip = null;
                }
            }
            else
            {
                time = (time + delta) % session.ClipSeconds;
            }
        }

        session.SetTime(time);
        session.OutlineThickness = Outline;
        ApplyHighlight(now);
        var view = new PreviewView("live", yaw, pitch, zoom, height);
        foreach (var index in Enumerable.Range(0, session.Parts.Count))
        {
            if (Hidden.Contains(index) != session.HiddenParts.Contains(index))
            {
                session.SetPartHidden(index, Hidden.Contains(index));
            }
        }

        foreach (var part in colorPrepare.Where(part => part >= 0 && part < session.Parts.Count))
        {
            session.PrepareColor(part);                                        // unpacks in the background: the window keeps drawing
        }

        colorPrepare.Clear();
        var waiting = false;
        foreach (var (part, range) in rangeRequests.ToArray())
        {
            if (part >= 0 && part < session.Parts.Count && !session.ColorReady(part))
            {
                session.PrepareColor(part);
                waiting = true;                                                // applied on a later frame, once unpacked
                continue;
            }

            if (part >= 0 && part < session.Parts.Count)
            {
                session.SetPartColorRange(part, range.Target, range.Tolerance);
            }

            rangeRequests.Remove(part);
        }

        foreach (var (part, change) in colorRequests.ToArray())
        {
            if (part >= 0 && part < session.Parts.Count && !session.ColorReady(part))
            {
                session.PrepareColor(part);
                waiting = true;
                continue;
            }

            if (part >= 0 && part < session.Parts.Count)
            {
                session.SetPartColor(part, change);
            }

            colorRequests.Remove(part);
        }

        if (waiting)
        {
            RequestNextFrameRendering();
        }
        foreach (var (part, shapeRequest) in shapeRequests)
        {
            var refusal = part >= 0 && part < session.Parts.Count ? session.SetPartShape(part, shapeRequest.Shape) : "No such part.";
            shapeRequest.Done?.Invoke(refusal);
        }

        shapeRequests.Clear();
        session.Present(fb, view, width, heightPx, pan: pan);
        AfterFrame?.Invoke(session, view, width, heightPx, now);
        if (sample is { } sampling)
        {
            sample = null;
            var sampled = session.SamplePartColor((float)(sampling.At.X * scale), (float)(sampling.At.Y * scale), width, heightPx);
            Post(() => sampling.Done(sampled));
        }

        if (pick is { } request)
        {
            pick = null;
            var picked = session.PickPart((float)(request.At.X * scale), (float)(request.At.Y * scale), width, heightPx);
            Post(() => request.Done(picked));
        }

        frames++;
        FramesDrawn++;
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

        RequestPacedFrame(now);
    }

    /// <summary>Starts drawing again after the control was hidden (no frame is requested while it is not shown).</summary>
    public void Resume()
    {
        nextDue = 0;
        RequestNextFrameRendering();
    }

    /// <summary>Asks for the next frame at the paced rate: the due time advances by one interval per frame, so timer
    /// granularity (about 15 ms on Windows) evens out instead of lowering the rate.</summary>
    private void RequestPacedFrame(double now)
    {
        var interval = 1.0 / Math.Clamp(MaxFps, 10, 240);
        nextDue = Math.Max(nextDue + interval, now);                       // after a stall, start counting again from now
        var wait = nextDue - clock.Elapsed.TotalSeconds;
        if (wait <= 0.002)
        {
            RequestNextFrameRendering();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(true);
            RequestNextFrameRendering();
        });
    }

    private void OpenNow(PreviewSessionOptions options)
    {
        var started = Stopwatch.StartNew();
        session?.Dispose();
        session = null;
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

    /// <summary>Selects a clip the model lists. Only an ability's phases (<paramref name="abilityClip"/>) may load a clip file the
    /// model does not list; a clip kept from the previous model (another hero's idle) never does: it would bend this skeleton.</summary>
    private bool TrySetClip(string name, bool abilityClip = false)
    {
        var file = abilityClip && name.EndsWith(".vnmclip", StringComparison.OrdinalIgnoreCase);   // an ability's clip file loads on demand
        if (session is null || (!file && !session.Animations.Contains(name)))
        {
            return false;
        }

        var keep = time;
        try
        {
            session.SetClip(name);
        }
        catch (S2ModKit.Domain.S2ModKitException) when (file)
        {
            return false;                                                    // not loadable on this model: the clip shown stays
        }

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
        if (session is not null)
        {
            session.HighlightPart = Highlight;                               // drawn as an overlay: works for every material
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

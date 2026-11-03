using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using ValveResourceFormat.Renderer;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// A hidden window whose OpenGL 4.6 core context is current on the creating thread; for command-line renders. A desktop
/// host supplies its own context instead (for example a WPF OpenGL control) and creates <see cref="PreviewSession"/>
/// while that context is current.
/// </summary>
public sealed class OffscreenGlContext : IDisposable
{
    private readonly NativeWindow window;

    public OffscreenGlContext()
    {
        GLFWProvider.CheckForMainThread = false;    // the batch renderer owns its context on a worker thread (Windows)
        window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            ClientSize = new(64, 64),
            Title = "S2ModKit preview",
            API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 6),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.Offscreen,
        });
        window.MakeCurrent();
        PreviewSession.InitializeGl(new GLFWBindingsContext());
    }

    public void Dispose() => window.Dispose();
}

public sealed partial class PreviewSession
{
    private static bool glInitialized;

    /// <summary>Loads OpenGL entry points and the renderer's global GL state once per process (current context required).</summary>
    public static void InitializeGl(OpenTK.IBindingsContext bindings)
    {
        if (glInitialized)
        {
            return;
        }

        GL.LoadBindings(bindings);
        GLEnvironment.Initialize(NullLogger.Instance);
        glInitialized = true;
    }
}

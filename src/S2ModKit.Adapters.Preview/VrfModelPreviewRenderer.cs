using S2ModKit.Application;

namespace S2ModKit.Adapters.Preview;

/// <summary>Batch renders through a hidden OpenGL window on a dedicated thread (the context belongs to that thread).</summary>
public sealed class VrfModelPreviewRenderer : IModelPreviewRenderer
{
    public RenderedPreview Render(PreviewRenderSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        RenderedPreview? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = RenderOnThread(spec, cancellationToken);
            }
#pragma warning disable CA1031 // rethrown on the caller's thread
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    private static RenderedPreview RenderOnThread(PreviewRenderSpec spec, CancellationToken cancellationToken)
    {
        using var gl = new OffscreenGlContext();
        using var session = PreviewSession.Open(new PreviewSessionOptions(spec.Archives, spec.ModelPath, spec.EnvironmentMap, spec.SceneMap));
        session.OutlineThickness = spec.OutlineThickness;
        if (spec.ClipPath is not null)
        {
            session.SetClip(spec.ClipPath);
        }

        var count = spec.Frames > 0 ? spec.Frames : Math.Max(1, (int)MathF.Round(session.ClipSeconds * spec.FramesPerSecond));
        var times = Enumerable.Range(0, Math.Min(count, ModelPreviewApplication.MaxFrames))
            .Select(i => session.ClipSeconds <= 0 ? 0f : i / (float)spec.FramesPerSecond % session.ClipSeconds)
            .ToArray();
        var frames = spec.Views.Select(_ => new List<PreviewImage>(times.Length)).ToArray();
        foreach (var t in times)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.SetTime(t);
            for (var v = 0; v < spec.Views.Count; v++)
            {
                frames[v].Add(session.Capture(spec.Views[v], spec.Width, spec.Height));
            }
        }

        return new RenderedPreview(frames, times, session.ClipSeconds, PreviewSession.RendererName, session.Parts);
    }
}

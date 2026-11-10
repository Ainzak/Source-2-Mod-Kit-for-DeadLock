using System.Diagnostics;
using System.Numerics;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Effect preview: one particle system plays alone at the middle of the model's height (where the camera looks) (the model is hidden meanwhile) and restarts every
/// <see cref="EffectLoopSeconds"/>. An overlay archive (recolored effect files) is searched before every other archive, so the effect
/// and its child effects show the new colours. VRF's renderer cannot draw every parameter form the game uses; such an effect is
/// stopped and its error reported, and the window falls back to colour swatches.
/// </summary>
public sealed partial class PreviewSession
{
    public const float EffectLoopSeconds = 3.5f;
    private readonly Stopwatch effectClock = Stopwatch.StartNew();
    private ParticleSceneNode? effect;
    private Resource? effectResource;
    private Package? overlay;
    private MemoryStream? overlayStream;
    private double effectStarted;
    private double effectTick;

    /// <summary>The effect playing (null: none).</summary>
    public string? Effect { get; private set; }

    /// <summary>Why the last effect stopped by itself (it could not be drawn), or null.</summary>
    public string? EffectError { get; private set; }

    /// <summary>Puts an archive in front of every other one (null: none); for recolored effect files.</summary>
    public void SetOverlay(byte[]? vpk)
    {
        loader.CurrentPackage = null;
        overlay?.Dispose();
        overlayStream?.Dispose();
        (overlay, overlayStream) = (null, null);
        if (vpk is null)
        {
            return;
        }

        overlayStream = new MemoryStream(vpk, writable: false);
        overlay = new Package();
        overlay.SetFileName("s2modkit-effect-preview_dir.vpk");
        overlay.Read(overlayStream);
        loader.CurrentPackage = overlay;
    }

    /// <summary>Plays effect <paramref name="path"/> (a <c>.vpcf</c> path without <c>_c</c>) alone, looping; null stops it and shows the
    /// model again. Returns why it cannot play, or null.</summary>
    public string? ShowEffect(string? path)
    {
        StopEffect();
        EffectError = null;
        if (path is null)
        {
            return null;
        }

        try
        {
            effectResource = loader.LoadFileCompiled(path.EndsWith("_c", StringComparison.Ordinal) ? path[..^2] : path);
            if (effectResource?.DataBlock is not ParticleSystem system)
            {
                StopEffect();
                return $"'{path}' is not in the archives or is not an effect.";
            }

            effect = new ParticleSceneNode(renderer.Scene, system, null!, false)
            {
                Transform = Matrix4x4.CreateTranslation(Bounds.Center.X, Bounds.Center.Y, Bounds.Min.Z + (Bounds.Size.Z * 0.45f)),   // where the camera looks
            };
            renderer.Scene.Add(effect, true);
            node.LayerEnabled = false;                                       // the effect alone
            Effect = path;
            effectStarted = effectTick = effectClock.Elapsed.TotalSeconds;
            return null;
        }
#pragma warning disable CA1031 // the renderer's particle parser throws many kinds; reported to the window
        catch (Exception exception)
#pragma warning restore CA1031
        {
            StopEffect();
            return $"The preview cannot draw this effect ({exception.Message}).";
        }
    }

    /// <summary>Advances the playing effect (called before each frame); restarts it at the end of the loop.</summary>
    private void AdvanceEffect(Camera camera)
    {
        if (effect is null)
        {
            return;
        }

        var now = effectClock.Elapsed.TotalSeconds;
        var step = (float)Math.Min(now - effectTick, 0.1);
        effectTick = now;
        try
        {
            if (now - effectStarted > EffectLoopSeconds)
            {
                effectStarted = now;
                effect.Restart();
            }

            effect.Update(new Scene.UpdateContext { Camera = camera, TextRenderer = text, Timestep = step, Uptime = (float)now });
        }
#pragma warning disable CA1031 // see ShowEffect
        catch (Exception exception)
#pragma warning restore CA1031
        {
            EffectError = $"The preview cannot draw this effect ({exception.Message}).";
            StopEffect();
        }
    }

    private void StopEffect()
    {
        if (effect is not null)
        {
            renderer.Scene.Remove(effect, true);
            effect.Delete();
        }

        effect = null;
        effectResource?.Dispose();
        effectResource = null;
        Effect = null;
        node.LayerEnabled = true;
    }
}

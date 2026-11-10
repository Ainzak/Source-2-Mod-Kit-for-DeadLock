using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>An effect placed on the model: the effect file (<c>.vpcf</c>), the bone it follows (null: the model's origin, "follows
/// the hero") and its place in that bone's space (as the built mod's attachment has it).</summary>
public sealed record PlacedEffect(string Effect, string? Bone, Vector3 Offset);

/// <summary>
/// Added effects in the preview: each placed effect plays with the model, at its bone in the current pose, so it follows every animation as an attachment does in game. Effects play once from the
/// moment they are placed, as the game starts an always-on effect once when the hero appears; an effect the renderer cannot
/// draw is dropped and reported through <see cref="PlacedErrors"/>.
/// </summary>
public sealed partial class PreviewSession
{
    private readonly List<(PlacedEffect Placed, ParticleSceneNode? Node, Resource? Resource, int Bone, string? Refusal)> placed = [];
    private readonly List<string> placedErrors = [];
    private double placedTick;

    /// <summary>The placed effect whose point is marked (-1: none).</summary>
    public int MarkedPlaced { get; set; } = -1;

    /// <summary>Why placed effects were dropped since the last call (each reason once).</summary>
    public IReadOnlyList<string> TakePlacedErrors()
    {
        var errors = placedErrors.ToArray();
        placedErrors.Clear();
        return errors;
    }

    /// <summary>The bone under pixel (<paramref name="x"/>, <paramref name="y"/>) of the last frame and the hit point in that bone's
    /// space; null when the point hits nothing.</summary>
    public (string Bone, Vector3 Offset)? PickPoint(float x, float y, int width, int height)
    {
        if (picker.Count != Parts.Count || Ray(x, y, width, height) is not { } ray)
        {
            return null;
        }

        var skin = Skin();
        if (picker.PickPoint(ray.Origin, ray.Direction, skin, node.Transform, hiddenIndexCounts.Keys.ToHashSet()) is not { } hit
            || hit.Bone >= node.AnimationController.Skeleton.Bones.Length || !Matrix4x4.Invert(BoneWorld(hit.Bone, skin), out var inverse))
        {
            return null;
        }

        return (node.AnimationController.Skeleton.Bones[hit.Bone].Name, Vector3.Transform(hit.Point, inverse));
    }

    /// <summary>
    /// Shows exactly <paramref name="effects"/> (an effect already playing at the same position in the list keeps playing; only
    /// its point moves). Returns why each cannot play, or null.
    /// </summary>
    public IReadOnlyList<string?> SetPlacedEffects(IReadOnlyList<PlacedEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var refusals = new string?[effects.Count];
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            if (i >= effects.Count || !string.Equals(effects[i].Effect, placed[i].Placed.Effect, StringComparison.OrdinalIgnoreCase))
            {
                RemovePlaced(i);                                             // this and every later slot is loaded again below
            }
        }

        var bones = node.AnimationController.Skeleton.Bones;
        for (var i = 0; i < effects.Count; i++)
        {
            var bone = effects[i].Bone is { } name ? Array.FindIndex(bones, item => item.Name == name) : -1;
            if (i < placed.Count)
            {
                placed[i] = (effects[i], placed[i].Node, placed[i].Resource, bone, placed[i].Refusal);
                refusals[i] = placed[i].Refusal;
                continue;
            }

            ParticleSceneNode? particles = null;
            Resource? resource = null;
            if (effects[i].Bone is not null && bone < 0)
            {
                refusals[i] = $"This model has no bone '{effects[i].Bone}'.";
            }
            else
            {
                try
                {
                    resource = loader.LoadFileCompiled(effects[i].Effect.EndsWith("_c", StringComparison.Ordinal) ? effects[i].Effect[..^2] : effects[i].Effect);
                    if (resource?.DataBlock is ParticleSystem system)
                    {
                        particles = new ParticleSceneNode(renderer.Scene, system, null!, false);
                        renderer.Scene.Add(particles, true);
                    }
                    else
                    {
                        refusals[i] = $"'{effects[i].Effect}' is not in the archives or is not an effect.";
                    }
                }
#pragma warning disable CA1031 // the renderer's particle parser throws many kinds; reported to the window
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    refusals[i] = $"The preview cannot draw this effect ({exception.Message}).";
                }
            }

            if (particles is null)
            {
                resource?.Dispose();
                resource = null;
            }

            placed.Add((effects[i], particles, resource, bone, refusals[i]));
        }

        placedTick = effectClock.Elapsed.TotalSeconds;
        return refusals;
    }

    /// <summary>Moves every placed effect to its bone in the current pose and advances it (before each frame).</summary>
    private void AdvancePlaced(Camera camera)
    {
        if (placed.Count == 0)
        {
            return;
        }

        var now = effectClock.Elapsed.TotalSeconds;
        var step = (float)Math.Clamp(now - placedTick, 0, 0.1);
        placedTick = now;
        var skin = Skin();
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            var item = placed[i];
            if (item.Node is null)
            {
                continue;
            }

            item.Node.LayerEnabled = effect is null;                         // hidden while one effect plays alone
            try
            {
                // As in game, the effect's control points take the bone's orientation, not only its place: many effects shift
                // or aim their particles in the control point's own axes (a fuse flame 40 units along -Y, for example).
                var point = PlacedPoint(item.Placed, item.Bone, skin);
                var frame = item.Bone < 0 ? node.Transform : BoneWorld(item.Bone, skin);
                var rotation = Matrix4x4.Decompose(frame, out _, out var turned, out _) ? Quaternion.Normalize(turned) : Quaternion.Identity;
                item.Node.Transform = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(point);
                var second = item.Node.GetControlPoint(1);                    // the built wrapper puts control points 0 and 1 at the point
                second.Position = point;
                second.Rotation = rotation;
                item.Node.Update(new Scene.UpdateContext { Camera = camera, TextRenderer = text, Timestep = step, Uptime = (float)now });
            }
#pragma warning disable CA1031 // see SetPlacedEffects
            catch (Exception exception)
#pragma warning restore CA1031
            {
                DropPlaced(i, $"The preview cannot draw this effect ({exception.Message}).");
            }
        }
    }

    /// <summary>The bone's matrix in the current pose (model space, then the model's place in the scene).</summary>
    private Matrix4x4 BoneWorld(int bone, Matrix4x4[] skin)
    {
        var pose = node.AnimationController.Pose;
        if (bone < pose.Length)
        {
            return pose[bone] * node.Transform;
        }

        Matrix4x4.Invert(node.AnimationController.Skeleton.Bones[bone].InverseBindPose, out var bind);
        return bind * (bone < skin.Length ? skin[bone] : Matrix4x4.Identity) * node.Transform;
    }

    private Vector3 PlacedPoint(PlacedEffect effect, int bone, Matrix4x4[] skin) =>
        bone < 0 ? Vector3.Transform(effect.Offset, node.Transform) : Vector3.Transform(effect.Offset, BoneWorld(bone, skin));

    /// <summary>Stops the last playing placed effect at or before <paramref name="index"/> (its slot stays, with the reason).</summary>
    private void DropPlaced(int index, string reason)
    {
        index = placed.FindLastIndex(Math.Min(index, placed.Count - 1), item => item.Node is not null);
        if (index < 0)
        {
            return;
        }

        var item = placed[index];
        placedErrors.Add($"{Path.GetFileName(item.Placed.Effect)}: {reason}");
        Release(item.Node, item.Resource);
        placed[index] = (item.Placed, null, null, item.Bone, reason);
    }

    private void RemovePlaced(int index)
    {
        Release(placed[index].Node, placed[index].Resource);
        placed.RemoveAt(index);
    }

    private void Release(ParticleSceneNode? particles, Resource? resource)
    {
        if (particles is not null)
        {
            renderer.Scene.Remove(particles, true);
            particles.Delete();
        }

        resource?.Dispose();
    }

    private void ClearPlaced()
    {
        for (var i = placed.Count - 1; i >= 0; i--)
        {
            RemovePlaced(i);
        }
    }

    /// <summary>A small pulsing diamond at the marked effect's point, drawn over the model.</summary>
    private void DrawPlacedMarker(int framebuffer, int width, int height, Matrix4x4 viewProjection)
    {
        if (MarkedPlaced < 0 || MarkedPlaced >= placed.Count)
        {
            return;
        }

        var centre = PlacedPoint(placed[MarkedPlaced].Placed, placed[MarkedPlaced].Bone, Skin());
        var r = Math.Max(1f, Bounds.Size.Z * 0.02f);
        Vector3[] tips = [centre + new Vector3(r, 0, 0), centre - new Vector3(r, 0, 0), centre + new Vector3(0, r, 0), centre - new Vector3(0, r, 0), centre + new Vector3(0, 0, r), centre - new Vector3(0, 0, r)];
        var triangles = new List<Vector3>();
        foreach (var x in new[] { 0, 1 })
        {
            foreach (var y in new[] { 2, 3 })
            {
                foreach (var z in new[] { 4, 5 })
                {
                    for (var pass = 0; pass < 1; pass++)
                    {
                        triangles.AddRange([tips[x], tips[y], tips[z]]);
                    }
                }
            }
        }

        highlight.Draw(framebuffer, width, height, viewProjection, [.. triangles], [], 0.5f + (0.5f * MathF.Sin(Environment.TickCount64 / 160f)));
    }
}

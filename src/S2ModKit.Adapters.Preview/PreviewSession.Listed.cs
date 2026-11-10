using System.Globalization;
using System.Numerics;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// The always-on effects a mod built by the app already has: the model lists wrappers under <c>particles/s2modkit/</c>, each with
/// one child effect and, when placed, an attachment on a bone. They are read back as <see cref="PlacedEffect"/>s so the preview
/// plays them like newly placed ones. The game's own listed effects (a hero's horn or book glow) are not played.
/// </summary>
public sealed partial class PreviewSession
{
    private const string AddedFolder = "particles/s2modkit/";

    /// <summary>The added effects the opened model already has, in the model's order (empty for other models).</summary>
    public IReadOnlyList<PlacedEffect> ListedEffects { get; private set; } = [];

    private void ReadListedEffects(Model model)
    {
        try
        {
            var text = model.Data["m_modelInfo"]["m_keyValueText"].ToString(CultureInfo.InvariantCulture);
            var attachments = Attachments(model);
            var found = new List<PlacedEffect>();
            const string Marker = "name = resource:\"";
            for (var at = text.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(Marker, at + 1, StringComparison.Ordinal))
            {
                var from = at + Marker.Length;
                var path = text[from..text.IndexOf('"', from)];
                if (!path.StartsWith(AddedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var wrapper = loader.LoadFileCompiled(path);
                if (wrapper?.DataBlock is not ParticleSystem system || !system.Data.TryGetValue("m_Children", out var children) || !children.IsArray || children.Count == 0)
                {
                    continue;
                }

                var child = children[0]["m_ChildRef"].ToString(CultureInfo.InvariantCulture);
                var attachment = system.Data.TryGetValue("m_controlPointConfigurations", out var configurations) && configurations.IsArray
                    ? configurations.Values.SelectMany(configuration => configuration["m_drivers"].Values)
                        .Select(driver => driver.TryGetValue("m_attachmentName", out var name) ? name.ToString(CultureInfo.InvariantCulture) : null).FirstOrDefault(name => name is not null)
                    : null;
                if (attachment is null)
                {
                    found.Add(new PlacedEffect(child, null, Vector3.Zero));
                }
                else if (attachments.TryGetValue(attachment, out var point))
                {
                    found.Add(new PlacedEffect(child, point.Bone, point.Offset));
                }
            }

            ListedEffects = found;
        }
#pragma warning disable CA1031 // optional: a model whose own settings cannot be read simply shows no added effects
        catch (Exception)
#pragma warning restore CA1031
        {
            ListedEffects = [];
        }
    }

    /// <summary>The model's attachments by name: the bone of the first influence and the offset in that bone's space.</summary>
    private static Dictionary<string, (string Bone, Vector3 Offset)> Attachments(Model model)
    {
        var result = new Dictionary<string, (string, Vector3)>(StringComparer.Ordinal);
        foreach (var (mesh, _, _) in model.GetEmbeddedMeshes())
        {
            if (!mesh.Data.TryGetValue("m_attachments", out var list) || !list.IsArray)
            {
                continue;
            }

            foreach (var entry in list.Values)
            {
                var value = entry["value"];
                var offset = value["m_vInfluenceOffsets"][0];
                result.TryAdd(entry["key"].ToString(CultureInfo.InvariantCulture), (value["m_influenceNames"][0].ToString(CultureInfo.InvariantCulture), new Vector3(Number(offset[0]), Number(offset[1]), Number(offset[2]))));
            }
        }

        return result;

        static float Number(KVObject value) => value.ToSingle(CultureInfo.InvariantCulture);
    }
}

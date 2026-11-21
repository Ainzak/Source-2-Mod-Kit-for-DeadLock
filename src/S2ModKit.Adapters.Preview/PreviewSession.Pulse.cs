using System.Globalization;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Time-driven material settings (<c>m_dynamicParams</c>), such as a glow's pulse <c>a * sin(b * time()) + c</c>: the renderer draws
/// a material's fixed value, so the preview evaluates the expressions it understands (<see cref="MaterialExpression"/>) every frame
/// and writes the result into the renderer's copy of the material's float setting.
/// </summary>
public sealed partial class PreviewSession
{
    private readonly Dictionary<Material, (string Name, byte[] Program)[]> timedSettings = [];

    /// <summary>Sets every drawn material's time-driven float settings for <paramref name="seconds"/> of play.</summary>
    public void AnimateMaterials(float seconds)
    {
        foreach (var material in drawOrder.Select(item => item.Draw.Material).Distinct())
        {
            if (!timedSettings.TryGetValue(material.Material, out var settings))
            {
                settings = TimedSettings(material.Material);
                timedSettings[material.Material] = settings;
            }

            foreach (var (name, program) in settings)
            {
                if (MaterialExpression.Evaluate(program, seconds) is { } value)
                {
                    material.FloatParams[name] = value;                    // the renderer's copy of the settings (sent to the shader when changed)
                }
            }
        }
    }

    /// <summary>The float settings of a material whose expression reads the time and is understood.</summary>
    internal static (string Name, byte[] Program)[] TimedSettings(Material material)
    {
        if (!material.Data.TryGetValue("m_dynamicParams", out var list) || !list.IsArray)
        {
            return [];
        }

        return [.. list.Values
            .Where(item => item.ValueType == KVValueType.Collection && item["m_value"].ValueType == KVValueType.BinaryBlob)
            .Select(item => (Name: item["m_name"].ToString(CultureInfo.InvariantCulture), Program: item["m_value"].AsBlob()))
            .Where(item => material.FloatParams.ContainsKey(item.Name) && MaterialExpression.UsesTime(item.Program))];
    }
}

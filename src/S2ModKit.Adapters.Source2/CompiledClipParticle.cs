using System.Globalization;
using System.Security.Cryptography;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>What <see cref="CompiledClipParticle.Add"/> wrote.</summary>
public sealed record ClipParticleReport(int Events, string Effect, string OutputSha256);

/// <summary>
/// Adds a particle start (<c>AE_CL_CREATE_PARTICLE_EFFECT</c>, <c>clip_particle_event@1</c>) to a compiled animation clip: the way the
/// game's own clips start effects (ability intros, hero-select poses). The event is a copy of a game clip's own particle
/// event (same layout and value types) with the effect, start time and attachment replaced, and the effect is added to the clip's
/// list of the files it uses. Pose data, the other events and every other value stay as they are.
/// </summary>
public static class CompiledClipParticle
{
    public const string EventClass = "AE_CL_CREATE_PARTICLE_EFFECT";

    public static (byte[] Bytes, ClipParticleReport Report) Add(byte[] clip, string logicalPath, byte[] templateClip, string templatePath,
        string effect, float startTime, string attachmentPoint, string attachmentType, bool stopOnSequenceChange)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(templateClip);
        ArgumentException.ThrowIfNullOrWhiteSpace(effect);
        ArgumentNullException.ThrowIfNull(attachmentPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentType);
        if (!effect.StartsWith("particles/", StringComparison.Ordinal) || !effect.EndsWith(".vpcf", StringComparison.Ordinal))
        {
            throw Errors.Input("CLIP_PARTICLE_PATH", $"'{effect}' is not an effect path.", "Pass a game effect path (particles/....vpcf).");
        }

        using var resource = Open(clip, logicalPath, out var data);
        using var templateResource = Open(templateClip, templatePath, out var templateData);
        var root = data.Data.Root;
        var duration = root["m_flDuration"].ToSingle(CultureInfo.InvariantCulture);
        if (startTime < 0 || startTime > duration)
        {
            throw Errors.Input("CLIP_PARTICLE_TIME", $"The start time {startTime} is outside the clip (0 to {duration} s).", "Pass a time within the clip.");
        }

        var template = templateData.Data.Root.TryGetValue("m_events", out var templateEvents) && templateEvents.IsArray
            ? templateEvents.Values.FirstOrDefault(item => item.TryGetValue("m_animEventClassName", out var name) && name.ToString(CultureInfo.InvariantCulture) == EventClass)
            : null;
        if (template is null)
        {
            throw Errors.Input("CLIP_PARTICLE_NO_TEMPLATE", $"'{templatePath}' starts no particle.", "Pass a game clip that starts a particle (an ability intro or a hero-select pose).");
        }

        var added = CompiledAddedEffects.Clone(template);
        added["m_flStartTime"]["m_flValue"] = CompiledAddedEffects.Number(added["m_flStartTime"]["m_flValue"], startTime);
        var kv = added["m_KV"];
        kv["name"] = CompiledAddedEffects.Text(kv["name"], effect);
        kv["attachment_point"] = CompiledAddedEffects.Text(kv["attachment_point"], attachmentPoint);
        kv["attachment_type"] = CompiledAddedEffects.Text(kv["attachment_type"], attachmentType);
        kv["stop_on_seq_change"] = new KVObject(stopOnSequenceChange) { Flag = kv["stop_on_seq_change"].Flag };
        if (!root.TryGetValue("m_events", out var events) || !events.IsArray)
        {
            throw Errors.Unsupported("CLIP_PARTICLE_LAYOUT", $"'{logicalPath}' has no event list.", "Report the clip.");
        }

        events.Add(added);
        var references = resource.Blocks.FirstOrDefault(block => block.Type == BlockType.RERL)
            ?? throw Errors.Unsupported("CLIP_PARTICLE_LAYOUT", $"'{logicalPath}' has no list of the files it uses.", "Report the clip.");
        var bytes = CompiledModelEdit.ReplaceBlocks(clip, resource, new Dictionary<int, byte[]>
        {
            [resource.Blocks.IndexOf(data)] = BinaryKv3BlockRoundTrip.SerializeAndVerify(data, "clip").Payload.ToArray(),
            [resource.Blocks.IndexOf(references)] = CompiledAddedEffects.WithReferences(clip.AsSpan((int)references.Offset, (int)references.Size), [effect], replace: false),
        });
        using (var check = Open(bytes, logicalPath, out var written))
        {
            var count = written.Data.Root["m_events"].Count;
            var listed = (check.ExternalReferences?.ResourceRefInfoList ?? []).Any(reference => string.Equals(reference.Name, effect, StringComparison.OrdinalIgnoreCase));
            if (count != events.Count || !listed)
            {
                throw Errors.Verification("CLIP_PARTICLE_VERIFY", "The written clip does not read back with the added event and file.", "Report the clip.");
            }
        }

        return (bytes, new ClipParticleReport(events.Count, effect, Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static Resource Open(byte[] compiled, string logicalPath, out BinaryKV3 data)
    {
        var resource = new Resource { FileName = logicalPath };
        try
        {
            resource.Read(new MemoryStream(compiled));
            data = resource.DataBlock as BinaryKV3 ?? throw Errors.Input("CLIP_PARTICLE_NOT_A_CLIP", $"'{logicalPath}' has no KV3 data block.", "Pass compiled .vnmclip_c files.");
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }
}

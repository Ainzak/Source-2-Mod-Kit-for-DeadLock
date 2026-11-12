using System.Globalization;
using System.Security.Cryptography;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

/// <summary>What <see cref="CompiledClipEvents.Apply"/> changed in a clip.</summary>
public sealed record ClipEventsReport(int Frames, int Tracks, int Events, bool SyncTrack, bool FloatChannels, string OutputSha256);

/// <summary>
/// Gives an edited animation clip the game clip's own events in the current format (<c>clip_events_from_game@1</c>). The Deadlock
/// SDK compiler writes events with an older layout (<c>m_flStartTimeSeconds</c>) and no per-track read offsets; the game's clips
/// keep <c>m_flStartTime { m_flValue }</c> and <c>m_nTrackReadOffset</c>. An edited clip changes only bone poses, so its events,
/// sync track and float channels are taken from the game's clip of the same path (same frame count, duration and skeleton
/// required), and every track's read offset is set from its static flags (3 for animated rotation, 3 for animated position,
/// 1 for animated scale; this reproduces the game's own offsets). Pose data is not touched.
/// </summary>
public static class CompiledClipEvents
{
    public static (byte[] Bytes, ClipEventsReport Report) Apply(byte[] edited, byte[] game, string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(game);
        using var editedResource = Open(edited, logicalPath, out var data);
        using var gameResource = Open(game, logicalPath, out var gameData);
        var root = data.Data.Root;
        var gameRoot = gameData.Data.Root;
        foreach (var key in new[] { "m_nNumFrames", "m_flDuration", "m_skeleton" })
        {
            var same = root.ContainsKey(key) && gameRoot.ContainsKey(key) && (key == "m_flDuration"
                ? Math.Abs(root[key].ToDouble(CultureInfo.InvariantCulture) - gameRoot[key].ToDouble(CultureInfo.InvariantCulture)) < 0.001   // the SDK rounds to 4 places
                : string.Equals(root[key].ToString(CultureInfo.InvariantCulture), gameRoot[key].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (!same)
            {
                throw Errors.Input("CLIP_EVENTS_MISMATCH", $"The edited clip and the game clip differ in {key}.", "Pass the game clip of the same path; keep the clip's frame count and duration.");
            }
        }

        root["m_flDuration"] = gameRoot["m_flDuration"];                    // the game's exact length (events are timed against it)

        var settings = root["m_trackCompressionSettings"];
        var gameSettings = gameRoot["m_trackCompressionSettings"];
        if (!settings.IsArray || !gameSettings.IsArray || settings.Count != gameSettings.Count)
        {
            throw Errors.Input("CLIP_EVENTS_MISMATCH", "The edited clip and the game clip have a different number of tracks.", "Use the hero's current skeleton.");
        }

        var like = gameSettings[0]["m_nTrackReadOffset"];
        var order = gameSettings[0].Children.Select(child => child.Key).ToArray();
        var offset = 0;
        root["m_trackCompressionSettings"] = CompiledAddedEffects.Each(settings, (track, _) =>
        {
            var values = track.Children.ToDictionary(child => child.Key, child => child.Value, StringComparer.Ordinal);
            values["m_nTrackReadOffset"] = CompiledAddedEffects.Integer(like, offset);
            offset += (Static(track, "m_bIsRotationStatic") ? 0 : 3) + (Static(track, "m_bIsTranslationStatic") ? 0 : 3) + (Static(track, "m_bIsScaleStatic") ? 0 : 1);
            var ordered = KVObject.Collection();                             // the game's key order; keys it lacks follow
            ordered.Flag = track.Flag;
            foreach (var key in order.Where(values.ContainsKey).Concat(values.Keys.Where(key => !order.Contains(key))))
            {
                ordered.Add(key, values[key]);
            }

            return ordered;
        });

        root["m_events"] = CompiledAddedEffects.Clone(gameRoot["m_events"]);
        var sync = gameRoot.ContainsKey("m_syncTrack");
        if (sync)
        {
            root["m_syncTrack"] = CompiledAddedEffects.Clone(gameRoot["m_syncTrack"]);
        }

        var floats = gameRoot.ContainsKey("m_floatChannelData");
        if (floats)
        {
            root["m_floatChannelData"] = CompiledAddedEffects.Clone(gameRoot["m_floatChannelData"]);
        }

        var payload = BinaryKv3BlockRoundTrip.SerializeAndVerify(data, "clip").Payload.ToArray();
        var bytes = CompiledModelEdit.ReplaceBlocks(edited, editedResource, new Dictionary<int, byte[]> { [editedResource.Blocks.IndexOf(data)] = payload });
        using (Open(bytes, logicalPath, out var check))
        {
            if (check.Data.Root["m_trackCompressionSettings"].Count != gameSettings.Count)
            {
                throw Errors.Verification("CLIP_EVENTS_VERIFY", "The written clip does not read back with all its tracks.", "Report the clip.");
            }
        }

        return (bytes, new ClipEventsReport(int.Parse(root["m_nNumFrames"].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), gameSettings.Count,
            gameRoot["m_events"].Count, sync, floats, Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static bool Static(KVObject track, string key) => track.ContainsKey(key) && track[key].ToBoolean(CultureInfo.InvariantCulture);

    private static Resource Open(byte[] compiled, string logicalPath, out BinaryKV3 data)
    {
        var resource = new Resource { FileName = logicalPath };
        try
        {
            resource.Read(new MemoryStream(compiled));
            data = resource.DataBlock as BinaryKV3 ?? throw Errors.Input("CLIP_EVENTS_NOT_A_CLIP", $"'{logicalPath}' has no KV3 data block.", "Pass compiled .vnmclip_c files.");
            if (!data.Data.Root.ContainsKey("m_trackCompressionSettings"))
            {
                throw Errors.Input("CLIP_EVENTS_NOT_A_CLIP", $"'{logicalPath}' is not an animation clip.", "Pass compiled .vnmclip_c files.");
            }

            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }
}

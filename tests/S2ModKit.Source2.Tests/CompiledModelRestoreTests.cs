using System.Globalization;
using S2ModKit.Adapters.Source2;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed class CompiledModelRestoreTests
{
    private const string Text = "<!-- kv3 -->\n{\n\tFirst_t = \n\t{\n\t\tm_name = \"a } b\"\n\t\tm_list = \n\t\t[\n\t\t\tresource:\"x.vdata\",\n\t\t]\n\t}\n\tPlain = 3\n}\n";

    [Fact]
    public void TopLevelBlocksAreFoundWholeAndStringsDoNotCloseThem()
    {
        Assert.Equal(["First_t", "Plain"], CompiledModelRestore.TopKeys(Text).Order(StringComparer.Ordinal));
        Assert.Equal("\tFirst_t = \n\t{\n\t\tm_name = \"a } b\"\n\t\tm_list = \n\t\t[\n\t\t\tresource:\"x.vdata\",\n\t\t]\n\t}", CompiledModelRestore.Block(Text, "First_t"));
        Assert.Equal("\tPlain = 3", CompiledModelRestore.Block(Text, "Plain"));
        Assert.Null(CompiledModelRestore.Block(Text, "Missing"));
    }

    /// <summary>Live check (S2MODKIT_TEST_GAME): Ivy's model with its tagged sounds and health bar attachment taken out (as an older mod
    /// has it) gets them back from the game's model, with the tagged sounds file in its list of files; the rest stays.</summary>
    [Fact]
    public void AnOlderModelGetsTheGamesSettingsAndAttachmentsBack()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to the Deadlock game folder to enable this proprietary local check.");
        }

        const string Model = "models/heroes_wip/ivy/ivy.vmdl_c";
        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(Model)!, out var current);
        Assert.False(CompiledModelRestore.Gaps(current, current, Model).Any);

        var older = Older(current, Model, "TaggedSoundSettings_t", "healthbar");
        var gaps = CompiledModelRestore.Gaps(older, current, Model);
        Assert.Equal(["TaggedSoundSettings_t"], gaps.Settings);
        Assert.Equal(["healthbar"], gaps.Attachments);

        var restored = CompiledModelRestore.Restore(older, current, Model);

        Assert.False(CompiledModelRestore.Gaps(restored, current, Model).Any);
        using var resource = new Resource { FileName = Model };
        resource.Read(new MemoryStream(restored));
        var model = (Model)resource.DataBlock!;
        var text = model.Data["m_modelInfo"]["m_keyValueText"].ToString(CultureInfo.InvariantCulture);
        Assert.Contains("TaggedSoundSettings_t", text, StringComparison.Ordinal);
        Assert.Contains(CompiledAddedEffects.References(current, Model).First(reference => reference.Contains("tagged_sounds", StringComparison.Ordinal)), CompiledAddedEffects.References(restored, Model));
        Assert.Equal(CompiledAddedEffects.Attachments(current, Model).Count, CompiledAddedEffects.Attachments(restored, Model).Count);
        Assert.Equal(CompiledAddedEffects.Attachments(current, Model).Single(item => item.Name == "healthbar"), CompiledAddedEffects.Attachments(restored, Model).Single(item => item.Name == "healthbar"));
        Assert.Equal(CompiledModelRestore.TopKeys(CompiledAddedEffects.KeyValueText((Model)Open(current, Model).DataBlock!)).Order(StringComparer.Ordinal), CompiledModelRestore.TopKeys(text).Order(StringComparer.Ordinal));
    }

    /// <summary>The model without one settings block and one attachment point (in every mesh).</summary>
    private static byte[] Older(byte[] compiled, string path, string block, string attachment)
    {
        using var resource = Open(compiled, path);
        var model = (Model)resource.DataBlock!;
        var payloads = new Dictionary<int, byte[]>();
        var text = CompiledAddedEffects.KeyValueText(model);
        model.Data["m_modelInfo"]["m_keyValueText"] = new KVObject(text.Replace(CompiledModelRestore.Block(text, block)! + "\n", string.Empty, StringComparison.Ordinal));
        payloads[resource.Blocks.IndexOf(model)] = CompiledAddedEffects.Serialize(model);
        foreach (var mesh in resource.Blocks.OfType<Mesh>())
        {
            if (CompiledAddedEffects.AttachmentList(mesh) is { Count: > 0 } list)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i]["key"].ToString(CultureInfo.InvariantCulture) == attachment)
                    {
                        list.RemoveAt(i);
                    }
                }

                payloads[resource.Blocks.IndexOf(mesh)] = CompiledAddedEffects.Serialize(mesh);
            }
        }

        return CompiledModelEdit.ReplaceBlocks(compiled, resource, payloads);
    }

    private static Resource Open(byte[] compiled, string path)
    {
        var resource = new Resource { FileName = path };
        resource.Read(new MemoryStream(compiled));
        return resource;
    }
}

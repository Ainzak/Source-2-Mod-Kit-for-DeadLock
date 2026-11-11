using System.Numerics;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop.Tests;

/// <summary>
/// Local checks of the direct reshape (<c>shape_part@1</c>) on the game's own models (proprietary inputs, nothing installed):
/// S2MODKIT_TEST_GAME is the Deadlock game folder and S2MODKIT_MESHOPTIMIZER_PATH the native mesh codec the release ships.
/// </summary>
public sealed class ShapeBuildTests
{
    private const string Celeste = "models/heroes_wip/unicorn/unicorn.vmdl";
    private const int Clothes = 5;                                          // unicorn_clothes.vmat in level-0 draw order

    private static (string Game, byte[] Model)? Inputs()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game) || !File.Exists(Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")))
        {
            return null;
        }

        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(Celeste + "_c")!, out var bytes);
        return (game, bytes);
    }

    private static Dictionary<string, Vector3[]> Positions(byte[] model)
    {
        using var resource = new Resource { FileName = Celeste + "_c" };
        resource.Read(new MemoryStream(model));
        var result = new Dictionary<string, Vector3[]>();
        foreach (var (mesh, index, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshes())
        {
            for (var b = 0; b < mesh.VBIB.VertexBuffers.Count; b++)
            {
                var buffer = mesh.VBIB.VertexBuffers[b];
                result[$"{index}/{b}"] = ValveResourceFormat.Blocks.VBIB.GetVector3AttributeArray(buffer, buffer.InputLayoutFields.First(field => field.SemanticName == "POSITION"));
            }
        }

        return result;
    }

    [Fact]
    public void APartIsReshapedInEveryLevelOfDetailAndNothingElseMoves()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var matrix = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(0, 0, 3);
        var reshaped = CompiledPartReshape.Apply(inputs.Model, Celeste + "_c", [new PartReshape(Clothes, matrix)], Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));

        var before = Positions(inputs.Model);
        var after = Positions(reshaped);
        var moved = before.Keys.Where(key => !before[key].SequenceEqual(after[key])).ToArray();
        Assert.Equal(["0/5", "4/5", "7/5", "10/5"], moved);                 // the clothes buffer of the body mesh in LOD 0-3
        foreach (var key in moved)
        {
            Assert.All(Enumerable.Range(0, before[key].Length), v => Assert.True(Vector3.Distance(after[key][v], Vector3.Transform(before[key][v], matrix)) < 1e-3f));
        }
    }

    /// <summary>A turned part's normals and tangents turn with it (Celeste's clothes store them packed in one 32-bit value), read back
    /// with VRF's own decoder; other buffers keep theirs.</summary>
    [Fact]
    public void ATurnedPartsNormalsTurnWithIt()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var matrix = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        var reshaped = CompiledPartReshape.Apply(inputs.Model, Celeste + "_c", [new PartReshape(Clothes, matrix)], Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));

        var before = Normals(inputs.Model);
        var after = Normals(reshaped);
        Assert.Equal("R32_UINT", before["0/5"].Format);
        foreach (var key in before.Keys)
        {
            var turned = key is "0/5" or "4/5" or "7/5" or "10/5";
            Assert.All(Enumerable.Range(0, before[key].Normals.Length), v =>
            {
                var expected = turned ? Vector3.TransformNormal(before[key].Normals[v], matrix) : before[key].Normals[v];
                Assert.True(Vector3.Dot(after[key].Normals[v], expected) > 0.999f, $"{key} vertex {v}: normal {after[key].Normals[v]}, expected {expected}");
                var tangent = new Vector3(before[key].Tangents[v].X, before[key].Tangents[v].Y, before[key].Tangents[v].Z);
                var expectedTangent = turned ? Vector3.TransformNormal(tangent, matrix) : tangent;
                Assert.True(Vector3.Dot(new Vector3(after[key].Tangents[v].X, after[key].Tangents[v].Y, after[key].Tangents[v].Z), expectedTangent) > 0.995f, $"{key} vertex {v}: tangent");
                Assert.Equal(before[key].Tangents[v].W, after[key].Tangents[v].W);
            });
        }
    }

    private static Dictionary<string, (string Format, Vector3[] Normals, Vector4[] Tangents)> Normals(byte[] model)
    {
        using var resource = new Resource { FileName = Celeste + "_c" };
        resource.Read(new MemoryStream(model));
        var result = new Dictionary<string, (string, Vector3[], Vector4[])>();
        foreach (var (mesh, index, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshes())
        {
            for (var b = 0; b < mesh.VBIB.VertexBuffers.Count; b++)
            {
                var buffer = mesh.VBIB.VertexBuffers[b];
                var field = buffer.InputLayoutFields.FirstOrDefault(item => item.SemanticName == "NORMAL");
                if (field.SemanticName is not null && field.Format.ToString() == "R32_UINT")
                {
                    var (normals, tangents) = ValveResourceFormat.Blocks.VBIB.GetNormalTangentArray(buffer, field);
                    result[$"{index}/{b}"] = (field.Format.ToString(), normals, tangents);
                }
            }
        }

        return result;
    }

    [Fact]
    public void PackedNormalsSurviveARoundTrip()
    {
        foreach (var normal in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f)), Vector3.Normalize(new Vector3(-0.6f, 0.2f, -0.77f)) })
        {
            var tangent = Vector3.Normalize(Vector3.Cross(normal, Math.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var packed = PackedNormalTangent.Encode(normal, new Vector4(tangent, -1));
            var (n, t) = PackedNormalTangent.Decode(packed);
            Assert.True(Vector3.Dot(n, normal) > 0.9999f);
            Assert.True(Vector3.Dot(new Vector3(t.X, t.Y, t.Z), tangent) > 0.9999f);
            Assert.Equal(-1, t.W);
            Assert.Equal(packed, PackedNormalTangent.Encode(n, t));                // a decoded value encodes to the same bits
        }
    }

    [Fact]
    public void BoundsFollowTheReshapedPart()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var reshaped = CompiledPartReshape.Apply(inputs.Model, Celeste + "_c", [new PartReshape(Clothes, Matrix4x4.CreateTranslation(0, 0, 40))], Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH"));

        static float Top(byte[] model)
        {
            using var resource = new Resource { FileName = Celeste + "_c" };
            resource.Read(new MemoryStream(model));
            var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshes().First().Mesh;
            return mesh.Data["m_sceneObjects"][0]["m_vMaxBounds"][2].ToSingle(System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.True(Top(reshaped) > Top(inputs.Model) + 10, "the mesh bounds did not grow with the raised clothes");
    }

    [Fact]
    public void EveryBaseHeroPartCanBeReshaped()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        using var package = new Package();
        package.Read(Path.Combine(inputs.Game, "citadel", "pak01_dir.vpk"));
        var refused = new List<string>();
        foreach (var hero in Hero.Catalogue)
        {
            if (package.FindEntry(hero.Model + "_c") is { } entry)
            {
                package.ReadEntry(entry, out var bytes);
                refused.AddRange(CompiledPartReshape.Refusals(bytes, hero.Model + "_c").Select(pair => $"{hero.Name} part {pair.Key + 1}: {pair.Value}"));
            }
        }

        Assert.Empty(refused);
    }

    [Fact]
    public async Task BuildEditedModReshapesThroughTheApp()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var token = TestContext.Current.CancellationToken;
        using var root = new ModTestRoot();
        var pak = Path.Combine(root.Game, "citadel", "pak01_dir.vpk");
        File.Delete(pak);
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(pak, new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [Celeste + "_c"] = inputs.Model }, token);
        var shape = new PartShape(new Vector3(1.4f, 1.4f, 1.4f), Vector3.Zero, new Vector3(0, 0, 5));
        var edit = new PartShapeEdit(Clothes, "models/heroes_wip/unicorn/materials/unicorn_clothes.vmat", shape, new Vector3(-20, -20, 0), new Vector3(20, 20, 70), 30f, ShapeLevel.Full);

        await root.Catalog().BuildEditsAsync(null, Celeste, new HashSet<int>(), [], [edit], null, null, null, null, token);

        var built = Assert.Single(await root.Catalog().LoadAsync(token), mod => mod.Loaded);
        using var package = new Package();
        package.Read(built.Files[0]);
        package.ReadEntry(package.FindEntry(Celeste + "_c")!, out var model);
        var (before, after) = (Positions(inputs.Model)["0/5"], Positions(model)["0/5"]);
        var matrix = shape.Matrix(edit.BoundsMin, edit.BoundsMax);
        Assert.True(Vector3.Distance(after[100], Vector3.Transform(before[100], matrix)) < 1e-3f);
        Assert.Equal(Positions(inputs.Model)["0/1"], Positions(model)["0/1"]);   // the head is untouched
        Assert.Equal(ShapeLevel.Full, Assert.Single(EditRecipe.Of(built)!.Shapes).Level);
    }

    [Fact]
    public void APartOutsideTheModelIsRefused()
    {
        if (Inputs() is not { } inputs)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME and S2MODKIT_MESHOPTIMIZER_PATH to enable this proprietary local check.");
            return;
        }

        var error = Assert.Throws<S2ModKitException>(() => CompiledPartReshape.Apply(inputs.Model, Celeste + "_c", [new PartReshape(99, Matrix4x4.Identity)], Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")));
        Assert.Equal("SHAPE_PART_RANGE", error.Error.Code);
        var twice = Assert.Throws<S2ModKitException>(() => CompiledPartReshape.Apply(inputs.Model, Celeste + "_c",
            [new PartReshape(Clothes, Matrix4x4.Identity), new PartReshape(Clothes, Matrix4x4.Identity)], Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH")));
        Assert.Equal("SHAPE_PART_TWICE", twice.Error.Code);
    }
}

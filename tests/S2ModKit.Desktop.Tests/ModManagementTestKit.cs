using System.Text;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

internal sealed class FakeGame(bool running = false) : IGameProcessProbe
{
    public bool Running { get; set; } = running;

    public bool IsGameRunning() => Running;
}

/// <summary>A Recycle Bin that deletes for real in the synthetic folder, so tests see the files gone.</summary>
internal sealed class FakeBin : IRecycleBin
{
    public List<string> Recycled { get; } = [];

    public void Recycle(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            File.Delete(path);
            Recycled.Add(Path.GetFileName(path));
        }
    }
}

/// <summary>A synthetic game folder (citadel/pak01_dir.vpk with Wraith's model, an addons folder), a library and a data folder.</summary>
internal sealed class ModTestRoot : IDisposable
{
    public const string Model = "models/heroes_wip/wraith/wraith.vmdl_c";

    private readonly string root = Path.Combine(Path.GetTempPath(), "s2modkit-desktop-tests", Guid.NewGuid().ToString("N"));

    public ModTestRoot()
    {
        Directory.CreateDirectory(Addons);
        Directory.CreateDirectory(Library);
        CompactVpkArchiveIO.WriteAsync(Path.Combine(Game, "citadel", "pak01_dir.vpk"),
            new Dictionary<string, ReadOnlyMemory<byte>> { [Model] = "model"u8.ToArray() }, CancellationToken.None).GetAwaiter().GetResult();
    }

    public string Game => Path.Combine(root, "game");

    public string Addons => Path.Combine(Game, "citadel", "addons");

    public string Library => Path.Combine(root, "library");

    public string Data => Path.Combine(root, "data");

    public string Root => root;

    public FakeGame GameProbe { get; } = new();

    public FakeBin Bin { get; } = new();

    public AppSettings Settings => new(Game, Data);

    public ModCatalog Catalog(string? managerState = null) => new(Settings, managerState ?? Path.Combine(root, "no-manager-state.json"), GameProbe, Bin);

    public string In(string fileName) => Path.Combine(Addons, fileName);

    public string[] AddonNames() => [.. Directory.EnumerateFiles(Addons).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];

    /// <summary>A valid VPK in the addons folder (or a library path) with the given logical entries.</summary>
    public async Task<string> VpkAsync(string path, params string[] entries)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), false);   // fixtures only inside the test folder
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await CompactVpkArchiveIO.WriteAsync(path, entries.ToDictionary(entry => entry, entry => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(entry)), CancellationToken.None);
        return path;
    }

    /// <summary>Mod Manager's state file listing the given mods as downloaded (disabled) or installed with their files.</summary>
    public async Task<string> ManagerStateAsync(params (string Id, string Name, string Status, string[] Files)[] mods)
    {
        var local = new
        {
            state = new
            {
                localMods = mods.Select(mod => new { remoteId = mod.Id, name = mod.Name, hero = (string?)null, status = mod.Status, downloadedAt = "2026-01-02T03:04:05.000Z", createdAt = (string?)null, installedVpks = mod.Files }).ToArray(),
            },
        };
        var path = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["local-config"] = System.Text.Json.JsonSerializer.Serialize(local) }));
        return path;
    }

    public void Dispose() => Directory.Delete(root, true);
}

/// <summary>A minimal valid 7z writer for fixtures (SharpCompress cannot write 7z or rar): every file is its own folder with the
/// Copy coder, headers uncompressed.</summary>
internal static class SevenZipFixture
{
    public static void Write(string path, params (string Name, byte[] Data)[] files)
    {
        var header = new List<byte> { 0x01, 0x04 };                          // Header, MainStreamsInfo
        header.AddRange([0x06, 0x00]);                                       // PackInfo, pack position 0
        header.AddRange(Number(files.Length));
        header.Add(0x09);                                                    // pack sizes
        header.AddRange(files.SelectMany(file => Number(file.Data.Length)));
        header.Add(0x00);
        header.Add(0x07);                                                    // UnpackInfo
        header.Add(0x0B);                                                    // Folder
        header.AddRange(Number(files.Length));
        header.Add(0x00);                                                    // not external
        foreach (var _ in files)
        {
            header.AddRange([0x01, 0x01, 0x00]);                             // one coder, id size 1, id 0 = Copy
        }

        header.Add(0x0C);                                                    // coder unpack sizes
        header.AddRange(files.SelectMany(file => Number(file.Data.Length)));
        header.AddRange([0x00, 0x08, 0x00, 0x00]);                           // end UnpackInfo, empty SubStreamsInfo (one stream per folder), end MainStreamsInfo
        header.AddRange([0x05]);                                             // FilesInfo
        header.AddRange(Number(files.Length));
        var names = files.SelectMany(file => Encoding.Unicode.GetBytes(file.Name + "\0")).ToArray();
        header.Add(0x11);
        header.AddRange(Number(names.Length + 1));
        header.Add(0x00);
        header.AddRange(names);
        header.AddRange([0x00, 0x00]);                                       // end FilesInfo, end Header

        var packed = files.SelectMany(file => file.Data).ToArray();
        var start = new byte[20];
        BitConverter.GetBytes((long)packed.Length).CopyTo(start, 0);        // next header offset
        BitConverter.GetBytes((long)header.Count).CopyTo(start, 8);
        BitConverter.GetBytes(Crc32(header.ToArray())).CopyTo(start, 16);
        using var output = File.Create(path);
        output.Write([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        output.Write(BitConverter.GetBytes(Crc32(start)));
        output.Write(start);
        output.Write(packed);
        output.Write(header.ToArray());
    }

    private static byte[] Number(int value) =>
        value < 0x80 ? [(byte)value] : value < 0x4000 ? [(byte)(0x80 | (value >> 8)), (byte)(value & 0xFF)] : throw new ArgumentOutOfRangeException(nameof(value));

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

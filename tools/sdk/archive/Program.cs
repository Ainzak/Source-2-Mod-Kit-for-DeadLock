using System.Security.Cryptography;
using System.Text.Json;
using S2ModKit.Adapters.Vpk;
using SteamDatabase.ValvePak;

// A separate research executable; reuse the native archive adapter, not resource writers.
var action = args[0];
if (action is "extract" or "check-source")
{
    using var archive = new Package();
    archive.Read(args[1]);
    var entries = (archive.Entries ?? throw new InvalidDataException("Missing VPK tree"))
        .Values.SelectMany(x => x).ToDictionary(x => x.GetFullPath().Replace('\\', '/'), StringComparer.Ordinal);
    foreach (var item in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[2]))!)
    {
        var path = item.Key;
        if (path.StartsWith('/') || path.Split('/').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("Unsafe archive path");
        if (!entries.TryGetValue(path, out var entry) || entry.TotalLength > 256 * 1024 * 1024)
            throw new InvalidDataException("Missing/oversize archive entry: " + path);
        archive.ReadEntry(entry, out var bytes, validateCrc: true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (item.Value.Length != 0 && hash != item.Value)
            throw new InvalidDataException("Current source changed: " + path);
        if (action == "extract")
        {
            var output = Path.GetFullPath(Path.Combine(args[3], path));
            var root = Path.GetFullPath(args[3]) + Path.DirectorySeparatorChar;
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive path escape");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var stream = new FileStream(output, FileMode.CreateNew);
            stream.Write(bytes);
        }
    }
}
else if (action == "package")
{
    var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
    var entries = files.ToDictionary(x => x.Key, x => (ReadOnlyMemory<byte>)File.ReadAllBytes(x.Value));
    if (File.Exists(args[2])) throw new IOException("Package exists");
    await CompactVpkArchiveIO.WriteAsync(args[2], entries);
    var verified = await CompactVpkArchiveIO.ReadAndVerifyAsync(args[2]);
    Console.WriteLine(JsonSerializer.Serialize(verified.Entries.Select(x => new { x.LogicalPath, x.Length, x.ContentHash })));
}
else if (action == "verify")
{
    var verified = await CompactVpkArchiveIO.ReadAndVerifyAsync(args[1]);
    if (args.Length > 2)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[2]))!;
        var actual = verified.Entries.ToDictionary(x => x.LogicalPath, x => x.ContentHash, StringComparer.Ordinal);
        if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var hash) || hash.Value != x.Value))
            throw new InvalidDataException("Package entries differ from verified model and skeleton");
    }
}
else if (action == "collisions")
{
    var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[2]))!;
    var inspector = new ValvePakAddonArchiveInspector();
    foreach (var file in Directory.EnumerateFiles(args[1], "*.vpk"))
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"_\d{3}\.vpk$")) continue;
        foreach (var path in paths)
            if ((await inspector.ProbeAsync(file, path, false)).ContainsLogicalPath)
                throw new InvalidOperationException("Existing override: " + file + " : " + path);
    }
}
else throw new ArgumentException("Unknown archive action");

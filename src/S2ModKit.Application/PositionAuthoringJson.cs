using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public static class PositionAuthoringJson
{
    public const int MaximumArtifactBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonDefaults.Options)
    {
        WriteIndented = false,
        MaxDepth = 32,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    public static T Read<T>(ReadOnlyMemory<byte> bytes, int budget = MaximumArtifactBytes)
    {
        if (bytes.Length is < 2 || bytes.Length > budget) throw Invalid("IMPORT_JSON_BUDGET", "Authoring document exceeds its byte budget.");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            CheckKeys(doc.RootElement);
            CheckTableBudgets(doc.RootElement, typeof(T) == typeof(PositionAuthoringDelta) ? 1_000_000 : 2_000_000);
            return JsonSerializer.Deserialize<T>(bytes.Span, Options) ?? throw new JsonException("Null document.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        { throw new S2ModKitException(Invalid("IMPORT_JSON_INVALID", "Invalid, duplicate, unknown, missing or null authoring facts.").Error, e); }
    }
    private static void CheckKeys(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException("Duplicate property."); CheckKeys(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) CheckKeys(item);
    }
    private static void CheckTableBudgets(JsonElement root, long recordBudget)
    {
        long records = 0, indices = 0;
        void Visit(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name is "positions" or "triangles" or "members" or "vertexIds" or "lods")
                    {
                        if (p.Value.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid bounded table.");
                        var n = p.Value.GetArrayLength();
                        if (p.Name == "positions") { if (n > 500_000) throw new JsonException("Member position budget."); records += n; }
                        if (p.Name == "triangles") indices += n;
                        if (p.Name == "members" && n > 256 || p.Name == "lods" && n > 8 || p.Name == "vertexIds" && n > 1_000_000)
                            throw new JsonException("Member/map budget.");
                        if (records > recordBudget || indices > 6_000_000) throw new JsonException("Aggregate table budget.");
                    }
                    Visit(p.Value);
                }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Visit(item);
        }
        Visit(root);
    }
    public static byte[] Canonical<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Options));
        return Canonical(document.RootElement);
    }
    public static byte[] Canonical(JsonElement root)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) Write(writer, root);
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            w.WriteStartObject(); foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); Write(w, p.Value); }
            w.WriteEndObject();
        }
        else if (e.ValueKind == JsonValueKind.Array) { w.WriteStartArray(); foreach (var item in e.EnumerateArray()) Write(w, item); w.WriteEndArray(); }
        else e.WriteTo(w);
    }
    public static string Word(float f)
    {
        if (!float.IsFinite(f)) throw Invalid("IMPORT_POSITION_NONFINITE", "Source/return coordinates must be finite binary32.");
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, f); return Convert.ToHexStringLower(bytes);
    }
    public static float DecodeWord(string word)
    {
        if (word is null || word.Length != 8 || word.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("IMPORT_WORD_INVALID", "Coordinates require lowercase little-endian float32 hex words.");
        var f = BinaryPrimitives.ReadSingleLittleEndian(Convert.FromHexString(word));
        if (!float.IsFinite(f)) throw Invalid("IMPORT_POSITION_NONFINITE", "Nonfinite coordinate word.");
        return f;
    }
    public static string[] Words(Point3 p) => [Word(p.X), Word(p.Y), Word(p.Z)];
    public static Point3 Point(string[] words)
    {
        if (words is null || words.Length != 3) throw Invalid("IMPORT_WORD_INVALID", "A position needs exactly three words.");
        return new(DecodeWord(words[0]), DecodeWord(words[1]), DecodeWord(words[2]));
    }
    public static void Hash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("IMPORT_HASH_INVALID", "Hash must be lowercase SHA-256.");
    }
    public static S2ModKitException Invalid(string code, string message) => Errors.InvalidRecipe(code, message, "Use source-bound all-LOD authoring artifacts; no model was written.");
    public static string BufferId(int lod, int mesh, int buffer) => $"lod:{lod}/mesh:{mesh}/buffer:{buffer}";
}

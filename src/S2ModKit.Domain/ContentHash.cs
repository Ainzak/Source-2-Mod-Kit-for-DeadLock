using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

[JsonConverter(typeof(ContentHashJsonConverter))]
public readonly record struct ContentHash
{
    public const int HexLength = 64;

    public ContentHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.ToLowerInvariant();
        if (normalized.Length != HexLength || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A content hash must be a 64-character SHA-256 hexadecimal value.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public static ContentHash Compute(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    public static ContentHash Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ContentHash(Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    public override string ToString() => Value;
}

public sealed class ContentHashJsonConverter : JsonConverter<ContentHash>
{
    public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("Content hash cannot be null.");
        try
        {
            return new ContentHash(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(exception.Message, exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}


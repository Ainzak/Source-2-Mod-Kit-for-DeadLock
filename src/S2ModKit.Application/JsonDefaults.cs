using System.Text.Json;
using System.Text.Json.Serialization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw Errors.InvalidRecipe("JSON_NULL_DOCUMENT", $"{description} cannot be null.", "Provide a JSON object matching the published schema.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new S2ModKitException(
                new S2Error("JSON_INVALID", "schema", $"{description} is not valid: {exception.Message}", "Validate the document against the corresponding schema.", ErrorCategory.CliOrSchema),
                exception);
        }
    }

    public static byte[] SerializeToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

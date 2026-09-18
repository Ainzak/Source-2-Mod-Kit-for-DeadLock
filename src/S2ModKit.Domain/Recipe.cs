using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2ModKit.Domain;

public sealed record RecipeDocument
{
    public int SchemaVersion { get; init; }

    public string RecipeId { get; init; } = string.Empty;

    public ContentHash InputHash { get; init; }

    public IReadOnlyList<RecipeOperation> Operations { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RemoveComponentOperation), "remove_component")]
[JsonDerivedType(typeof(TransformComponentOperation), "transform_component")]
public abstract record RecipeOperation
{
    public string OperationId { get; init; } = string.Empty;

    [JsonIgnore]
    public abstract string OperationKind { get; }

    public int Version { get; init; } = 1;

    public abstract string Granularity { get; init; }

    public ComponentSelector Selector { get; init; } = new();

    public string LodPolicy { get; init; } = "all_present";

    public IReadOnlyDictionary<string, int> ExpectedMatchesByLod { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record RemoveComponentOperation : RecipeOperation
{
    [JsonIgnore]
    public override string OperationKind => "remove_component";

    public override string Granularity { get; init; } = "draw_call";
}

public sealed record TransformComponentOperation : RecipeOperation
{
    [JsonIgnore]
    public override string OperationKind => "transform_component";

    public override string Granularity { get; init; } = "draw_call_owned_vertices";

    public IReadOnlyDictionary<string, int> ExpectedVerticesByLod { get; init; } = new Dictionary<string, int>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ConnectedComponentIdsByLod { get; init; }

    public string OwnershipPolicy { get; init; } = "exclusive";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PhysicsPolicy { get; init; }

    public ComponentTransform Transform { get; init; } = new();

    public TransformLimits Limits { get; init; } = new();
}

public sealed record ComponentTransform
{
    public TransformPivot Pivot { get; init; } = new();

    public float UniformScale { get; init; }

    public TransformVector3 Translation { get; init; } = new();
}

public sealed record TransformPivot
{
    public string Kind { get; init; } = string.Empty;

    public int ReferenceLod { get; init; }
}

public sealed record TransformVector3
{
    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }
}

public sealed record TransformLimits
{
    public float MaximumVertexDisplacement { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MaximumCollisionDisplacement { get; init; }
}

public sealed record ComponentSelector
{
    public string Kind { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaterialPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DrawCallIds { get; init; }
}

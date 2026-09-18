using S2ModKit.Domain;

namespace S2ModKit.Domain.Tests;

public sealed class DomainTests
{
    [Fact]
    public void ContentHashIsDeterministicAndLowerCase()
    {
        var first = ContentHash.Compute([1, 2, 3, 4]);
        var second = ContentHash.Compute([1, 2, 3, 4]);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Value.Length);
        Assert.Equal(first.Value.ToLowerInvariant(), first.Value);
    }

    [Fact]
    public void DrawCallIdentityNormalizesSourcePaths()
    {
        var first = DrawCallSnapshot.Create("Models\\Hero//LOD0.vmesh_c", 0, 0, 1, "/Materials\\Accessory.vmat", 12, 36);
        var second = DrawCallSnapshot.Create("models/hero/lod0.vmesh_c", 0, 0, 1, "materials/accessory.vmat", 12, 36);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("materials/accessory.vmat", first.MaterialPath);
    }

    [Fact]
    public void RecipeValidatorRejectsTopologyGranularity()
    {
        var recipe = ValidRecipe() with
        {
            Operations = [ValidRecipe().Operations[0] with { Granularity = "connected_geometry" }],
        };

        var exception = Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(recipe));
        Assert.Equal("OPERATION_UNSUPPORTED", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    [Fact]
    public void RecipeValidatorAcceptsBoundedTransformInVersionTwo()
    {
        var recipe = ValidTransformRecipe();

        RecipeValidator.Validate(recipe);

        Assert.IsType<TransformComponentOperation>(recipe.Operations.Single());
    }

    [Fact]
    public void RecipeValidatorRejectsTransformInVersionOne()
    {
        var recipe = ValidTransformRecipe() with { SchemaVersion = 1 };

        var exception = Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(recipe));

        Assert.Equal("SCHEMA_OPERATION_MISMATCH", exception.Error.Code);
        Assert.Equal(ErrorCategory.CliOrSchema, exception.Error.Category);
    }

    [Fact]
    public void RecipeValidatorRejectsIdentityAndUnsafeTransformLimits()
    {
        var operation = (TransformComponentOperation)ValidTransformRecipe().Operations.Single();
        var identity = ValidTransformRecipe() with
        {
            Operations =
            [
                operation with
                {
                    Transform = operation.Transform with
                    {
                        UniformScale = 1f,
                        Translation = new TransformVector3(),
                    },
                },
            ],
        };
        var unsafeLimit = ValidTransformRecipe() with
        {
            Operations =
            [
                operation with
                {
                    Limits = new TransformLimits
                    {
                        MaximumVertexDisplacement = RecipeValidator.MaximumTransformDisplacement + 1f,
                    },
                },
            ],
        };

        Assert.Equal(
            "TRANSFORM_IDENTITY",
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(identity)).Error.Code);
        Assert.Equal(
            "TRANSFORM_LIMIT_OUT_OF_RANGE",
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(unsafeLimit)).Error.Code);
    }

    [Fact]
    public void RecipeValidatorRejectsNonFiniteTransformValuesAndMismatchedLods()
    {
        var operation = (TransformComponentOperation)ValidTransformRecipe().Operations.Single();
        var nonFinite = ValidTransformRecipe() with
        {
            Operations =
            [
                operation with
                {
                    Transform = operation.Transform with { UniformScale = float.NaN },
                },
            ],
        };
        var mismatchedLods = ValidTransformRecipe() with
        {
            Operations =
            [
                operation with
                {
                    ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 12 },
                },
            ],
        };

        Assert.Equal(
            "TRANSFORM_NON_FINITE",
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(nonFinite)).Error.Code);
        Assert.Equal(
            "LOD_EXPECTATION_MISMATCH",
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(mismatchedLods)).Error.Code);
    }

    [Fact]
    public void RecipeValidatorAcceptsConnectedComponentTransformAndRejectsDuplicateIds()
    {
        var operation = (TransformComponentOperation)ValidTransformRecipe().Operations.Single() with
        {
            Version = 3,
            Granularity = "connected_component_vertices",
            ConnectedComponentIdsByLod = new Dictionary<string, IReadOnlyList<string>>
            {
                ["0"] = ["cc_0123456789abcdef01234567"],
                ["1"] = ["cc_89abcdef0123456701234567"],
            },
        };
        var recipe = ValidTransformRecipe() with { SchemaVersion = 4, Operations = [operation] };

        RecipeValidator.Validate(recipe);

        var duplicate = recipe with
        {
            Operations =
            [
                operation with
                {
                    ConnectedComponentIdsByLod = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["0"] = ["cc_0123456789abcdef01234567", "cc_0123456789abcdef01234567"],
                        ["1"] = ["cc_89abcdef0123456701234567"],
                    },
                },
            ],
        };
        Assert.Equal(
            "CONNECTED_COMPONENT_SELECTION_INVALID",
            Assert.Throws<S2ModKitException>(() => RecipeValidator.Validate(duplicate)).Error.Code);
    }

    private static RecipeDocument ValidRecipe() => new()
    {
        SchemaVersion = 1,
        RecipeId = "remove-accessory",
        InputHash = ContentHash.Compute([1]),
        Operations =
        [
            new RemoveComponentOperation
            {
                OperationId = "remove-accessory",
                Selector = new ComponentSelector { Kind = "material_exact", MaterialPath = "materials/accessory.vmat" },
                ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 },
            },
        ],
    };

    private static RecipeDocument ValidTransformRecipe() => new()
    {
        SchemaVersion = 2,
        RecipeId = "scale-accessory",
        InputHash = ContentHash.Compute([2]),
        Operations =
        [
            new TransformComponentOperation
            {
                OperationId = "scale-accessory",
                Selector = new ComponentSelector
                {
                    Kind = "material_exact",
                    MaterialPath = "materials/accessory.vmat",
                },
                ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1, ["1"] = 1 },
                ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 12, ["1"] = 8 },
                Transform = new ComponentTransform
                {
                    Pivot = new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = 0 },
                    UniformScale = 1.2f,
                    Translation = new TransformVector3(),
                },
                Limits = new TransformLimits { MaximumVertexDisplacement = 32f },
            },
        ],
    };
}

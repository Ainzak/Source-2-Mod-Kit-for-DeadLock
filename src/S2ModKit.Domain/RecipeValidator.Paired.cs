namespace S2ModKit.Domain;

public static partial class RecipeValidator
{
    private static void ValidatePairedTransform(TransformComponentOperation operation)
    {
        if (operation.Transform is not null || operation.DirectionalTransform is not null || operation.CoordinatedTransform is not null
            || operation.Region is not null || operation.LocalTransform is not null || operation.PhysicsPolicy is not null
            || operation.ConnectedComponentIdsByLod is not null || operation.Limits is null || operation.Limits.MaximumCollisionDisplacement is not null
            || operation.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || operation.ZeroBoneBoxPolicy is not { Kind: "reject", Version: 1 } || operation.ZeroRenderSpherePolicy is not { Kind: "reject", Version: 1 }
            || operation.SourceTrianglePolicy is not { Kind: "preserve_source_coincidence", Version: 1 }
            || operation.ProceduralInputPolicy is not { Kind: "preserve_serialized_inputs_unverified", Version: 1 })
            throw PairedInvalid("Require isolated paired intent and all five explicit preservation/rejection policies.");
        var intent = operation.PairedTransform;
        if (intent?.Fields is not { Count: 2 } fields || fields.Any(f => f is null || f.FieldId is null || !IdentifierRegex().IsMatch(f.FieldId))
            || !fields.Select(f => f.FieldId).SequenceEqual(fields.Select(f => f.FieldId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw PairedInvalid("Require exactly two fields in unique ordinal fieldId order.");
        foreach (var field in fields)
        {
            if (field.Field?.Pivot is not { Kind: "explicit_point", Point: not null, ReferenceLod: null, Face: null, BoneName: null })
                throw PairedInvalid("Each paired field requires one explicit finite model-space point.");
            // Reuse the established field/member/protection domain, without reinterpreting its single-field operation.
            ValidateDirectionalTransform(operation with
            {
                Version = 9,
                PairedTransform = null,
                DirectionalTransform = new(intent.Members, field.Field, intent.Protection),
                SourceTrianglePolicy = null,
                ProceduralInputPolicy = null
            });
        }
    }

    private static S2ModKitException PairedInvalid(string message) => Errors.InvalidRecipe("PAIRED_INTENT_INVALID", message,
        "Use the explicit schema-11 pair, complete members/protection and acknowledged compatibility policies.");
}

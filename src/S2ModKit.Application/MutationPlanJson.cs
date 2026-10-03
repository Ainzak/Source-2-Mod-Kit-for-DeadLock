using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Dispatches persisted plans without changing the undiscriminated legacy wire shape.</summary>
public static class MutationPlanJson
{
    public static MutationPlan Read(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("A mutation plan must be an object.");
        if (EllipsoidContractJson.HasVersion(root, 4)) return EllipsoidContractJson.ReadPlan(root, json);
        EllipsoidContractJson.RejectOperationProperty(root, "ellipsoidTransformTarget");
        var discriminators = root.EnumerateObject().Where(property => property.Name == "schemaVersion").ToArray();
        if (discriminators.Length > 1) throw Invalid("The plan version discriminator is duplicated.");
        var experimental = discriminators.Length == 1;
        if (experimental && (discriminators[0].Value.ValueKind != JsonValueKind.Number
            || !discriminators[0].Value.TryGetInt32(out var version) || version is not (2 or 3)))
        {
            throw Errors.InvalidRecipe("PLAN_VERSION_UNSUPPORTED", "Only an absent legacy discriminator or experimental schemaVersion 2 is supported.", "Regenerate the plan using its original operation contract.");
        }

        if (experimental)
        {
            RequireUniqueProperties(root);
            RequireProperties(root, "recipeId", "inputHash", "fingerprint", "operations", "inputs");
            if (root.GetProperty("operations") is not { ValueKind: JsonValueKind.Array } operations
                || operations.GetArrayLength() != 1) throw Invalid("An experimental plan requires exactly one operation.");
            var operation = operations[0];
            RequireProperties(operation, "operationId", "kind", "version", "selectedDrawCalls", "targetBlocks", "experimentalTransformTarget");
            var target = operation.GetProperty("experimentalTransformTarget");
            RequireProperties(target, "structuralProfileId", "structuralProfileVersion", "boundsPolicyId", "boundsPolicyVersion",
                "runtimeMetadataPolicy", "selector", "pivotIntent", "pivot", "uniformScale", "maximumDisplacement", "displacementLimit",
                "geometryTargets", "boxTargets", "preservationTargets", "sourceBlocks");
            RequireProperties(target.GetProperty("runtimeMetadataPolicy"), "kind", "version");
            if (root.GetProperty("schemaVersion").GetInt32() == 3)
            {
                RequireProperties(target, "region");
                RequireRegionShape(target.GetProperty("region"));
            }
            else if (target.TryGetProperty("region", out _)) throw Invalid("Plan version 2 cannot contain region fields, including null.");
            foreach (var box in Array(target, "boxTargets"))
            {
                RequireProperties(box, "resourceBlockIndex", "fieldPath", "storage", "coordinateSpace", "coordinateMatrixHash",
                    "coordinateMatrixWords", "contributorSetHash", "contributorCount", "originalWords", "expectedWords", "growth");
            }

            foreach (var preserved in Array(target, "preservationTargets"))
            {
                RequireProperties(preserved, "resourceBlockIndex", "fieldPath", "category", "scope", "disposition", "sourcePayloadHash", "originalWords");
            }
        }

        var plan = JsonSerializer.Deserialize<MutationPlan>(json, JsonDefaults.Options)
            ?? throw Invalid("A mutation plan cannot be null.");
        if (plan.Operations is null || plan.Operations.Any(operation => operation is null)) throw Invalid("Plan operations are unavailable.");
        if (!experimental)
        {
            if (root.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array
                && operations.EnumerateArray().Any(operation => operation.ValueKind == JsonValueKind.Object
                    && operation.TryGetProperty("experimentalTransformTarget", out _))) throw Invalid("Legacy plans cannot contain experimental targets.");
            if (plan.Operations.Any(operation => operation.Kind == "remove_component" ? operation.Version != 1
                : operation.Kind != "transform_component" || operation.Version is < 1 or > 4)) throw Invalid("Legacy plans admit only published legacy operations.");
            return plan;
        }

        ValidateExperimental(plan);
        if (plan.Fingerprint != ComputeExperimentalFingerprint(plan))
        {
            throw Errors.Verification("PLAN_FINGERPRINT_DRIFT", "Experimental plan facts do not match their fingerprint.", "Regenerate the plan from immutable inputs.");
        }

        return plan;
    }

    /// <summary>All serialized semantic facts enter a canonical property-ordered digest; the digest itself does not.</summary>
    public static ContentHash ComputeExperimentalFingerprint(MutationPlan plan)
    {
        if (plan.SchemaVersion is not (2 or 3 or 4)) throw Invalid("Only experimental plan versions 2 through 4 use this fingerprint.");
        var facts = JsonSerializer.SerializeToElement(new { plan.SchemaVersion, plan.RecipeId, plan.InputHash, plan.Inputs, plan.Operations }, JsonDefaults.Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, facts);
        return ContentHash.Compute(stream.ToArray());
    }

    public static ContentHash ComputeEllipsoidTargetFingerprint(PlannedEllipsoidTransformTarget target)
    {
        var facts = JsonSerializer.SerializeToElement(target, JsonDefaults.Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in facts.EnumerateObject().Where(p => p.Name != "targetFingerprint").OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        return ContentHash.Compute(stream.ToArray());
    }

    internal static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid($"Duplicated contract property '{property.Name}'.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) RequireUniqueProperties(child);
        }
    }

    internal static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("A required contract object is unavailable.");
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) throw Invalid($"Required contract property '{name}' is unavailable.");
        }
    }

    private static JsonElement.ArrayEnumerator Array(JsonElement value, string name)
    {
        var array = value.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array) throw Invalid($"Required contract property '{name}' must be an array.");
        return array.EnumerateArray();
    }

    private static void ValidateExperimental(MutationPlan plan)
    {
        var operation = plan.Operations.Single();
        var target = operation.ExperimentalTransformTarget;
        var regionPlan = plan.SchemaVersion == 3;
        if (operation.Kind != "transform_component" || operation.Version != (regionPlan ? 6 : 5) || target is null
            || operation.GeometryTargets is not { Count: 0 } || operation.DistanceFieldTargets is not { Count: 0 }
            || operation.AffineTransformTarget is not null || operation.CoupledTransformTarget is not null
            || operation.SelectedDrawCalls is not { Count: > 0 } || operation.SelectedDrawCalls.Any(item => item is null)
            || operation.TargetBlocks is not { Count: > 0 } || operation.TargetBlocks.Any(item => item is null)
            || plan.Inputs is not { Count: > 0 } || plan.Inputs.Any(item => item is null)
            || target.StructuralProfileId != (regionPlan ? "root_owned_axis_ramp_visual_scale" : "root_complete_buffer_visual_uniform") || target.StructuralProfileVersion != 1
            || target.BoundsPolicyId != "retain_expand_boxes_preserve_runtime" || target.BoundsPolicyVersion != 1
            || target.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || target.Pivot is null || target.Selector is null || target.PivotIntent is null
            || target.GeometryTargets is not { Count: > 0 } || target.GeometryTargets.Any(item => item is null)
            || target.BoxTargets is not { Count: > 0 } || target.BoxTargets.Any(item => item is null)
            || target.PreservationTargets is not { Count: > 0 } || target.PreservationTargets.Any(item => item is null)
            || target.SourceBlocks is not { Count: > 0 } || target.SourceBlocks.Any(item => item is null)
            || !float.IsFinite(target.MaximumDisplacement) || target.MaximumDisplacement <= 0 || target.MaximumDisplacement > target.DisplacementLimit)
        {
            throw Invalid("The experimental plan is missing required profile, policy, geometry, box or preservation facts.");
        }
        if (regionPlan) ValidateRegion(target);
        else if (target.Region is not null) throw Invalid("Earlier plans cannot use region facts.");

        var expectations = operation.SelectedDrawCalls.GroupBy(call => call.Lod).ToDictionary(group => group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), group => group.Count());
        var vertices = target.GeometryTargets.GroupBy(item => item.Lod).ToDictionary(group => group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), group => group.Sum(item => item.SelectedVertexCount));
        RecipeValidator.Validate(new RecipeDocument
        {
            SchemaVersion = regionPlan ? 7 : 6,
            RecipeId = plan.RecipeId,
            InputHash = plan.InputHash,
            Operations = [new TransformComponentOperation
            {
                OperationId = operation.OperationId, Version = regionPlan ? 6 : 5, Granularity = regionPlan ? "axis_ramp_vertices" : "draw_call_vertices", Selector = target.Selector,
                Region = target.Region?.Selection,
                ExpectedMatchesByLod = expectations, ExpectedVerticesByLod = vertices, RuntimeMetadataPolicy = target.RuntimeMetadataPolicy,
                Transform = new ComponentTransform { Pivot = target.PivotIntent, UniformScale = target.UniformScale },
                Limits = new TransformLimits { MaximumVertexDisplacement = target.DisplacementLimit },
            }],
        });
        ValidateStorageFacts(plan, operation, target.GeometryTargets, target.BoxTargets, target.PreservationTargets, target.SourceBlocks);
    }

    internal static void ValidateStorageFacts(MutationPlan plan, PlannedOperation operation,
        IReadOnlyList<PlannedGeometryTarget> geometryTargets, IReadOnlyList<PlannedExperimentalBoxTarget> boxTargets,
        IReadOnlyList<PlannedExperimentalPreservationTarget> preservationTargets, IReadOnlyList<PlannedTargetBlock> sourceBlocks)
    {
        var target = new { GeometryTargets = geometryTargets, BoxTargets = boxTargets, PreservationTargets = preservationTargets, SourceBlocks = sourceBlocks };
        if (target.SourceBlocks.Select(block => block.Index).Distinct().Count() != target.SourceBlocks.Count
            || target.SourceBlocks.Any(block => block.Index < 0 || string.IsNullOrWhiteSpace(block.Type) || !ValidHash(block.InputHash))
            || operation.TargetBlocks.Select(block => block.Index).Distinct().Count() != operation.TargetBlocks.Count
            || operation.SelectedDrawCalls.Select(call => call.DrawCallId).Distinct(StringComparer.Ordinal).Count() != operation.SelectedDrawCalls.Count
            || target.GeometryTargets.Select(item => item.Lod).Distinct().Count() != target.GeometryTargets.Count
            || target.BoxTargets.Select(item => (item.ResourceBlockIndex, item.FieldPath)).Distinct().Count() != target.BoxTargets.Count
            || target.PreservationTargets.Select(item => (item.ResourceBlockIndex, item.FieldPath)).Distinct().Count() != target.PreservationTargets.Count
            || operation.SelectedDrawCalls.Any(call => !PortablePath(call.ResourcePath))
            || target.GeometryTargets.Any(item => !PortablePath(item.ResourcePath))
            || plan.Inputs.Any(item => item.Size <= 0 || !ValidHash(item.ContentHash) || !PortablePath(item.LogicalPath))
            || !plan.Inputs.Any(item => item.ContentHash == plan.InputHash))
        {
            throw Invalid("Frozen source, selection or target identities are missing or duplicated.");
        }

        var sources = target.SourceBlocks.ToDictionary(block => block.Index);
        if (operation.TargetBlocks.Any(block => !sources.TryGetValue(block.Index, out var source) || source != block)
            || target.GeometryTargets.Any(item => !sources.TryGetValue(item.ResourceBlockIndex, out var mesh) || mesh.Type != "MDAT"
                || !sources.TryGetValue(item.VertexResourceBlockIndex, out var vertex) || vertex.Type != "MVTX" || vertex.InputHash != item.VertexBlockInputHash
                || !sources.TryGetValue(item.IndexResourceBlockIndex, out var index) || index.Type != "MIDX" || index.InputHash != item.IndexBlockInputHash))
        {
            throw Invalid("A geometry or mutation target does not match the complete source-block inventory.");
        }

        ValidateMetadataFacts(target.BoxTargets, target.PreservationTargets, sources);
    }

    internal static void ValidateMetadataFacts(IReadOnlyList<PlannedExperimentalBoxTarget> boxTargets,
        IReadOnlyList<PlannedExperimentalPreservationTarget> preservationTargets, IReadOnlyDictionary<int, PlannedTargetBlock> sources)
    {
        var target = new { BoxTargets = boxTargets, PreservationTargets = preservationTargets };
        if (target.BoxTargets.Any(box => box.ResourceBlockIndex < 0 || string.IsNullOrWhiteSpace(box.FieldPath)
                || box.Storage is not ("min_max" or "center_half_extent") || string.IsNullOrWhiteSpace(box.CoordinateSpace)
                || box.CoordinateMatrixWords is not { Count: 12 } || box.ContributorCount < 1
                || box.OriginalWords is not { Count: 6 } || box.ExpectedWords is not { Count: 6 } || box.Growth is null
                || !ValidHash(box.CoordinateMatrixHash) || !ValidHash(box.ContributorSetHash)
                || !sources.TryGetValue(box.ResourceBlockIndex, out var source) || source.Type != "MDAT"
                || box.CoordinateMatrixWords.Any(word => !float.IsFinite(BitConverter.UInt32BitsToSingle(word)))
                || !ValidBoxWords(box.Storage, box.OriginalWords) || !ValidBoxWords(box.Storage, box.ExpectedWords)
                || box.Growth.Any(item => item is null || string.IsNullOrWhiteSpace(item.Measure)
                    || !double.IsFinite(item.OriginalValue) || !double.IsFinite(item.PlannedValue) || !double.IsFinite(item.Delta)))
            || target.PreservationTargets.Any(item => item.ResourceBlockIndex < 0 || string.IsNullOrWhiteSpace(item.FieldPath)
                || item.Category is not ("render_sphere" or "root_sphere" or "occlusion_distance_field" or "collision_payload")
                || item.Scope is not ("affected" or "resource") || item.Disposition != "preserve_unverified" || item.OriginalWords is null
                || !sources.TryGetValue(item.ResourceBlockIndex, out var source) || source.InputHash != item.SourcePayloadHash
                || source.Type != PreservationBlockType(item.Category)
                || (item.Category is "render_sphere" or "root_sphere"
                    ? item.OriginalWords.Count != 1 || !float.IsFinite(BitConverter.UInt32BitsToSingle(item.OriginalWords[0]))
                        || BitConverter.UInt32BitsToSingle(item.OriginalWords[0]) < 0f
                        || (item.Scope == "affected" && BitConverter.UInt32BitsToSingle(item.OriginalWords[0]) == 0f)
                    : item.OriginalWords.Count != 0)))
        {
            throw Invalid("A typed box or preservation target is malformed.");
        }
    }

    private static bool ValidHash(ContentHash hash) => hash.Value is { Length: ContentHash.HexLength };

    internal static void ValidateRegion(PlannedExperimentalTransformTarget target)
    {
        var region = target.Region;
        if (region?.Selection is null || region.Buffers is null || region.Buffers.Count != target.GeometryTargets.Count
            || region.Buffers.Any(buffer => buffer is null)
            || region.Buffers.Select(buffer => (buffer.Lod, buffer.MeshOrdinal, buffer.VertexBufferOrdinal)).Distinct().Count() != region.Buffers.Count)
            throw Invalid("Complete typed region facts are required in every LOD.");
        foreach (var buffer in region.Buffers)
        {
            var matches = target.GeometryTargets.Where(g => g.Lod == buffer.Lod && g.MeshOrdinal == buffer.MeshOrdinal && g.VertexBufferOrdinal == buffer.VertexBufferOrdinal).ToArray();
            if (matches.Length != 1 || buffer.PinnedVertexCount < 0 || buffer.TransitionVertexCount < 0 || buffer.FullVertexCount < 0
                || (long)buffer.PinnedVertexCount + buffer.TransitionVertexCount + buffer.FullVertexCount != matches[0].SelectedVertexCount
                || buffer.ChangedVertexCount <= 0 || buffer.ChangedVertexCount > matches[0].SelectedVertexCount - buffer.PinnedVertexCount
                || !ValidHash(buffer.MaskHash) || !ValidHash(buffer.InputPackedFrameHash) || !ValidHash(buffer.ExpectedPackedFrameHash)
                || buffer.PackedFrameLayout is not { Format: "R32_UINT", EncodingProfile: "source2_normal_tangent_v2" } layout
                || layout.Stride != matches[0].PositionLayout.Stride || layout.Offset < 0 || (long)layout.Offset + 4 > layout.Stride
                || (layout.Offset < (long)matches[0].PositionLayout.Offset + 12 && matches[0].PositionLayout.Offset < (long)layout.Offset + 4)
                || !matches[0].AllowedChangedAttributes.SequenceEqual(["normal_tangent", "position"]))
                throw Invalid("Region mask counts, identities or packed-frame facts are malformed.");
        }
    }

    internal static void RequireRegionShape(JsonElement region)
    {
        RequireProperties(region, "selection", "buffers");
        RequireProperties(region.GetProperty("selection"), "kind", "version", "axis", "pinnedThrough", "fullFrom");
        foreach (var buffer in Array(region, "buffers"))
        {
            RequireProperties(buffer, "lod", "meshOrdinal", "vertexBufferOrdinal", "maskHash", "pinnedVertexCount",
                "transitionVertexCount", "fullVertexCount", "changedVertexCount", "packedFrameLayout", "inputPackedFrameHash", "expectedPackedFrameHash");
            RequireProperties(buffer.GetProperty("packedFrameLayout"), "format", "offset", "stride", "encodingProfile");
        }
    }

    private static bool PortablePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/') && !path.Contains(':') && !path.Any(char.IsControl)
        && path == StableIdentity.NormalizePath(path) && !path.Split('/').Any(part => part is "" or "." or "..");

    private static string PreservationBlockType(string category) => category switch
    {
        "render_sphere" => "MDAT",
        "root_sphere" => "DATA",
        "occlusion_distance_field" => "DSTF",
        "collision_payload" => "PHYS",
        _ => string.Empty,
    };

    private static bool ValidBoxWords(string storage, IReadOnlyList<uint> words)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var first = BitConverter.UInt32BitsToSingle(words[axis]);
            var second = BitConverter.UInt32BitsToSingle(words[axis + 3]);
            if (!float.IsFinite(first) || !float.IsFinite(second) || (storage == "min_max" ? first >= second : second <= 0)) return false;
        }

        return true;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static S2ModKitException Invalid(string message) => Errors.InvalidRecipe("PLAN_CONTRACT_INVALID", message, "Regenerate the plan under its explicit operation and schema version.");
}

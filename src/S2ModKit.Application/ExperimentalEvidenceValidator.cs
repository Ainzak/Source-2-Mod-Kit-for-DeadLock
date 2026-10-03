using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Experimental publication requires both positive structural checks and explicit untested consumer obligations.</summary>
public static class ExperimentalEvidenceValidator
{
    private static readonly string[] RequiredChecks =
        ["experimental_reopen", "experimental_geometry", "experimental_box_policy", "experimental_metadata_preservation", "experimental_unchanged_data"];
    private static readonly string[] Risks = ["sphere_containment", "proxy_coherence", "collision_correspondence", "runtime"];

    public static IEnumerable<BoundaryEvidence> PlannedBoundaries() => RequiredChecks
        .Select(name => new BoundaryEvidence(name, "not_applicable", "Dry-run freezes targets but does not observe output bytes."))
        .Concat(Risks.Where(name => name != "runtime").Select(name => new BoundaryEvidence(name, "untested", "Intentional preservation is not consumer qualification.")));

    public static void Validate(EvidenceReport report)
    {
        var region = report.SchemaVersion == 8;
        var experimental = report.Operations.Any(operation => operation.Version is 5 or 6 || operation.ExperimentalTransform is not null);
        if (report.SchemaVersion is not (7 or 8))
        {
            if (experimental || report.SchemaVersion is < 1 or > 6) throw Invalid("Experimental evidence requires schema version 7.");
            return;
        }
        if (report.Operations is not [{ Kind: "transform_component", Version: 5 or 6, ExperimentalTransform: not null }]
            || report.Operations[0].Version != (region ? 6 : 5)
            || report.ProofLevel != "offline_static" || report.Status != "passed")
            throw Invalid("Schema 7 requires exactly one passed offline experimental operation.");
        var operation = report.Operations[0];
        var visual = operation.ExperimentalTransform!;
        if (visual.StructuralProfileId != (region ? "root_owned_axis_ramp_visual_scale" : "root_complete_buffer_visual_uniform") || visual.StructuralProfileVersion != 1
            || visual.BoundsPolicyId != "retain_expand_boxes_preserve_runtime" || visual.BoundsPolicyVersion != 1
            || visual.RuntimeMetadataPolicy is not { Kind: "preserve_unverified", Version: 1 }
            || visual.Boxes is not { Count: > 0 } || visual.PreservedMetadata is not { Count: > 0 }
            || operation.GeometryChanges is not { Count: > 0 } || report.Warnings.Count == 0)
            throw Invalid("The experimental profile, acknowledgement or measured targets are incomplete.");
        if (region && (visual.Region?.Selection is null || visual.Region.Buffers is not { Count: > 0 }
            || visual.Region.Buffers.Any(buffer => buffer is null || buffer.ChangedVertexCount <= 0)
            || visual.Region.Buffers.Count != operation.GeometryChanges.Count))
            throw Invalid("Region evidence requires the complete frozen mask and shading facts.");
        if (region)
        {
            var selection = visual.Region!.Selection;
            if (selection is not { Kind: "axis_ramp", Version: 1, Axis: "x" or "y" or "z" }
                || !float.IsFinite(selection.PinnedThrough) || !float.IsFinite(selection.FullFrom) || selection.PinnedThrough >= selection.FullFrom
                || visual.Region.Buffers.Select(b => (b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal)).Distinct().Count() != visual.Region.Buffers.Count)
                throw Invalid("Region selection or buffer identities are malformed.");
            foreach (var buffer in visual.Region.Buffers)
            {
                var geometry = operation.GeometryChanges.Where(g => g.Lod == buffer.Lod && g.MeshOrdinal == buffer.MeshOrdinal).ToArray();
                if (geometry.Length != 1 || buffer.VertexBufferOrdinal < 0 || buffer.PinnedVertexCount < 0 || buffer.TransitionVertexCount < 0 || buffer.FullVertexCount < 0
                    || (long)buffer.PinnedVertexCount + buffer.TransitionVertexCount + buffer.FullVertexCount != geometry[0].VertexCount
                    || buffer.ChangedVertexCount > geometry[0].VertexCount - buffer.PinnedVertexCount
                    || buffer.MaskHash.Value is not { Length: 64 } || buffer.InputPackedFrameHash.Value is not { Length: 64 } || buffer.ExpectedPackedFrameHash.Value is not { Length: 64 }
                    || buffer.PackedFrameLayout is not { Format: "R32_UINT", EncodingProfile: "source2_normal_tangent_v2" } layout
                    || layout.Offset < 0 || (long)layout.Offset + 4 > layout.Stride
                    || !geometry[0].ChangedAttributes.SequenceEqual(["normal_tangent", "position"]))
                    throw Invalid("Region geometry and frozen mask/frame facts disagree.");
            }
        }
        if (!region && visual.Region is not null) throw Invalid("Schema 7 cannot contain region facts.");
        foreach (var name in RequiredChecks.Concat(Risks))
        {
            var matches = report.Boundaries.Where(boundary => boundary.Name == name).ToArray();
            var expected = Risks.Contains(name) ? "untested" : report.Output is null ? "not_applicable" : "passed";
            if (matches.Length != 1 || matches[0].Status != expected)
                throw Invalid($"Required boundary '{name}' is missing, duplicated or incorrectly qualified.");
        }
        if (report.Boundaries.Any(boundary => boundary.Status == "failed")
            || report.Boundaries.Any(boundary => boundary.Status == "untested"
                && !Risks.Contains(boundary.Name) && boundary.Name != "source2_viewer"))
            throw Invalid("An unexpected failed or untested publication check is present.");
        var phase = report.Output is null ? "planned" : "passed";
        if (visual.Boxes.Any(box => box is null || box.Target is null || box.Status != phase
            || (report.Output is null ? box.ObservedWords is not null
                : box.ObservedWords is null || !box.ObservedWords.SequenceEqual(box.Target.ExpectedWords)))
            || visual.PreservedMetadata.Any(field => field is null || field.Target is null || field.Status != phase
                || (report.Output is null ? field.ObservedWords is not null || field.ObservedPayloadHash is not null
                    : field.ObservedPayloadHash is null || field.ObservedWords is null
                        || !field.ObservedWords.SequenceEqual(field.Target.OriginalWords)
                        || (field.Target.FieldPath == "$payload" && field.ObservedPayloadHash != field.Target.SourcePayloadHash))))
            throw Invalid("Passed evidence requires actual observed words and payload identities; dry runs cannot invent observations.");
    }

    internal static void ValidateShape(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        var versions = root.EnumerateObject().Where(property => property.Name == "schemaVersion").ToArray();
        var isNew = versions.Any(property => property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetInt32(out var version) && version is 7 or 8);
        if (!isNew)
        {
            if (root.TryGetProperty("operations", out var old) && old.ValueKind == JsonValueKind.Array
                && old.EnumerateArray().Any(operation => operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("experimentalTransform", out _)))
                throw Invalid("Legacy evidence cannot contain experimental fields, including null.");
            return;
        }
        MutationPlanJson.RequireUniqueProperties(root);
        MutationPlanJson.RequireProperties(root, "schemaVersion", "proofLevel", "status", "operations", "boundaries", "warnings", "input", "planFingerprint");
        var operations = root.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1) throw Invalid("One experimental operation is required.");
        var operation = operations[0];
        MutationPlanJson.RequireProperties(operation, "kind", "version", "geometryChanges", "experimentalTransform");
        var visual = operation.GetProperty("experimentalTransform");
        if (root.GetProperty("schemaVersion").GetInt32() == 8)
        {
            MutationPlanJson.RequireProperties(visual, "region");
            MutationPlanJson.RequireRegionShape(visual.GetProperty("region"));
        }
        else if (visual.TryGetProperty("region", out _)) throw Invalid("Schema 7 cannot contain region fields, including null.");
        MutationPlanJson.RequireProperties(visual, "structuralProfileId", "structuralProfileVersion", "boundsPolicyId", "boundsPolicyVersion",
            "runtimeMetadataPolicy", "pivot", "uniformScale", "displacementLimit", "boxes", "preservedMetadata");
        foreach (var name in new[] { "boxes", "preservedMetadata" })
            if (visual.GetProperty(name).ValueKind != JsonValueKind.Array) throw Invalid("Measured evidence targets must be arrays.");
        foreach (var box in visual.GetProperty("boxes").EnumerateArray())
        {
            MutationPlanJson.RequireProperties(box, "target", "status");
            if (!box.TryGetProperty("observedWords", out _)) throw Invalid("The observed-words property is required, even in a dry run.");
            MutationPlanJson.RequireProperties(box.GetProperty("target"), "resourceBlockIndex", "fieldPath", "storage", "coordinateSpace",
                "coordinateMatrixHash", "coordinateMatrixWords", "contributorSetHash", "contributorCount", "originalWords", "expectedWords", "growth");
        }
        foreach (var field in visual.GetProperty("preservedMetadata").EnumerateArray())
        {
            MutationPlanJson.RequireProperties(field, "target", "status");
            if (!field.TryGetProperty("observedPayloadHash", out _) || !field.TryGetProperty("observedWords", out _))
                throw Invalid("Observed preservation properties are required, even in a dry run.");
            MutationPlanJson.RequireProperties(field.GetProperty("target"), "resourceBlockIndex", "fieldPath", "category", "scope",
                "disposition", "sourcePayloadHash", "originalWords");
        }
    }

    private static S2ModKitException Invalid(string message) => Errors.Verification("EXPERIMENTAL_EVIDENCE_INVALID", message,
        "No experimental build/package may be published without complete observed evidence and explicit risk boundaries.");
}

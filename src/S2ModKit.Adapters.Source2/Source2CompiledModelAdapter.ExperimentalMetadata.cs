using System.Globalization;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static void AddExperimentalRenderSphereTargets(ParsedModel parsed, Source2AffineProfile profile,
        HashSet<int> affectedBones, List<PlannedExperimentalPreservationTarget> targets)
    {
        var bones = ExperimentalArray(ExperimentalCollection(profile.Mesh.Block.Data, "m_skeleton"), "m_bones");
        var hash = ContentHash.Compute(parsed.Envelope.Blocks[profile.Mesh.BlockIndex].Payload.Span);
        for (var index = 0; index < bones.Count; index++)
        {
            var radius = ExperimentalFloat(bones[index]["m_flSphereRadius"]);
            var affected = affectedBones.Contains(index);
            ValidateExperimentalRadius(radius, affected);
            targets.Add(new(profile.Mesh.BlockIndex, $"m_skeleton.m_bones[{index}].m_flSphereRadius", "render_sphere",
                affected ? "affected" : "resource", "preserve_unverified", hash, [BitConverter.SingleToUInt32Bits(radius)]));
        }
    }

    private static void AddExperimentalBlockPreservation(ParsedModel parsed, List<PlannedExperimentalPreservationTarget> targets)
    {
        string[] knownDistanceFieldKeys = ["m_nResX", "m_nResY", "m_nResZ", "m_quantizedData", "m_flGridCellSize",
            "m_flMaxQuantizedDistance", "m_flSurfaceBias", "m_bounds", "m_nParentBoneNameHash", "m_nBodyGroupIndex", "m_nBodyGroupChoice",
            "m_bIsTwoSided", "m_bIsFarFieldOnly", "m_bUseForOcclusion", "m_bUseForCollision"];
        foreach (var block in parsed.Envelope.Blocks.Where(block => block.Type is "DSTF" or "PHYS"))
        {
            if (block.Type == "DSTF")
            {
                if (parsed.Resource.Blocks[block.Index] is not BinaryKV3 data
                    || data.Data.Root.Children.Any(property => property.Key != "m_distanceFields"))
                    throw ExperimentalUnsupported("EXPERIMENTAL_DISTANCE_FIELD_UNSUPPORTED", "The distance-field root contains an unclassified family.");
                var fields = Source2TransformMetadataAnalyzer.ReadDistanceFields(data.Data.Root, block.Index, "experimental DSTF inventory");
                if (fields.Any(field => field.UseForCollision || !field.UseForOcclusion))
                    throw ExperimentalUnsupported("EXPERIMENTAL_COLLISION_DISTANCE_FIELD_UNSUPPORTED", "Only authored occlusion-only distance fields may be intentionally preserved.");
                foreach (var field in ExperimentalArray(data.Data.Root, "m_distanceFields").Values)
                {
                    if (!field.IsCollection || field.Children.Any(property => !knownDistanceFieldKeys.Contains(property.Key, StringComparer.Ordinal))
                        || ExperimentalCollection(field, "m_bounds").Children.Any(property => property.Key is not ("m_vMinBounds" or "m_vMaxBounds")))
                        throw ExperimentalUnsupported("EXPERIMENTAL_DISTANCE_FIELD_UNSUPPORTED", "An unclassified distance-field member cannot be waived by experimental preservation.");
                }
            }

            targets.Add(new(block.Index, "$payload", block.Type == "DSTF" ? "occlusion_distance_field" : "collision_payload",
                "resource", "preserve_unverified", ContentHash.Compute(block.Payload.Span), []));
        }
    }

    private static void ValidateExperimentalVertexStreams(Source2AffineProfile profile)
    {
        var descriptors = ExperimentalArray(profile.Mesh.Descriptor, "m_vertexBuffers");
        foreach (var descriptor in descriptors.Values)
        {
            var layout = ExperimentalArray(descriptor, "m_inputLayoutFields");
            var identities = new HashSet<(string Semantic, int Index)>();
            var ranges = new List<(int Start, int End)>();
            var stride = descriptor["m_nElementSizeInBytes"].ToInt32(CultureInfo.InvariantCulture);
            foreach (var field in layout.Values)
            {
                var semantic = field["m_pSemanticName"].ToString(CultureInfo.InvariantCulture).ToUpperInvariant();
                var format = field["m_Format"].ToUInt32(CultureInfo.InvariantCulture);
                var index = field["m_nSemanticIndex"].ToInt32(CultureInfo.InvariantCulture);
                var offset = field["m_nOffset"].ToInt32(CultureInfo.InvariantCulture);
                var slot = field["m_nSlot"].ToInt32(CultureInfo.InvariantCulture);
                var slotType = field["m_nSlotType"].ToString(CultureInfo.InvariantCulture);
                var width = (semantic, format) switch
                {
                    ("POSITION", 6u) => 12,
                    ("NORMAL", 42u) => 4,
                    ("TEXCOORD", 34u) => 4,
                    ("TEXCOORD", 37u) => 4, // R16G16_SNORM: two immutable 16-bit texture coordinates.
                    ("TEXCOORD", 16u) => 8,
                    ("COLOR", 28u) => 4,
                    ("BLENDINDICES", 30u) => 4,
                    ("BLENDINDICES", 12u) => 8,
                    ("BLENDINDICES", 4u) => 16,
                    ("BLENDWEIGHT", 28u) => 4,
                    ("BLENDWEIGHT", 11u) => 8,
                    _ => 0,
                };
                if (width == 0 || index < 0 || (semantic != "TEXCOORD" && index != 0) || !identities.Add((semantic, index))
                    || slot != 0 || slotType != "RENDER_SLOT_PER_VERTEX"
                    || (field.TryGetValue("m_nInstanceStepRate", out var step) && step.ToInt32(CultureInfo.InvariantCulture) != 0)
                    || offset < 0 || offset > stride - width || ranges.Any(range => offset < range.End && range.Start < offset + width))
                    throw ExperimentalUnsupported("EXPERIMENTAL_VERTEX_STREAM_UNSUPPORTED",
                        $"Vertex stream {semantic}[{index}] format {format}, offset {offset}, stride {stride}, " +
                        $"slot {slot}, kind {slotType} is unknown, overlapping or additional position-dependent data.");
                ranges.Add((offset, checked(offset + width)));
            }
        }
    }

    private static KVObject ExperimentalArray(KVObject parent, string name)
    {
        if (!parent.TryGetValue(name, out var value) || value is null || !value.IsArray)
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", $"Required array '{name}' is unavailable.");
        return value;
    }

    private static KVObject ExperimentalCollection(KVObject parent, string name)
    {
        if (!parent.TryGetValue(name, out var value) || value is null || !value.IsCollection)
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", $"Required object '{name}' is unavailable.");
        return value;
    }

    internal static float ExperimentalFloat(KVObject value)
    {
        if (value is null || value.ValueType is not (KVValueType.FloatingPoint or KVValueType.FloatingPoint64
            or KVValueType.Int32 or KVValueType.Int64 or KVValueType.UInt32 or KVValueType.UInt64))
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", "A serialized numeric field is unavailable.");
        var raw = value.ToDouble(CultureInfo.InvariantCulture);
        var result = (float)raw;
        if (!float.IsFinite(result) || (double)result != raw)
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", "A serialized field is not an exact finite binary32 value.");
        // A binary64 conversion can lose low integer bits before the float comparison above.
        // Compare integer sources with the exact integer represented by the binary32 result.
        if (value.ValueType is KVValueType.Int32 or KVValueType.Int64
            && new System.Numerics.BigInteger(result) != new System.Numerics.BigInteger(value.ToInt64(CultureInfo.InvariantCulture))
            || value.ValueType is KVValueType.UInt32 or KVValueType.UInt64
            && new System.Numerics.BigInteger(result) != new System.Numerics.BigInteger(value.ToUInt64(CultureInfo.InvariantCulture)))
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", "A serialized integer is not exactly represented by binary32.");
        return result;
    }

    private static Point3 ExperimentalVector(KVObject parent, string name)
    {
        var array = ExperimentalArray(parent, name);
        if (array.Count != 3) throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", "A box vector must have three float words.");
        if (array.Values.Any(value => value.ValueType is not (KVValueType.FloatingPoint or KVValueType.FloatingPoint64)))
            throw ExperimentalUnsupported("EXPERIMENTAL_METADATA_UNSUPPORTED", "Mutable box vectors require characterized floating-point storage.");
        return new(ExperimentalFloat(array[0]), ExperimentalFloat(array[1]), ExperimentalFloat(array[2]));
    }

    private static void ValidateExperimentalRadius(float radius, bool affected)
    {
        if (!float.IsFinite(radius) || radius < 0 || (affected && radius == 0))
            throw ExperimentalUnsupported("EXPERIMENTAL_SPHERE_LAYOUT_UNSUPPORTED", "Sphere words must be finite and affected radii positive; their spaces remain unverified.");
    }
}

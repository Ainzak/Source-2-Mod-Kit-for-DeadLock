using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    /// <summary>
    /// A new-operation admission primitive only. Existing box planners do not call it.
    /// The caller must rediscover complete skinning/remap/root/procedural evidence first.
    /// </summary>
    internal static CoordinatedZeroBoxSource ReadCoordinatedZeroBoxSource(
        KVObject rawBox, IReadOnlyList<int> completeContributors, IReadOnlyList<Point3> allBeforePositions,
        KVObject rawInverseBind, IReadOnlyList<float> characterizedInverseBind, float renderSphereRadius,
        bool hasProceduralDependency, ZeroBoneBoxPolicy? policy,
        KVObject? rawRenderSphere = null, ZeroRenderSpherePolicy? zeroRenderSpherePolicy = null)
    {
        if (policy is not { Kind: "preserve_all_zero_single_contributor_unverified", Version: 1 })
            throw CoordinatedZeroUnsupported("Explicit preserve_all_zero_single_contributor_unverified@1 acknowledgement is required.");
        if (rawBox is null || !rawBox.IsCollection
            || rawBox.Children.Any(property => property.Key is not ("m_vecCenter" or "m_vecSize")))
            throw CoordinatedZeroUnsupported("The zero-box storage family is unknown.");
        var center = ExperimentalArray(rawBox, "m_vecCenter");
        var size = ExperimentalArray(rawBox, "m_vecSize");
        if (center.Count != 3 || size.Count != 3)
            throw CoordinatedZeroUnsupported("Exactly six typed binary32 positive-zero words are required.");
        var fields = center.Values.Concat(size.Values).ToArray();
        // Reject integer zero, binary64 zero, -0, a flat regular box and any
        // nonzero center. Numeric conversion cannot erase this storage boundary.
        if (fields.Any(field => field.ValueType != KVValueType.FloatingPoint
            || BitConverter.SingleToUInt32Bits(ExperimentalFloat(field)) != 0))
            throw CoordinatedZeroUnsupported("Exactly six typed binary32 positive-zero words are required.");
        if (completeContributors is not { Count: 1 } || allBeforePositions is null
            || (uint)completeContributors[0] >= (uint)allBeforePositions.Count || hasProceduralDependency)
            throw CoordinatedZeroUnsupported("One complete ordinary nonzero-influence contributor and no procedural dependency are required.");
        if (rawInverseBind is null || !rawInverseBind.IsArray || rawInverseBind.Count != 12
            || characterizedInverseBind is not { Count: 12 })
            throw CoordinatedZeroUnsupported("A complete characterized inverse-bind matrix is required.");
        var matrix = rawInverseBind.Values.Select(ExperimentalFloat).ToArray();
        if (!matrix.Select(BitConverter.SingleToUInt32Bits).SequenceEqual(characterizedInverseBind.Select(BitConverter.SingleToUInt32Bits)))
            throw CoordinatedZeroUnsupported("Serialized and characterized inverse-bind words disagree.");
        CoordinatedZeroRenderSphereSource? zeroSphere = null;
        if (renderSphereRadius == 0)
            zeroSphere = ReadCoordinatedZeroRenderSphereSource(rawRenderSphere, zeroRenderSpherePolicy);
        else ValidateExperimentalRadius(renderSphereRadius, affected: true);
        // Finite model-space contributor plus the unchanged characterized affine
        // map must remain finitely enclosable; no geometry-derived box is generated.
        _ = AffinePointEnclosure.Enclose(allBeforePositions[completeContributors[0]], matrix);
        return new(Words(0f, 0f, 0f, 0f, 0f, 0f), Words(matrix), completeContributors[0], zeroSphere);
    }

    private static CoordinatedZeroRenderSphereSource ReadCoordinatedZeroRenderSphereSource(
        KVObject? radius, ZeroRenderSpherePolicy? policy)
    {
        if (policy is not { Kind: "preserve_zero_render_sphere_with_zero_box_unverified", Version: 1 }
            || radius is null || radius.ValueType is not (KVValueType.FloatingPoint or KVValueType.FloatingPoint64))
            throw CoordinatedZeroUnsupported("The paired zero render sphere needs its own explicit acknowledgement and characterized floating-point storage.");
        // This is called only after the six-f32-+0/single-contributor box passes.
        // Freeze the actual serialized type and IEEE word; do not narrow away -0
        // or a tiny binary64 value. Root spheres retain their finite-positive gate.
        var raw = radius.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
        if (BitConverter.DoubleToUInt64Bits(raw) != 0)
            throw CoordinatedZeroUnsupported("Only a typed positive-zero render sphere paired with an admitted zero box may be preserved.");
        return new(radius.ValueType == KVValueType.FloatingPoint ? "binary32" : "binary64", 0);
    }

    private static S2ModKitException CoordinatedZeroUnsupported(string message) =>
        Errors.Unsupported("COORDINATED_ZERO_BOX_UNSUPPORTED", message,
            "Use only the separately acknowledged six-positive-zero, single-contributor profile; no box repair or old-writer fallback is permitted.");
}

internal sealed record CoordinatedZeroBoxSource(uint[] OriginalWords, uint[] MatrixWords, int ContributorMeshVertexIndex,
    CoordinatedZeroRenderSphereSource? ZeroRenderSphere);

internal sealed record CoordinatedZeroRenderSphereSource(string Storage, ulong OriginalWord);

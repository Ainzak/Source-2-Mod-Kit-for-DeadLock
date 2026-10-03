namespace S2ModKit.Geometry;

/// <summary>
/// Encloses the exact real-valued affine image of serialized binary32 words.
/// This numerical primitive does not establish a resource's coordinate frame or contributor set.
/// </summary>
public static class AffinePointEnclosure
{
    /// <summary>
    /// Uses three row-major rows of four coefficients: x, y, z, translation.
    /// Float products are exact in binary64 (at most 48 significant bits); additions execute
    /// left to right, without fused multiply-add, reassociation or an asset-fitted epsilon.
    /// Each addition uses an error-free TwoSum residual to round the interval outwards only
    /// when required. Final endpoints round outwards to binary32. Unrepresentable enclosures
    /// reject, even if cancellation might make the exact result representable.
    /// </summary>
    public static Bounds3 Enclose(Point3 point, IReadOnlyList<float> rowMajor3x4)
    {
        ArgumentNullException.ThrowIfNull(rowMajor3x4);
        if (rowMajor3x4.Count != 12)
        {
            throw new ArgumentException("An affine enclosure requires exactly twelve coefficients.", nameof(rowMajor3x4));
        }

        // Freeze caller-supplied words before arithmetic, including validation of unused zeros.
        Span<float> matrix = stackalloc float[12];
        for (var index = 0; index < matrix.Length; index++)
        {
            var value = rowMajor3x4[index];
            ScalarValidation.RequireFinite(value, nameof(rowMajor3x4));
            matrix[index] = value;
        }

        Span<float> minimum = stackalloc float[3];
        Span<float> maximum = stackalloc float[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var row = axis * 4;
            var x = (double)matrix[row] * point.X;
            var y = (double)matrix[row + 1] * point.Y;
            var z = (double)matrix[row + 2] * point.Z;
            var low = AddDirected(x, y, upper: false);
            var high = AddDirected(x, y, upper: true);
            low = AddDirected(low, z, upper: false);
            high = AddDirected(high, z, upper: true);
            low = AddDirected(low, matrix[row + 3], upper: false);
            high = AddDirected(high, matrix[row + 3], upper: true);
            minimum[axis] = EncodeDirected(low, upper: false);
            maximum[axis] = EncodeDirected(high, upper: true);
        }

        return new Bounds3(new(minimum[0], minimum[1], minimum[2]), new(maximum[0], maximum[1], maximum[2]));
    }

    private static double AddDirected(double left, double right, bool upper)
    {
        // Knuth TwoSum: left + right equals sum + error exactly in this bounded exponent range.
        // Binary32 products and their four-term sums cannot overflow or underflow binary64;
        // neither can the residuals. Do not replace this with FastTwoSum (no magnitude ordering).
        var sum = left + right;
        var virtualRight = sum - left;
        var virtualLeft = sum - virtualRight;
        var rightError = right - virtualRight;
        var leftError = left - virtualLeft;
        var error = leftError + rightError;
        return upper
            ? error > 0 ? Math.BitIncrement(sum) : sum
            : error < 0 ? Math.BitDecrement(sum) : sum;
    }

    private static float EncodeDirected(double value, bool upper)
    {
        var result = (float)value;
        if (upper ? result < value : result > value)
        {
            result = upper ? MathF.BitIncrement(result) : MathF.BitDecrement(result);
        }

        if (!float.IsFinite(result))
        {
            throw new ArgumentException("The conservative affine enclosure is not representable by finite float endpoints.");
        }

        return result;
    }
}

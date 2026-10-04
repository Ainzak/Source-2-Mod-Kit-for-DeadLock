using System.Numerics;

namespace S2ModKit.Geometry.Tests;

public sealed class TiltedRampScaleTests
{
    private static readonly TangentFrame Frame = new(new(0, 1, 0), new(1, 0, 0), -1);

    [Fact]
    public void ExactEndpointsPreservePinnedWordsAndEstablishedFullArithmetic()
    {
        var field = new TiltedRampScale(0, 1, 2, 1, 0, 8, 1.5f, default, 64);
        var pinned = new Point3(-0f, float.Epsilon, -0f);
        var result = field.Evaluate(pinned);
        Assert.Equal(TiltedRampMembership.Pinned, result.Membership);
        AssertWords(pinned, result.Position);
        Assert.Equal(0, result.MaximumDisplacement);
        Assert.Same(Frame, field.TransformFrame(pinned, Frame));
        var full = new Point3(3, -0f, 5);
        Assert.Equal(TiltedRampMembership.Full, field.Evaluate(full).Membership);
        AssertWords(new UniformTransform(default, 1.5f, default).Apply(full), field.Evaluate(full).Position);
        Assert.Same(Frame, field.TransformFrame(full, Frame));
    }

    [Fact]
    public void CancellationAndRoundedEndpointWeightsCannotChangeMembership()
    {
        var field = new TiltedRampScale(0, 1, 2, 1, 1, 2, 1.5f, new(1, 0, 0), 64);
        Assert.Equal(TiltedRampMembership.Pinned, field.Evaluate(new(1, -0f, -float.Epsilon)).Membership);
        Assert.Equal(TiltedRampMembership.Transition, field.Evaluate(new(1, -0f, float.Epsilon)).Membership);
        var belowFull = field.Evaluate(new(2, 0, -float.Epsilon));
        Assert.Equal(TiltedRampMembership.Transition, belowFull.Membership);
        Assert.Equal(1, belowFull.Weight); // Rounded t=1 must not make this a full member.
        Assert.Equal(TiltedRampMembership.Full, field.Evaluate(new(2, 0, float.Epsilon)).Membership);
        var cancellation = new TiltedRampScale(0, 1, 2, -1, 0, float.Epsilon * 8, 1.5f, default, 64);
        var negativeZero = new Point3(-0f, 1, 0);
        AssertWords(negativeZero, cancellation.Evaluate(negativeZero).Position);
        Assert.Equal(TiltedRampMembership.Transition, cancellation.Evaluate(new(float.Epsilon * 5, 1, float.Epsilon)).Membership);
    }

    [Fact]
    public void ExactCertificateAdmitsEqualityAndRejectsAdverseGlobalFields()
    {
        var boundary = new TiltedRampScale(0, 1, 2, 1, 0, 6, 1.5f, new(7, 0, 0), 64);
        Assert.Equal("1", boundary.Certificate.Numerator);
        Assert.Equal("8", boundary.Certificate.Denominator);
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 6, 1.5f, new(MathF.BitIncrement(7), 0, 0), 64));
        var safe = new TiltedRampScale(0, 1, 2, 1, 97, 102, 1.5f, new(-3, 0, 100), 64);
        Assert.Equal(new TiltedRampCertificate("1", "1"), safe.Certificate);
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 1, 2, new(100, 0, 0), 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 1, 0.5f, new(-100, 0, 0), 64));
    }

    [Fact]
    public void InvalidIntentFramesOverflowAndStoredDisplacementReject()
    {
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 0, 1, 0, 8, 1.5f, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(2, 1, 0, 1, 0, 8, 1.5f, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(-1, 1, 2, 1, 0, 8, 1.5f, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 0, 2, 1, 0, 8, 1.5f, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 2, 0, 8, 1.5f, default, 64));
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, invalid, 8, 1.5f, default, 64));
            Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, invalid, 1.5f, default, 64));
            Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 8, invalid, default, 64));
            Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 8, 1.5f, default, invalid));
        }
        foreach (var scale in new[] { 0.49f, 1, 2.01f })
            Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 8, scale, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 8, 8, 1.5f, default, 64));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 8, 1.5f, default, 0));
        Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, 0, 8, 1.5f, default, 65));
        var field = new TiltedRampScale(0, 1, 2, 1, 0, 8, 2, default, 64);
        Assert.Throws<ArgumentException>(() => field.Evaluate(new(float.MaxValue, 0, 8)));
        Assert.Throws<ArgumentException>(() => field.Evaluate(new(8, 1000, 0)));
        foreach (var bad in new[] { Frame with { Handedness = 0 }, Frame with { Normal = default }, Frame with { Tangent = Frame.Normal } })
            Assert.Throws<ArgumentException>(() => field.TransformFrame(new(1, 2, 3), bad));
        var capped = new TiltedRampScale(0, 1, 2, 1, 0, 8, 1.5f, default, 0.5f);
        Assert.Throws<ArgumentException>(() => capped.Evaluate(new(4, 0, 4)));
    }

    [Fact]
    public void TenThousandCasesMatchIndependentSoftwareRoundedRationalArithmetic()
    {
        var random = new Random(33104);
        for (var i = 0; i < 10_000; i++)
        {
            var first = i % 3 == 0 ? 1 : 0;
            var second = i % 3 == 2 ? 1 : 2;
            var sign0 = (i & 1) == 0 ? 1 : -1;
            var sign1 = (i & 2) == 0 ? 1 : -1;
            var scale = (i & 4) == 0 ? 1.5f : 0.75f;
            var pivot = new Point3(0, 0, 0);
            var field = new TiltedRampScale(first, sign0, second, sign1, -3, 9, scale, pivot, 64);
            // Diverse non-dyadic t plus source coordinates with more than 53 bits
            // between exponents. Software IEEE rounding below shares no production math.
            float[] xyz = [random.Next(-2048, 2049) / 128f, random.Next(-2048, 2049) / 128f, random.Next(-2048, 2049) / 128f];
            if (i % 11 == 0) xyz[second] = MathF.ScaleB((float)random.Next(-127, 128), -120);
            var point = new Point3(xyz[0], xyz[1], xyz[2]);
            var q = (Fraction.FromFloat(xyz[first]) * new Fraction(sign0, 1)) + (Fraction.FromFloat(xyz[second]) * new Fraction(sign1, 1));
            var membership = q.Compare(new(-3, 1)) <= 0 ? TiltedRampMembership.Pinned
                : q.Compare(new(9, 1)) >= 0 ? TiltedRampMembership.Full : TiltedRampMembership.Transition;
            var weight = membership == TiltedRampMembership.Full ? 1d : 0d;
            var actual = field.Evaluate(point);
            Assert.Equal(membership, actual.Membership);
            if (membership == TiltedRampMembership.Transition)
            {
                var t = RoundDouble((q + new Fraction(3, 1)) / new Fraction(12, 1));
                weight = Multiply(Multiply(t, t), Subtract(3, Multiply(2, t)));
                var a = Add(1, Multiply(Subtract(scale, 1), weight));
                AssertWords(new(RoundFloat(Fraction.FromDouble(Multiply(a, xyz[0]))),
                    RoundFloat(Fraction.FromDouble(Multiply(a, xyz[1]))),
                    RoundFloat(Fraction.FromDouble(Multiply(a, xyz[2])))), actual.Position);
            }
            else if (membership == TiltedRampMembership.Pinned) AssertWords(point, actual.Position);
            else
            {
                // Software binary32 multiply and add, independent of UniformTransform.
                AssertWords(new(RoundFloat(Fraction.FromFloat(xyz[0]) * Fraction.FromFloat(scale)),
                    RoundFloat(Fraction.FromFloat(xyz[1]) * Fraction.FromFloat(scale)),
                    RoundFloat(Fraction.FromFloat(xyz[2]) * Fraction.FromFloat(scale))), actual.Position);
            }
            Assert.Equal(BitConverter.DoubleToUInt64Bits(weight), BitConverter.DoubleToUInt64Bits(actual.Weight));
        }
    }

    [Fact]
    public void DifferentialFramesMatchIndependentFullMatrixInverseForEverySignedAxisPair()
    {
        var random = new Random(33105);
        for (var i = 0; i < 1200; i++)
        {
            var first = i % 3 == 0 ? 1 : 0;
            var second = i % 3 == 2 ? 1 : 2;
            var sign0 = (i & 1) == 0 ? 1 : -1;
            var sign1 = (i & 2) == 0 ? 1 : -1;
            var scale = (i & 4) == 0 ? 1.5f : 0.75f;
            var field = new TiltedRampScale(first, sign0, second, sign1, -8, 8, scale, default, 64);
            double[] p = [random.Next(-127, 128) / 32d, random.Next(-127, 128) / 32d, random.Next(-127, 128) / 32d];
            var point = new Point3((float)p[0], (float)p[1], (float)p[2]);
            var frame = i % 2 == 0 ? Frame : new TangentFrame(new(1, 0, 0), new(0, 0, 1), 1);
            var actual = field.TransformFrame(point, frame);
            var t = ((sign0 * p[first]) + (sign1 * p[second]) + 8) / 16;
            // Independent polynomial + dense matrix/cofactor inverse; no rank-one inverse.
            var a = 1 + ((scale - 1) * ((3 * t * t) - (2 * t * t * t)));
            var slope = (scale - 1) * 6 * t * (1 - t) / 16;
            double[] v = [0, 0, 0]; v[first] = sign0; v[second] = sign1;
            var matrix = new double[3, 3];
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++) matrix[row, col] = (row == col ? a : 0) + (slope * p[row] * v[col]);
            var cofactors = new double[3, 3];
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 3; col++)
                {
                    var rows = Enumerable.Range(0, 3).Where(r => r != row).ToArray();
                    var cols = Enumerable.Range(0, 3).Where(c => c != col).ToArray();
                    cofactors[row, col] = (((row + col) & 1) == 0 ? 1 : -1)
                        * ((matrix[rows[0], cols[0]] * matrix[rows[1], cols[1]]) - (matrix[rows[0], cols[1]] * matrix[rows[1], cols[0]]));
                }
            var det = Enumerable.Range(0, 3).Sum(c => matrix[0, c] * cofactors[0, c]);
            double[] n = [frame.Normal.X, frame.Normal.Y, frame.Normal.Z];
            double[] tangent = [frame.Tangent.X, frame.Tangent.Y, frame.Tangent.Z];
            var expectedN = Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 3).Sum(c => cofactors[r, c] * n[c]) / det).ToArray();
            Normalize(expectedN);
            var expectedT = Enumerable.Range(0, 3).Select(r => Enumerable.Range(0, 3).Sum(c => matrix[r, c] * tangent[c])).ToArray();
            var projection = expectedT.Zip(expectedN, (x, y) => x * y).Sum();
            for (var j = 0; j < 3; j++) expectedT[j] -= projection * expectedN[j];
            Normalize(expectedT);
            AssertDirection(expectedN, actual.Normal);
            AssertDirection(expectedT, actual.Tangent);
            Assert.Equal(frame.Handedness, actual.Handedness);
        }
    }

    [Fact]
    public void GlobalCertificateAgreesWithIndependentRationalBoundIncludingAdjacentFloats()
    {
        var random = new Random(33106);
        for (var i = 0; i < 1500; i++)
        {
            var scale = i % 2 == 0 ? 1.5f : 0.75f;
            var pivotX = random.Next(-512, 513) / 32f;
            if (i % 5 == 0) pivotX = MathF.BitIncrement(pivotX);
            var pivotZ = random.Next(-128, 129) / 32f;
            var qPivot = Fraction.FromFloat(pivotX) + Fraction.FromFloat(pivotZ);
            var adverse = scale > 1 ? qPivot + new Fraction(3, 1) : new Fraction(9, 1) - qPivot;
            if (adverse.N.Sign < 0) adverse = new(0, 1);
            var bound = Fraction.FromFloat(Math.Min(1, scale)) - (Fraction.FromFloat(Math.Abs(scale - 1)) * new Fraction(3, 2) * adverse / new Fraction(12, 1));
            if (bound.Compare(new(1, 8)) < 0)
                Assert.Throws<ArgumentException>(() => new TiltedRampScale(0, 1, 2, 1, -3, 9, scale, new(pivotX, 0, pivotZ), 64));
            else
            {
                var field = new TiltedRampScale(0, 1, 2, 1, -3, 9, scale, new(pivotX, 0, pivotZ), 64);
                Assert.Equal(bound.N.ToString(System.Globalization.CultureInfo.InvariantCulture), field.Certificate.Numerator);
                Assert.Equal(bound.D.ToString(System.Globalization.CultureInfo.InvariantCulture), field.Certificate.Denominator);
            }
        }
    }

    private static void AssertWords(Point3 expected, Point3 actual)
    {
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.X), BitConverter.SingleToUInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Y), BitConverter.SingleToUInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Z), BitConverter.SingleToUInt32Bits(actual.Z));
    }
    private static void Normalize(double[] v)
    {
        var length = Math.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < 3; i++) v[i] /= length;
    }
    private static void AssertDirection(double[] expected, Point3 actual) =>
        Assert.InRange((expected[0] * actual.X) + (expected[1] * actual.Y) + (expected[2] * actual.Z), 0.9999999, 1.0000001);

    private static double Add(double a, double b) => RoundDouble(Fraction.FromDouble(a) + Fraction.FromDouble(b));
    private static double Subtract(double a, double b) => RoundDouble(Fraction.FromDouble(a) - Fraction.FromDouble(b));
    private static double Multiply(double a, double b) => RoundDouble(Fraction.FromDouble(a) * Fraction.FromDouble(b));
    private static double RoundDouble(Fraction value) => Round(value, 52, -1074);
    private static float RoundFloat(Fraction value) => (float)Round(value, 23, -149);

    // Independent integer significand/remainder rounding, with no production bit search.
    private static double Round(Fraction value, int fractionBits, int minimumUnit)
    {
        if (value.N.IsZero) return 0;
        var n = BigInteger.Abs(value.N);
        var exponent = checked((int)(n.GetBitLength() - value.D.GetBitLength()));
        if ((exponent >= 0 ? n.CompareTo(value.D << exponent) : (n << -exponent).CompareTo(value.D)) < 0) exponent--;
        var unit = Math.Max(minimumUnit, exponent - fractionBits);
        var scaledN = unit < 0 ? n << -unit : n;
        var scaledD = unit < 0 ? value.D : value.D << unit;
        var significand = BigInteger.DivRem(scaledN, scaledD, out var remainder);
        var midpoint = (remainder * 2).CompareTo(scaledD);
        if (midpoint > 0 || (midpoint == 0 && !significand.IsEven)) significand++;
        var result = Math.ScaleB((double)significand, unit);
        return value.N.Sign < 0 ? -result : result;
    }

    private readonly record struct Fraction
    {
        public Fraction(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
            var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
            N = numerator / gcd; D = denominator / gcd;
        }
        public BigInteger N { get; }
        public BigInteger D { get; }
        public int Compare(Fraction b) => (N * b.D).CompareTo(b.N * D);
        public static Fraction operator +(Fraction a, Fraction b) => new((a.N * b.D) + (b.N * a.D), a.D * b.D);
        public static Fraction operator -(Fraction a, Fraction b) => new((a.N * b.D) - (b.N * a.D), a.D * b.D);
        public static Fraction operator *(Fraction a, Fraction b) => new(a.N * b.N, a.D * b.D);
        public static Fraction operator /(Fraction a, Fraction b) => new(a.N * b.D, a.D * b.N);
        public static Fraction FromFloat(float value)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            var exponent = (int)((bits >> 23) & 255);
            BigInteger n = bits & 0x7fffff;
            if (exponent != 0) n += 1 << 23;
            if (bits >> 31 != 0) n = -n;
            var shift = exponent == 0 ? -149 : exponent - 150;
            return shift >= 0 ? new(n << shift, 1) : new(n, BigInteger.One << -shift);
        }
        public static Fraction FromDouble(double value)
        {
            var bits = BitConverter.DoubleToUInt64Bits(value);
            var exponent = (int)((bits >> 52) & 2047);
            BigInteger n = bits & 0x000fffffffffffffUL;
            if (exponent != 0) n += BigInteger.One << 52;
            if (bits >> 63 != 0) n = -n;
            var shift = exponent == 0 ? -1074 : exponent - 1075;
            return shift >= 0 ? new(n << shift, 1) : new(n, BigInteger.One << -shift);
        }
    }
}

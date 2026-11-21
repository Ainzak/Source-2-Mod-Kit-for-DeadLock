using System.Buffers.Binary;

namespace S2ModKit.Adapters.Preview;

/// <summary>
/// Evaluates a compiled material expression (a material's <c>m_dynamicParams</c> value) at a time, for previews. Only the small
/// stack language of time-driven expressions is understood: <c>07</c> pushes a 32-bit float, <c>06 nn nn</c> calls a function
/// (<c>sin</c> 0, <c>cos</c> 1, <c>time()</c> 0x1B), <c>13</c> adds, <c>14</c> subtracts, <c>15</c> multiplies, <c>16</c> divides,
/// <c>18</c> negates, <c>00</c> ends. Anything else (branches, variables, other functions) gives null: the caller keeps the
/// material's fixed value.
/// </summary>
public static class MaterialExpression
{
    public static float? Evaluate(ReadOnlySpan<byte> program, float seconds)
    {
        var stack = new Stack<float>();
        var at = 0;
        while (at < program.Length)
        {
            var op = program[at++];
            switch (op)
            {
                case 0x00:
                    return stack.Count == 1 && float.IsFinite(stack.Peek()) ? stack.Pop() : null;
                case 0x07 when at + 4 <= program.Length:
                    stack.Push(BinaryPrimitives.ReadSingleLittleEndian(program[at..]));
                    at += 4;
                    break;
                case 0x06 when at + 2 <= program.Length:
                    var function = BinaryPrimitives.ReadUInt16LittleEndian(program[at..]);
                    at += 2;
                    switch (function)
                    {
                        case 0x1B:
                            stack.Push(seconds);
                            break;
                        case 0x00 when stack.Count > 0:
                            stack.Push(MathF.Sin(stack.Pop()));
                            break;
                        case 0x01 when stack.Count > 0:
                            stack.Push(MathF.Cos(stack.Pop()));
                            break;
                        default:
                            return null;
                    }

                    break;
                case 0x18 when stack.Count > 0:
                    stack.Push(-stack.Pop());
                    break;
                case >= 0x13 and <= 0x16 when stack.Count > 1:
                    var right = stack.Pop();
                    var left = stack.Pop();
                    stack.Push(op switch
                    {
                        0x13 => left + right,
                        0x14 => left - right,
                        0x15 => left * right,
                        _ => left / right,
                    });
                    break;
                default:
                    return null;
            }
        }

        return null;                                                       // no end marker
    }

    /// <summary>True when the expression reads the time (it changes while the preview plays).</summary>
    public static bool UsesTime(byte[] program) =>
        Evaluate(program, 0f) is { } start && Enumerable.Range(1, 8).Any(step => Evaluate(program, step * 0.37f) is { } later && Math.Abs(later - start) > 1e-5f);
}

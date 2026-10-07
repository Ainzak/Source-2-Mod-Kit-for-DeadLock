using System.Reflection;
using System.Reflection.Emit;
using S2ModKit.Adapters.Source2;
using S2ModKit.Geometry;

namespace S2ModKit.Architecture.Tests;

public sealed class PairedVerifierIndependenceTests
{
    [Fact]
    public void ResourceVerifierCallGraphCannotReachPlannerWriterOrDeformationEvaluator()
    {
        var pending = new Stack<MethodBase>();
        pending.Push(typeof(Source2CompiledModelAdapter).GetMethod(nameof(Source2CompiledModelAdapter.VerifyPairedTransformAsync))!);
        var visited = new HashSet<MethodBase>();
        string[] forbidden = ["PlanPairedTransform", "CalculatePairedWords", "PairedMath", "ResolvePairedProfiles", "ResolvePairedProcedural",
            "ResolvePairedConsumers", "AddPairedFeConsumers", "PairedTriangles", "PlanDirectionalTransform", "CalculateDirectionalWords",
            "ResolveDirectionalProfiles", "ResolveDirectionalProtection", "ResolveDirectionalContext", "DirectionalCoincidences", "DirectionalProtectedWords",
            "PlanDirectionalMetadata", "DirectionalBufferFacts", "RewritePaired", "AuditPairedCandidate", "RewriteAsync", "CreatePlan"];
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method)) continue;
            Assert.NotEqual(typeof(DirectionalEllipsoidScale), method.DeclaringType);
            Assert.NotEqual(typeof(PairedDirectionalEllipsoidScale), method.DeclaringType);
            Assert.DoesNotContain(method.Name, forbidden);
            foreach (var called in Calls(method))
                if (called.DeclaringType?.Assembly.GetName().Name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true) pending.Push(called);
        }
        Assert.Contains(visited, m => m.DeclaringType == typeof(DirectionalFieldReconstruction) && m.Name == nameof(DirectionalFieldReconstruction.ReconstructPosition));
        Assert.Contains(visited, m => m.DeclaringType == typeof(DirectionalFieldReconstruction) && m.Name == nameof(DirectionalFieldReconstruction.ReconstructFrame));
        Assert.Contains(visited, m => m.Name == "AuditDirectionalProtection");
        Assert.Contains(visited, m => m.Name == "AuditPairedMetadata");
        Assert.Contains(visited, m => m.Name == "AuditPairedProcedural");
        Assert.Contains(visited, m => m.Name == "ReadPairedVerificationConsumers");
        Assert.Contains(visited, m => m.Name == "ReadPairedVerificationFeConsumers");
        Assert.Contains(visited, m => m.Name == "AuditPairedTriangles");
        Assert.Contains(visited, m => m.DeclaringType == typeof(PairedDirectionalFieldReconstruction));
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        var offset = 0;
        while (offset < bytes.Length)
        {
            ushort value = bytes[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            var code = codes[value];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null) yield return called;
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + BitConverter.ToInt32(bytes, offset) * 4),
                _ => 4,
            };
        }
    }
}

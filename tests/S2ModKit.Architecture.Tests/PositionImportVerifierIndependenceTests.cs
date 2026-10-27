using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using S2ModKit.Adapters.Source2;
using S2ModKit.Geometry;

namespace S2ModKit.Architecture.Tests;

public sealed class PositionImportVerifierIndependenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceVerifierCallGraphCannotReachPlannerWriterOrDeformationEvaluator(bool composition)
    {
        var pending = new Stack<MethodBase>();
        pending.Push(typeof(Source2CompiledModelAdapter).GetMethod(composition ? nameof(Source2CompiledModelAdapter.VerifyPositionImportCompositionAsync) : nameof(Source2CompiledModelAdapter.VerifyPositionImportAsync))!);
        var visited = new HashSet<MethodBase>();
        string[] forbidden = ["PlanPositionImportAsync", "RewritePositionImportAsync", "RewritePositionImportCompositionAsync", "PlanImportDrawProjectionAsync", "ApplyPositionImportFields", "ApplyReturnedWords", "MakePositionImportPlan", "MakePositionImportCompositionPlan", "PlanDirectionalMetadata", "CalculateDirectionalWords", "CalculatePairedWords", "ResolveDirectionalContext", "RewriteAsync", "CreatePlan"];
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method)) continue;
            Assert.NotEqual(typeof(DirectionalEllipsoidScale), method.DeclaringType);
            Assert.NotEqual(typeof(PairedDirectionalEllipsoidScale), method.DeclaringType);
            Assert.DoesNotContain(method.Name, forbidden);
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } state) pending.Push(state.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!);
            foreach (var called in Calls(method))
                if (called.DeclaringType?.Assembly.GetName().Name?.StartsWith("S2ModKit.", StringComparison.Ordinal) == true) pending.Push(called);
        }
        Assert.Contains(visited, m => m.Name == "VerifyPositionImportCore");
        Assert.Contains(visited, m => m.Name == "AuditCompleteContributorMetadata");
        Assert.Contains(visited, m => m.Name == "AuditPairedProcedural");
        Assert.Contains(visited, m => m.Name == "ReadDirectionalVerificationContext");
        Assert.Contains(visited, m => m.Name == "ImportTriangleFacts");
        Assert.Contains(visited, m => m.Name == "Validate" && m.DeclaringType?.Name == "PositionReturnValidator");
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

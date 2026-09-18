using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task InspectJsonWritesModelOnlyInsideSuccessEnvelope()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("models/test.vmdl_c", document.RootElement.GetProperty("result").GetProperty("model").GetProperty("artifact").GetProperty("logicalPath").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("result").GetProperty("project").GetProperty("dependencies").GetArrayLength());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task InspectTextReportsGeometryCapabilityAndOwnedVertexSummary()
    {
        var cli = new S2ModKitCli(new FakeApplication(snapshot: GeometrySnapshot), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Geometry: ready — synthetic geometry", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("vertices=3; exclusive-from-other-draw-calls=True", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("bounds=(0,1,2)..(3,4,5)", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task InspectJsonIncludesTypedGeometrySummary()
    {
        var cli = new S2ModKitCli(new FakeApplication(snapshot: GeometrySnapshot), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());
        var geometry = document.RootElement.GetProperty("result").GetProperty("model").GetProperty("lods")[0].GetProperty("meshes")[0].GetProperty("geometry");

        Assert.Equal(0, exitCode);
        Assert.Equal("ready", geometry.GetProperty("status").GetString());
        Assert.Equal(3, geometry.GetProperty("drawCalls")[0].GetProperty("uniqueVertexCount").GetInt32());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ComponentsListJsonWritesCanonicalDiscoveryInsideSuccessEnvelope()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["components", "list", "--project", "memory", "--format", "json"],
            output,
            error,
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");

        Assert.Equal(0, exitCode);
        Assert.Equal("components.list", document.RootElement.GetProperty("command").GetString());
        Assert.Equal(2, result.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("cmp_0123456789abcdef01234567", result.GetProperty("candidates")[0].GetProperty("candidateId").GetString());
        Assert.Equal("material_group", result.GetProperty("candidates")[0].GetProperty("kind").GetString());
        Assert.Equal("mesh_lineage", result.GetProperty("candidates")[1].GetProperty("kind").GetString());
        Assert.Equal("accessory_mesh", result.GetProperty("candidates")[1].GetProperty("sourceLabel").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ComponentsListTextShowsIdsLodsCapabilitiesAndGeometryFacts()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["components", "list", "--project", "memory"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("accessory [cmp_0123456789abcdef01234567]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Kind: material_group", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("LOD 0: 1 draw call(s)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("transform_component@1: available", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("vertices=12", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Source: accessory_mesh (key=accessory_mesh)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("mesh=models/test.vmdl_c#0; block=1; source=accessory_mesh", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

}

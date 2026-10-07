using S2ModKit.Application;
using S2ModKit.Domain;
using NJsonSchema;
namespace S2ModKit.Application.Tests;

public sealed partial class PreciseComponentDiscoveryServiceTests
{
    [Fact]
    public async Task PairedDiscoveryIsStructuralDeterministicAndCannotBeDowngradedToDirectionalAdmission()
    {
        var fixture = CreateGunFixture(3); var reversed = Reverse(fixture); var token = TestContext.Current.CancellationToken;
        var first = await PairedAuthoring.DiscoverAsync(fixture.Input, fixture.Model, token);
        var second = await PairedAuthoring.DiscoverAsync(reversed.Input, reversed.Model, token);
        Assert.Equal(7, first.SchemaVersion); Assert.Equal(JsonDefaults.Serialize(first), JsonDefaults.Serialize(second));
        Assert.All(first.Candidates, c => Assert.All(c.Capabilities, cap => { Assert.Equal(10, cap.OperationVersion); Assert.Equal("unsupported", cap.Availability); }));
        var json = JsonDefaults.Serialize(first); _ = JsonDefaults.Deserialize<ComponentDiscoveryResultV2>(System.Text.Encoding.UTF8.GetBytes(json), "discovery");
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<ComponentDiscoveryResultV2>(System.Text.Encoding.UTF8.GetBytes(JsonDefaults.Serialize(first with { SchemaVersion = 6 })), "discovery"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        Assert.NotNull(directory); var schema = await JsonSchema.FromFileAsync(Path.Combine(directory.FullName, "schemas", "component-discovery.schema.json"), token);
        Assert.Empty(schema.Validate(json));
        foreach (var count in new[] { 0, 2 })
        {
            var c = first.Candidates[0]; var malformed = first with { Candidates = [c with { Capabilities = count == 0 ? [] : [c.Capabilities[0], c.Capabilities[0]] }] };
            var text = JsonDefaults.Serialize(malformed);
            Assert.Throws<S2ModKitException>(() => JsonDefaults.Deserialize<ComponentDiscoveryResultV2>(System.Text.Encoding.UTF8.GetBytes(text), "discovery"));
            Assert.NotEmpty(schema.Validate(text));
        }
    }
}

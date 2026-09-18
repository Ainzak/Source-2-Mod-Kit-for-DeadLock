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
    public async Task DoctorJsonWritesOneSuccessEnvelopeToStandardOutput()
    {
        var application = new FakeApplication();
        var cli = new S2ModKitCli(application, "test-adapter", "1.2.3", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["doctor", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("doctor", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("test-adapter", document.RootElement.GetProperty("result").GetProperty("adapterName").GetString());
        Assert.Equal("not_configured", document.RootElement.GetProperty("result").GetProperty("geometryCodec").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task DoctorTextWritesHumanOutputWithoutJsonEnvelope()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1.2.3", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["doctor"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("S2ModKit doctor: ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Geometry codec: not_configured", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task MissingRequiredOptionReturnsSchemaExitCodeAndJsonError()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)ErrorCategory.CliOrSchema, exitCode);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("CLI_PARSE_ERROR", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(ErrorCategory.InputOrResolution)]
    [InlineData(ErrorCategory.UnsupportedCapability)]
    [InlineData(ErrorCategory.SelectionOrLod)]
    [InlineData(ErrorCategory.RewriteOrVerification)]
    public async Task ExpectedFailuresReturnTheirStableExitCategory(ErrorCategory category)
    {
        var failure = new S2ModKitException(new S2Error("TEST_FAILURE", "test", "Expected failure.", "Fix the test input.", category));
        var cli = new S2ModKitCli(new FakeApplication(failure), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)category, exitCode);
        Assert.Equal((int)category, document.RootElement.GetProperty("error").GetProperty("category").GetInt32());
        Assert.Equal("TEST_FAILURE", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task UnexpectedFailureReturnsSeventyAndKeepsDiagnosticsOnStandardError()
    {
        var cli = new S2ModKitCli(new FakeApplication(new InvalidOperationException("diagnostic detail")), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory", "--format", "json"], output, error, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output.ToString());

        Assert.Equal((int)ErrorCategory.Unexpected, exitCode);
        Assert.Equal("UNEXPECTED_INTERNAL_ERROR", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("InvalidOperationException: diagnostic detail", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic detail", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextFailureUsesOnlyStandardError()
    {
        var failure = new S2ModKitException(new S2Error("INPUT_MISSING", "input", "Missing.", "Provide it.", ErrorCategory.InputOrResolution));
        var cli = new S2ModKitCli(new FakeApplication(failure), "test-adapter", "1", externalVerifierAvailable: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(["inspect", "--project", "memory"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal((int)ErrorCategory.InputOrResolution, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("ERROR INPUT_MISSING [input]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliReferenceDocumentsEveryPublicHelpOption()
    {
        var reference = await File.ReadAllTextAsync(
            GetRepositoryPath("docs", "CLI-REFERENCE.md"),
            TestContext.Current.CancellationToken);
        var contracts = new[]
        {
            new HelpContract(["doctor"], "s2mod doctor"),
            new HelpContract(["interactive"], "s2mod interactive"),
            new HelpContract(["catalog", "heroes", "list"], "s2mod catalog heroes list"),
            new HelpContract(["catalog", "resolve"], "s2mod catalog resolve"),
            new HelpContract(["compatibility", "scan"], "s2mod compatibility scan"),
            new HelpContract(["project", "create"], "s2mod project create"),
            new HelpContract(["project", "create-vpk"], "s2mod project create-vpk"),
            new HelpContract(["inspect"], "s2mod inspect"),
            new HelpContract(["components", "list"], "s2mod components list"),
            new HelpContract(["recipe", "scaffold"], "s2mod recipe scaffold"),
            new HelpContract(["plan"], "s2mod plan"),
            new HelpContract(["build"], "s2mod build"),
            new HelpContract(["verify"], "s2mod verify"),
            new HelpContract(["package", "create"], "s2mod package create"),
            new HelpContract(["package", "create-minimal"], "s2mod package create-minimal"),
            new HelpContract(["package", "verify"], "s2mod package verify"),
            new HelpContract(["addons", "inventory"], "s2mod addons inventory"),
            new HelpContract(["addons", "install"], "s2mod addons install"),
            new HelpContract(["addons", "verify-active"], "s2mod addons verify-active"),
            new HelpContract(["addons", "rollback"], "s2mod addons rollback"),
            new HelpContract(["runtime", "record"], "s2mod runtime record"),
        };

        foreach (var contract in contracts)
        {
            var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await cli.RunAsync(
                [.. contract.Arguments, "--help"],
                output,
                error,
                TestContext.Current.CancellationToken);
            var section = ExtractReferenceSection(reference, contract.Signature);

            Assert.Equal(0, exitCode);
            Assert.Empty(error.ToString());
            foreach (var option in Regex.Matches(output.ToString(), @"--[a-z][a-z-]*")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal))
            {
                Assert.Contains(option, section, StringComparison.Ordinal);
            }
        }
    }

    private static string ExtractReferenceSection(string reference, string signature)
    {
        var heading = $"### `{signature}`";
        var start = reference.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"CLI reference is missing heading '{heading}'.");
        var end = reference.IndexOf("\n### `", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? reference[start..] : reference[start..end];
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }

    private sealed record HelpContract(IReadOnlyList<string> Arguments, string Signature);

}

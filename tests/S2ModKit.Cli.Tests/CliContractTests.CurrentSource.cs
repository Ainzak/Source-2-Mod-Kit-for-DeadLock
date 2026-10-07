using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Cli;
using S2ModKit.Domain;

namespace S2ModKit.Cli.Tests;

public sealed partial class CliContractTests
{
    [Fact]
    public async Task GuardedInstallRequiresReviewedHashAndWritesJsonOnly()
    {
        var guarded = new FakeCurrentSourceInstallation();
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(),
            "test-adapter", "1", false, currentSourceInstallation: guarded);
        using var output = new StringWriter(); using var error = new StringWriter();
        var hash = ContentHash.Compute("reviewed-package"u8);
        var code = await cli.RunAsync(CurrentSourceArguments(hash.Value), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(0, code); Assert.Empty(error.ToString());
        Assert.Equal(hash, guarded.Request!.ExpectedPackageHash);
        Assert.Equal("current.vpk", guarded.Request.SourceLocator);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("addons.install-current", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("relevant_source_snapshot", json.RootElement.GetProperty("result").GetProperty("preflight").GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMalformedReviewHashCannotInvokeGuardedInstall(bool malformed)
    {
        var guarded = new FakeCurrentSourceInstallation();
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(),
            "test-adapter", "1", false, currentSourceInstallation: guarded);
        using var output = new StringWriter(); using var error = new StringWriter();
        var arguments = CurrentSourceArguments("wrong");
        if (!malformed) arguments = arguments.Take(arguments.Length - 2).ToArray();
        Assert.Equal(2, await cli.RunAsync(arguments, output, error, TestContext.Current.CancellationToken));
        Assert.Null(guarded.Request);
    }

    [Theory]
    [InlineData(false, 20, "CURRENT_SOURCE_INSTALL_UNAVAILABLE")]
    [InlineData(true, 40, "CURRENT_SOURCE_RESOURCE_DRIFT")]
    public async Task MissingGuardAndSourceDriftHaveStableFailureEnvelopes(bool configured, int expectedExit, string expectedCode)
    {
        var guarded = new FakeCurrentSourceInstallation { Reject = true };
        var cli = new S2ModKitCli(new FakeApplication(), new FakePackagingApplication(), new FakeAddonManagementApplication(),
            "test-adapter", "1", false, currentSourceInstallation: configured ? guarded : null);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(expectedExit, await cli.RunAsync(CurrentSourceArguments(ContentHash.Compute("package"u8).Value),
            output, error, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(error.ToString());
    }

    private static string[] CurrentSourceArguments(string hash) => ["addons", "install-current", "--addons-root", "addons", "--project", "project",
        "--package", "package", "--base-vpk", "current.vpk", "--expected-package-hash", hash];

    private sealed class FakeCurrentSourceInstallation : ICurrentSourceInstallationApplication
    {
        public CurrentSourceInstallationRequest? Request { get; private set; }
        public bool Reject { get; init; }
        public async Task<CurrentSourceInstallationResult> InstallAsync(CurrentSourceInstallationRequest request, CancellationToken cancellationToken = default)
        {
            if (Reject) throw Errors.Verification("CURRENT_SOURCE_RESOURCE_DRIFT", "source changed", "rebuild");
            Request = request;
            var status = await new FakeAddonManagementApplication().InstallAsync(request.AddonsRoot, request.ProjectRoot, request.PackageId, request.Slot, cancellationToken);
            var hash = ContentHash.Compute("model"u8);
            CurrentSourceResource[] expected = [new("models/test.vmdl_c", "model", hash, 5)];
            CurrentSourceObservation[] observed = [new(expected[0], "synthetic", "source-entry", "source-catalog", "sha256")];
            return new(status, new(1, "source-preflight-test", "passed", "relevant_source_snapshot", DateTimeOffset.UnixEpoch,
                "project", "build", hash, request.PackageId, request.ExpectedPackageHash, hash, hash, hash, expected, observed, observed));
        }
    }
}

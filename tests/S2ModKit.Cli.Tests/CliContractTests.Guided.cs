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
    public async Task InteractiveSelectsConfiguredSourceHeroAndResourceAndSavesCheckpoint()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader("1\n1\n1\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.ComponentSelectionStep, session.Step);
            Assert.Equal("haze", session.SelectedHeroId);
            Assert.Equal("haze.primary", session.SelectedResourceId);
            Assert.Contains("Choose a component", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(GuidedWorkflowContract.PausedStatus, session.Status);
            Assert.DoesNotContain("models/heroes_staging", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveCancellationIsResumableAndCreatesNoWorkflowArtifact()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var firstInput = new StringReader("1\ncancel\n");
            using var firstOutput = new StringWriter();
            using var firstError = new StringWriter();

            var firstExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                firstInput,
                firstOutput,
                firstError,
                TestContext.Current.CancellationToken);
            using var resumedInput = new StringReader("1\n1\n");
            using var resumedOutput = new StringWriter();
            using var resumedError = new StringWriter();
            var resumedExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--resume"],
                resumedInput,
                resumedOutput,
                resumedError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, firstExit);
            Assert.Contains("No project, recipe, package, or installation was created", firstOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, resumedExit);
            Assert.Contains("Resumed guided session", resumedOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Choose a component", resumedOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("The imported project remains", resumedOutput.ToString(), StringComparison.Ordinal);
            Assert.Empty(firstError.ToString());
            Assert.Empty(resumedError.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("back")]
    [InlineData("previous")]
    public async Task InteractiveBackFromResourceSelectionReturnsToHeroWithoutReopeningCatalogueInventory(string navigation)
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var inventoryFactory = new FakeCatalogueInventoryFactory(directoryHash);
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: inventoryFactory);
            using var input = new StringReader($"1\n1\n{navigation}\ncancel\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.HeroSelectionStep, session.Step);
            Assert.Null(session.SelectedHeroId);
            Assert.Null(session.SelectedResourceId);
            Assert.Null(session.ProjectRoot);
            Assert.Equal(1, inventoryFactory.OpenCount);
            Assert.Equal(2, Regex.Count(output.ToString(), "Choose a hero:", RegexOptions.CultureInvariant));
            Assert.Contains("0. Back to hero selection", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveZeroReturnsFromComponentThroughResourceToHeroInSameSession()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var inventoryFactory = new FakeCatalogueInventoryFactory(directoryHash);
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: inventoryFactory);
            using var input = new StringReader("1\n1\n1\n0\n0\ncancel\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.HeroSelectionStep, session.Step);
            Assert.Null(session.SelectedHeroId);
            Assert.Null(session.SelectedResourceId);
            Assert.Null(session.ProjectRoot);
            Assert.False(session.ProjectReady);
            Assert.Equal(1, inventoryFactory.OpenCount);
            Assert.Contains("0. Back to resource selection", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, Regex.Count(output.ToString(), "Choose a hero:", RegexOptions.CultureInvariant));
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveWithoutOptionsPromptsForNewSessionSetup()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"{sessionPath}\n1\npak01_dir.vpk\ncancel\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.SourceSelectionStep, session.Step);
            Assert.Single(session.Sources);
            Assert.Equal(GuidedWorkflowContract.BaseVpkSource, session.Sources[0].Kind);
            Assert.Contains("Session JSON path", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Hero catalogue JSON path", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Choose a source type", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Theory]
    [InlineData("q")]
    [InlineData("quit")]
    public async Task InteractiveCancellationAliasesPauseBeforeCreatingArtifacts(string cancellation)
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"{cancellation}\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.SourceSelectionStep, session.Step);
            Assert.Equal(GuidedWorkflowContract.PausedStatus, session.Status);
            Assert.Null(session.ProjectRoot);
            Assert.Contains("No project, recipe, package, or installation was created", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveScaffoldsAvailableActionAndStopsAfterDryRun()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var application = new FakeApplication();
            var cli = new S2ModKitCli(
                application,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader("1\n1\n1\n1\n1\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.OutputSelectionStep, session.Step);
            Assert.True(session.ProjectReady);
            Assert.Equal(RecipeScaffoldContract.RemoveIntent, application.LastScaffoldRequest!.Intent);
            Assert.Equal("cmp_0123456789abcdef01234567", application.LastScaffoldRequest.ComponentIds.Single());
            Assert.NotNull(session.RecipeContentHash);
            Assert.NotNull(session.PlanFingerprint);
            Assert.Equal(1, session.PlannedDrawCallCount);
            Assert.Contains("Dry-run passed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Model bytes built: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Ready for output selection", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveRejectsUnboundedTransformParametersBeforeScaffolding()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var application = new FakeApplication();
            var cli = new S2ModKitCli(
                application,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader("1\n1\n1\n1\n2\n1\n2\n0\n64\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(RecipeScaffoldContract.UniformScaleIntent, application.LastScaffoldRequest!.Intent);
            Assert.Equal(2f, application.LastScaffoldRequest.UniformScale);
            Assert.Equal(64f, application.LastScaffoldRequest.MaximumVertexDisplacement);
            Assert.Contains("other than 1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("greater than 0", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task InteractiveRejectsJsonFormatAsHumanTerminalError()
    {
        var cli = new S2ModKitCli(new FakeApplication(), "test-adapter", "1", externalVerifierAvailable: false);
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await cli.RunAsync(
            ["interactive", "--format", "json"],
            input,
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal((int)ErrorCategory.CliOrSchema, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("ERROR CLI_PARSE_ERROR", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveBuildsPackagesAndCanDeclineInstallation()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                new FakeAddonManagementApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader("1\n1\n1\n1\n1\n3\n1\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");
            GuidedWorkflow.ValidateSession(session);

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, session.Status);
            Assert.Equal(GuidedWorkflowContract.MinimalPackageOutput, session.OutputChoice);
            Assert.Equal("build-test", session.BuildId);
            Assert.Equal("vpk-0123456789abcdef0123", session.PackageId);
            Assert.Null(session.InstallationId);
            Assert.Contains("nothing was installed", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Theory]
    [InlineData("1", GuidedWorkflowContract.PlanOnlyOutput, false)]
    [InlineData("2", GuidedWorkflowContract.BuildOnlyOutput, true)]
    public async Task InteractiveCanStopAfterPlanOrVerifiedBuild(string selection, string expectedOutput, bool expectsBuild)
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                new FakeAddonManagementApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"1\n1\n1\n1\n1\n{selection}\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal(expectedOutput, session.OutputChoice);
            GuidedWorkflow.ValidateSession(session);
            Assert.Equal(expectsBuild, session.BuildId is not null);
            Assert.Null(session.PackageId);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, session.Status);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractiveResumesExportAfterCancellationWithoutRebuilding()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        var exportPath = Path.Combine(Path.GetTempPath(), $"s2modkit-export-{Guid.NewGuid():N}.vpk");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                new FakeAddonManagementApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var firstInput = new StringReader("1\n1\n1\n1\n1\n4\n");
            using var firstOutput = new StringWriter();
            using var firstError = new StringWriter();

            var firstExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                firstInput,
                firstOutput,
                firstError,
                TestContext.Current.CancellationToken);
            var paused = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");
            using var resumedInput = new StringReader($"{exportPath}\n1\n");
            using var resumedOutput = new StringWriter();
            using var resumedError = new StringWriter();
            var resumedExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--resume"],
                resumedInput,
                resumedOutput,
                resumedError,
                TestContext.Current.CancellationToken);
            var completed = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");

            Assert.Equal(0, firstExit);
            Assert.Equal(GuidedWorkflowContract.PausedStatus, paused.Status);
            Assert.NotNull(paused.BuildId);
            Assert.NotNull(paused.PackageId);
            Assert.Equal(0, resumedExit);
            Assert.Equal(Path.GetFullPath(exportPath), completed.ExportPath);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, completed.Status);
            Assert.Empty(firstError.ToString());
            Assert.Empty(resumedError.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
            File.Delete(exportPath);
        }
    }

    [Fact]
    public async Task InteractiveAutomaticallyInstallsVerifiesAndRollsBackByReceipt()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        var addonsRoot = Path.Combine(Path.GetTempPath(), $"s2modkit-addons-{Guid.NewGuid():N}");
        try
        {
            var addons = new FakeAddonManagementApplication();
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                addons,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"1\n1\n1\n1\n1\n3\n2\n{addonsRoot}\ninstall\n2\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal("auto", addons.LastInstallSlot);
            Assert.True(addons.RolledBack);
            Assert.Equal("rolled_back", session.InstallationStatus);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, session.Status);
            Assert.Contains("Rollback command:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("addons rollback", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractiveAcceptsExplicitVerifiedSlotAndCanLeaveInstallActive()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        var addonsRoot = Path.Combine(Path.GetTempPath(), $"s2modkit-addons-{Guid.NewGuid():N}");
        try
        {
            var addons = new FakeAddonManagementApplication();
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                addons,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"1\n1\n1\n1\n1\n3\n3\n{addonsRoot}\n5\nAA\n95\ninstall\n1\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Equal("95", addons.LastInstallSlot);
            Assert.False(addons.RolledBack);
            Assert.Equal("active", session.InstallationStatus);
            Assert.Equal(95, session.InstallationSlot);
            Assert.Contains("two-digit slot", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("left active", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractiveDoesNotInstallWhenConfirmationIsNotExact()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        var addonsRoot = Path.Combine(Path.GetTempPath(), $"s2modkit-addons-{Guid.NewGuid():N}");
        try
        {
            var addons = new FakeAddonManagementApplication();
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                addons,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader($"1\n1\n1\n1\n1\n3\n2\n{addonsRoot}\nyes\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken), "Guided session");

            Assert.Equal(0, exitCode);
            Assert.Null(addons.LastInstallSlot);
            Assert.Null(session.InstallationId);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, session.Status);
            Assert.Contains("not authorized", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractiveFreshUserCompletesKeyboardOnlyInNarrowTerminalAfterInvalidChoices()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                new FakeAddonManagementApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var input = new StringReader("x\n1\n0\n1\n9\n1\n9\n1\n9\n1\n9\n1\n");
            using var output = new NarrowTextWriter(40);
            using var error = new StringWriter();

            var exitCode = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                input,
                output,
                error,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");
            var rendered = output.ToString();

            Assert.Equal(0, exitCode);
            Assert.Equal(GuidedWorkflowContract.PlanOnlyOutput, session.OutputChoice);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, session.Status);
            Assert.Contains("Enter 1-", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("cmp_", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("dc_", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("models/heroes", rendered, StringComparison.Ordinal);
            Assert.All(rendered.Split('\n'), line => Assert.True(line.Length <= 40));
            Assert.Empty(error.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractiveFreshAgentHandsCheckpointedArtifactsToDocumentedCommands()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        try
        {
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                new FakeAddonManagementApplication(),
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var guidedInput = new StringReader("1\n1\n1\n1\n1\n3\n1\n");
            using var guidedOutput = new StringWriter();
            using var guidedError = new StringWriter();

            var guidedExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                guidedInput,
                guidedOutput,
                guidedError,
                TestContext.Current.CancellationToken);
            var session = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            using var planOutput = new StringWriter();
            using var planError = new StringWriter();
            var planExit = await cli.RunAsync(
                ["plan", "--project", session.ProjectRoot!, "--recipe", session.RecipePath!, "--format", "json"],
                planOutput,
                planError,
                TestContext.Current.CancellationToken);
            using var verifyOutput = new StringWriter();
            using var verifyError = new StringWriter();
            var verifyExit = await cli.RunAsync(
                ["verify", "--project", session.ProjectRoot!, "--build", session.BuildId!],
                verifyOutput,
                verifyError,
                TestContext.Current.CancellationToken);
            using var packageOutput = new StringWriter();
            using var packageError = new StringWriter();
            var packageExit = await cli.RunAsync(
                ["package", "verify", "--project", session.ProjectRoot!, "--package", session.PackageId!],
                packageOutput,
                packageError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, guidedExit);
            Assert.Equal(0, planExit);
            Assert.Equal(0, verifyExit);
            Assert.Equal(0, packageExit);
            Assert.Equal("plan", JsonDocument.Parse(planOutput.ToString()).RootElement.GetProperty("command").GetString());
            Assert.Equal("verify", JsonDocument.Parse(verifyOutput.ToString()).RootElement.GetProperty("command").GetString());
            Assert.Equal("package.verify", JsonDocument.Parse(packageOutput.ToString()).RootElement.GetProperty("command").GetString());
            Assert.Empty(guidedError.ToString());
            Assert.Empty(planError.ToString());
            Assert.Empty(verifyError.ToString());
            Assert.Empty(packageError.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

    [Fact]
    public async Task InteractivePauseAfterInstallKeepsRollbackVisibleAndResumable()
    {
        var directoryHash = ContentHash.Compute("catalogue-directory"u8);
        var cataloguePath = await WriteCatalogueAsync(directoryHash);
        var sessionPath = Path.Combine(Path.GetTempPath(), $"s2modkit-guided-{Guid.NewGuid():N}.json");
        var addonsRoot = Path.Combine(Path.GetTempPath(), $"s2modkit-addons-{Guid.NewGuid():N}");
        try
        {
            var addons = new FakeAddonManagementApplication();
            var cli = new S2ModKitCli(
                new FakeApplication(writeScaffoldedRecipe: true),
                new FakePackagingApplication(),
                addons,
                "test-adapter",
                "1",
                externalVerifierAvailable: false,
                catalogueInventoryFactory: new FakeCatalogueInventoryFactory(directoryHash));
            using var firstInput = new StringReader($"1\n1\n1\n1\n1\n3\n2\n{addonsRoot}\ninstall\n");
            using var firstOutput = new StringWriter();
            using var firstError = new StringWriter();

            var firstExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--base-vpk", "pak01_dir.vpk"],
                firstInput,
                firstOutput,
                firstError,
                TestContext.Current.CancellationToken);
            var paused = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");
            using var resumedInput = new StringReader("2\n");
            using var resumedOutput = new StringWriter();
            using var resumedError = new StringWriter();
            var resumedExit = await cli.RunAsync(
                ["interactive", "--catalogue", cataloguePath, "--session", sessionPath, "--resume"],
                resumedInput,
                resumedOutput,
                resumedError,
                TestContext.Current.CancellationToken);
            var completed = JsonDefaults.Deserialize<GuidedWorkflowSession>(
                await File.ReadAllBytesAsync(sessionPath, TestContext.Current.CancellationToken),
                "Guided session");

            Assert.Equal(0, firstExit);
            Assert.Equal(GuidedWorkflowContract.PausedStatus, paused.Status);
            Assert.Equal(GuidedWorkflowContract.RollbackSelectionStep, paused.Step);
            Assert.Equal("active", paused.InstallationStatus);
            Assert.Contains("Rollback command:", firstOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("may be active", firstOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, resumedExit);
            Assert.True(addons.RolledBack);
            Assert.Equal("rolled_back", completed.InstallationStatus);
            Assert.Equal(GuidedWorkflowContract.CompleteStatus, completed.Status);
            Assert.Empty(firstError.ToString());
            Assert.Empty(resumedError.ToString());
        }
        finally
        {
            File.Delete(cataloguePath);
            File.Delete(sessionPath);
            File.Delete(Path.ChangeExtension(sessionPath, ".recipe.json"));
        }
    }

}

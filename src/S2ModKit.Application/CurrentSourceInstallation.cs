using System.Text.Json.Serialization;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record CurrentSourceResource(
    [property: JsonRequired] string LogicalPath, [property: JsonRequired] string Role,
    [property: JsonRequired] ContentHash ContentHash, [property: JsonRequired] long Size);

public sealed record CurrentSourceObservation(
    [property: JsonRequired] CurrentSourceResource Resource,
    [property: JsonRequired] string SourceKind, [property: JsonRequired] string SourceIdentity,
    [property: JsonRequired] string CatalogIdentity, [property: JsonRequired] string VerificationMode);

/// <summary>Reads actual current bytes through an explicitly configured source, never a passed flag.</summary>
public interface ICurrentSourceReader
{
    Task<IReadOnlyList<CurrentSourceObservation>> ReadAsync(string sourceLocator,
        IReadOnlyList<CurrentSourceResource> resources, CancellationToken cancellationToken = default);
}

public sealed class CatalogCurrentSourceReader(IResourceCatalogInventoryFactory factory) : ICurrentSourceReader
{
    public async Task<IReadOnlyList<CurrentSourceObservation>> ReadAsync(string sourceLocator,
        IReadOnlyList<CurrentSourceResource> resources, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLocator);
        ArgumentNullException.ThrowIfNull(resources);
        SourcePreflightJson.ValidateResources(resources);
        var catalog = factory.OpenReadOnly(sourceLocator);
        try
        {
            var observations = new List<CurrentSourceObservation>(resources.Count);
            foreach (var expected in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var opened = await catalog.TryOpenAsync(expected.LogicalPath, cancellationToken).ConfigureAwait(false)
                    ?? throw Errors.Input("CURRENT_SOURCE_RESOURCE_MISSING", $"Current source lacks '{expected.LogicalPath}'.", "Import and qualify a complete current source before installation.");
                if (opened.Content.LogicalPath != expected.LogicalPath || ContentHash.Compute(opened.Content.Bytes.Span) != opened.Content.ContentHash)
                    throw Errors.Verification("CURRENT_SOURCE_READ_INCONSISTENT", "Current source returned inconsistent bytes or identity.", "Reject the source and retry a verified snapshot.");
                observations.Add(new(expected with { ContentHash = opened.Content.ContentHash, Size = opened.Content.Bytes.Length },
                    catalog.Descriptor.SourceKind, opened.SourceIdentity, catalog.Descriptor.SourceIdentity, catalog.Descriptor.VerificationMode));
            }
            return observations;
        }
        finally { (catalog as IDisposable)?.Dispose(); }
    }
}

public sealed record CurrentSourceCandidateVerification(PublishedBuild Build, PublishedVpkPackage Package,
    ContentHash ModelVerificationHash, ContentHash PackageVerificationHash);

public interface ICurrentSourceCandidateVerifier
{
    Task<CurrentSourceCandidateVerification> VerifyAsync(string projectRoot, string buildId, string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the existing independent model and archive verification lifecycle.</summary>
public sealed class CurrentSourceCandidateVerifier(IS2ModKitApplication models, IVpkPackagingApplication packages)
    : ICurrentSourceCandidateVerifier
{
    public async Task<CurrentSourceCandidateVerification> VerifyAsync(string projectRoot, string buildId, string packageId,
        CancellationToken cancellationToken = default)
    {
        var model = await models.VerifyAsync(projectRoot, buildId, cancellationToken).ConfigureAwait(false);
        var package = await packages.VerifyAsync(projectRoot, packageId, requireExternalVerifier: true, cancellationToken).ConfigureAwait(false);
        if (model.Evidence.Status != "passed" || package.Evidence.Status != "passed")
            throw Errors.Verification("CURRENT_SOURCE_CANDIDATE_VERIFICATION_FAILED", "Model or package verification did not pass.", "Resolve verification failures before installation.");
        return new(model.Build, package.Package, ContentHash.Compute(JsonDefaults.SerializeToUtf8(model.Evidence)),
            ContentHash.Compute(JsonDefaults.SerializeToUtf8(package.Evidence)));
    }
}

public sealed record CurrentSourceInstallationRequest(string AddonsRoot, string ProjectRoot,
    string PackageId, ContentHash ExpectedPackageHash, string SourceLocator, string Slot);

public sealed record SourcePreflightReport(
    [property: JsonRequired] int SchemaVersion, [property: JsonRequired] string ReportId,
    [property: JsonRequired] string Status, [property: JsonRequired] string Scope,
    [property: JsonRequired] DateTimeOffset ObservedUtc, [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string BuildId, [property: JsonRequired] ContentHash PlanFingerprint,
    [property: JsonRequired] string PackageId, [property: JsonRequired] ContentHash PackageHash,
    [property: JsonRequired] ContentHash EntryHash, [property: JsonRequired] ContentHash ModelVerificationHash,
    [property: JsonRequired] ContentHash PackageVerificationHash,
    [property: JsonRequired] IReadOnlyList<CurrentSourceResource> Expected,
    [property: JsonRequired] IReadOnlyList<CurrentSourceObservation> Initial,
    [property: JsonRequired] IReadOnlyList<CurrentSourceObservation> Handoff);

public sealed record CurrentSourceInstallationResult(InstallationStatus Installation, SourcePreflightReport Preflight);

public interface ICurrentSourceInstallationApplication
{
    Task<CurrentSourceInstallationResult> InstallAsync(CurrentSourceInstallationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A blocking source snapshot in the same workflow as the managed install handoff.</summary>
public sealed class CurrentSourceInstallationApplication(IProjectWorkspace projects, IVpkPackageWorkspace packages,
    ICurrentSourceReader source, ICurrentSourceCandidateVerifier verifier, IAddonManagementApplication addons, IClock clock)
    : ICurrentSourceInstallationApplication
{
    private sealed record Binding(ProjectManifest Project, PublishedBuild Build, PublishedVpkPackage Package,
        IReadOnlyList<CurrentSourceResource> Expected);

    public async Task<CurrentSourceInstallationResult> InstallAsync(CurrentSourceInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceLocator);
        var prepared = await LoadBindingAsync(request, cancellationToken).ConfigureAwait(false);
        var initial = await ReadCurrentAsync(request.SourceLocator, prepared.Expected, cancellationToken).ConfigureAwait(false);
        RequireCurrent(prepared.Expected, initial);
        var verified = await verifier.VerifyAsync(request.ProjectRoot, prepared.Build.BuildId, request.PackageId, cancellationToken).ConfigureAwait(false);
        if (verified.Build != prepared.Build || verified.Package != prepared.Package)
            throw Drift("Candidate verification returned a different build or package.");
        var handoffBinding = await LoadBindingAsync(request, cancellationToken).ConfigureAwait(false);
        if (prepared.Project.ProjectId != handoffBinding.Project.ProjectId || prepared.Build != handoffBinding.Build
            || prepared.Package != handoffBinding.Package || !prepared.Expected.SequenceEqual(handoffBinding.Expected))
            throw Drift("Project/build/package linkage changed before installation handoff.");
        var handoff = await ReadCurrentAsync(request.SourceLocator, handoffBinding.Expected, cancellationToken).ConfigureAwait(false);
        RequireCurrent(handoffBinding.Expected, handoff);
        cancellationToken.ThrowIfCancellationRequested();
        var report = new SourcePreflightReport(1, $"source-preflight-{Guid.NewGuid():N}", "passed", "relevant_source_snapshot",
            clock.UtcNow.ToUniversalTime(), prepared.Project.ProjectId, prepared.Build.BuildId, prepared.Build.PlanFingerprint,
            prepared.Package.PackageId, prepared.Package.ContentHash, prepared.Package.ReplacementEntryHash,
            verified.ModelVerificationHash, verified.PackageVerificationHash, prepared.Expected, initial, handoff);
        SourcePreflightJson.Validate(report);
        await projects.SaveEvidenceAsync(request.ProjectRoot, report.ReportId, SourcePreflightJson.Write(report),
            $"Current-source snapshot passed for {report.PackageId}. All {report.Expected.Count} relevant resources match at handoff.\n" +
            "This observation does not lock against a subsequent source update or qualify live behavior.\n", cancellationToken).ConfigureAwait(false);
        // No receipt, marker or addon access occurs above this boundary. A preflight report is
        // diagnostic output and is never accepted as authorization on a future invocation.
        var installed = await addons.InstallAsync(request.AddonsRoot, request.ProjectRoot, request.PackageId, request.Slot, cancellationToken).ConfigureAwait(false);
        return new(installed, report);
    }

    private async Task<Binding> LoadBindingAsync(CurrentSourceInstallationRequest request, CancellationToken cancellationToken)
    {
        var project = await projects.LoadProjectAsync(request.ProjectRoot, cancellationToken).ConfigureAwait(false);
        var loadedPackage = await packages.LoadPackageAsync(request.ProjectRoot, request.PackageId, cancellationToken).ConfigureAwait(false);
        var package = loadedPackage.Package;
        if (package.Mode != "minimal") throw Errors.Unsupported("CURRENT_SOURCE_PACKAGE_MODE_UNSUPPORTED",
            "The guarded current-source route requires a minimal one-model package.", "Use a verified minimal package; additional package entries need their own source closure.");
        if (!project.DependencyGraphComplete || package.PackageId != request.PackageId || package.ContentHash != request.ExpectedPackageHash
            || package.EntryLogicalPath != project.Input.LogicalPath)
            throw Drift("The reviewed package identity or complete project source graph does not match.");
        var (build, content) = await projects.LoadBuildAsync(request.ProjectRoot, package.BuildId, cancellationToken).ConfigureAwait(false);
        if (build.BuildId != package.BuildId || build.LogicalPath != package.EntryLogicalPath || content.LogicalPath != build.LogicalPath
            || build.ContentHash != package.ReplacementEntryHash || content.ContentHash != build.ContentHash
            || ContentHash.Compute(content.Bytes.Span) != build.ContentHash || content.Bytes.Length != build.Size)
            throw Drift("Package replacement bytes do not match the immutable build.");
        var expected = project.Dependencies.Prepend(project.Input).Select(item => new CurrentSourceResource(item.LogicalPath,
            item == project.Input ? "model" : "dependency", item.ContentHash, item.Size)).OrderBy(item => item.LogicalPath, StringComparer.Ordinal).ToArray();
        SourcePreflightJson.ValidateResources(expected);
        var plan = await projects.LoadPlanAsync(request.ProjectRoot, build.PlanFingerprint, cancellationToken).ConfigureAwait(false);
        if (plan.Fingerprint != build.PlanFingerprint || !plan.Inputs.SequenceEqual(expected.Select(item => new PlannedInput(item.LogicalPath, item.ContentHash, item.Size))))
            throw Drift("The build plan's complete source identities differ from the project.");
        return new(project, build, package, expected);
    }

    private async Task<IReadOnlyList<CurrentSourceObservation>> ReadCurrentAsync(string locator,
        IReadOnlyList<CurrentSourceResource> expected, CancellationToken cancellationToken)
    {
        try { return await source.ReadAsync(locator, expected, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new S2ModKitException(new S2Error("CURRENT_SOURCE_READ_FAILED", "input",
                "Current source could not be read completely.", "Resolve archive access and rerun the guarded workflow; no install handoff was made.",
                ErrorCategory.InputOrResolution), exception);
        }
    }

    internal static void RequireCurrent(IReadOnlyList<CurrentSourceResource> expected, IReadOnlyList<CurrentSourceObservation> observed)
    {
        if (observed is null || observed.Count != expected.Count) throw Drift("Current source inventory is incomplete.");
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = observed[index];
            if (actual is null || actual.Resource != expected[index])
                throw Errors.Verification("CURRENT_SOURCE_RESOURCE_DRIFT", $"Current model/dependency differs: '{expected[index].LogicalPath}'.",
                    "Import current source, reauthor as needed, rebuild and reverify before installation.");
            if (string.IsNullOrWhiteSpace(actual.SourceKind) || string.IsNullOrWhiteSpace(actual.SourceIdentity)
                || string.IsNullOrWhiteSpace(actual.CatalogIdentity) || string.IsNullOrWhiteSpace(actual.VerificationMode))
                throw Drift("Current source provenance is incomplete.");
        }
    }

    private static S2ModKitException Drift(string summary) => Errors.Verification("CURRENT_SOURCE_HANDOFF_DRIFT", summary,
        "Reject this handoff and recheck the reviewed package against a complete immutable project.");
}

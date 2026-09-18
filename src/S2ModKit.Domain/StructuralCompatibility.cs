using System.Globalization;
using System.Text;

namespace S2ModKit.Domain;

public static class CapabilityAvailability
{
    public const string Available = "available";

    public const string Blocked = "blocked";

    public const string Unsupported = "unsupported";

    public const string Ambiguous = "ambiguous";

    public static bool IsDefined(string value) => value is Available or Blocked or Unsupported or Ambiguous;
}

public static class StructuralCompatibilityContract
{
    public const int Version = 1;

    public const string Matched = "matched";

    public const string Rejected = "rejected";

    public const string Ambiguous = "ambiguous";

    public static bool IsProfileMatchStatus(string value) => value is Matched or Rejected or Ambiguous;
}

public sealed record StructuralFact
{
    public StructuralFact(string key, string value)
    {
        Key = StructuralContractText.NormalizeIdentifier(key, nameof(key));
        Value = StructuralContractText.NormalizeText(value, nameof(value));
    }

    public string Key { get; }

    public string Value { get; }
}

public sealed record StructuralProfileIdentity
{
    public StructuralProfileIdentity(string profileId, int version)
    {
        ProfileId = StructuralContractText.NormalizeIdentifier(profileId, nameof(profileId));
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A structural profile version must be positive.");
        }

        Version = version;
    }

    public string ProfileId { get; }

    public int Version { get; }
}

public sealed record StructuralCompatibilityReason
{
    public StructuralCompatibilityReason(string code, string summary)
    {
        Code = StructuralContractText.NormalizeReasonCode(code, nameof(code));
        Summary = StructuralContractText.NormalizeText(summary, nameof(summary));
    }

    public string Code { get; }

    public string Summary { get; }
}

public sealed record StructuralSignature
{
    public StructuralSignature(
        int contractVersion,
        string family,
        string signatureId,
        ContentHash fingerprint,
        IReadOnlyList<StructuralFact> facts)
    {
        if (contractVersion != StructuralCompatibilityContract.Version)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                $"Structural signature contract version must be {StructuralCompatibilityContract.Version}.");
        }

        ContractVersion = contractVersion;
        Family = StructuralContractText.NormalizeIdentifier(family, nameof(family));
        Facts = CanonicalizeFacts(facts);
        var expectedFingerprint = ComputeFingerprint(Family, Facts);
        var expectedSignatureId = $"sig_{expectedFingerprint.Value[..24]}";
        if (fingerprint != expectedFingerprint)
        {
            throw new ArgumentException("Structural signature fingerprint does not match its canonical facts.", nameof(fingerprint));
        }

        if (!string.Equals(signatureId, expectedSignatureId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Structural signature ID does not match its canonical fingerprint.", nameof(signatureId));
        }

        SignatureId = signatureId;
        Fingerprint = fingerprint;
    }

    public int ContractVersion { get; }

    public string Family { get; }

    public string SignatureId { get; }

    public ContentHash Fingerprint { get; }

    public IReadOnlyList<StructuralFact> Facts { get; }

    public static StructuralSignature Create(string family, IEnumerable<StructuralFact> facts)
    {
        var normalizedFamily = StructuralContractText.NormalizeIdentifier(family, nameof(family));
        var canonicalFacts = CanonicalizeFacts(facts?.ToArray() ?? throw new ArgumentNullException(nameof(facts)));
        var fingerprint = ComputeFingerprint(normalizedFamily, canonicalFacts);
        return new StructuralSignature(
            StructuralCompatibilityContract.Version,
            normalizedFamily,
            $"sig_{fingerprint.Value[..24]}",
            fingerprint,
            canonicalFacts);
    }

    private static StructuralFact[] CanonicalizeFacts(IReadOnlyList<StructuralFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            throw new ArgumentException("A structural signature requires at least one fact.", nameof(facts));
        }

        if (facts.Any(static fact => fact is null))
        {
            throw new ArgumentException("Structural facts cannot contain null entries.", nameof(facts));
        }

        var ordered = facts
            .OrderBy(static fact => fact.Key, StringComparer.Ordinal)
            .ThenBy(static fact => fact.Value, StringComparer.Ordinal)
            .ToArray();
        var duplicate = ordered
            .GroupBy(static fact => fact.Key, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Structural fact key '{duplicate.Key}' appears more than once.",
                nameof(facts));
        }

        return ordered;
    }

    private static ContentHash ComputeFingerprint(string family, IReadOnlyList<StructuralFact> facts)
    {
        var seed = new StringBuilder("structural_compatibility@1\n");
        AppendLengthPrefixed(seed, family);
        foreach (var fact in facts)
        {
            AppendLengthPrefixed(seed, fact.Key);
            AppendLengthPrefixed(seed, fact.Value);
        }

        return ContentHash.Compute(Encoding.UTF8.GetBytes(seed.ToString()));
    }

    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
}

public sealed record StructuralProfileMatch
{
    public StructuralProfileMatch(
        StructuralProfileIdentity profile,
        string status,
        IReadOnlyList<StructuralCompatibilityReason> reasons)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (!StructuralCompatibilityContract.IsProfileMatchStatus(status))
        {
            throw new ArgumentException("Structural profile match status is not recognized.", nameof(status));
        }

        Status = status;
        Reasons = StructuralContractText.CanonicalizeReasons(reasons, nameof(reasons));
    }

    public StructuralProfileIdentity Profile { get; }

    public string Status { get; }

    public IReadOnlyList<StructuralCompatibilityReason> Reasons { get; }
}

public sealed record OperationCompatibility
{
    public OperationCompatibility(
        string operationKind,
        int operationVersion,
        string availability,
        IReadOnlyList<StructuralCompatibilityReason> reasons)
    {
        OperationKind = StructuralContractText.NormalizeIdentifier(operationKind, nameof(operationKind));
        if (operationVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationVersion), "An operation version must be positive.");
        }

        if (!CapabilityAvailability.IsDefined(availability))
        {
            throw new ArgumentException("Operation capability availability is not recognized.", nameof(availability));
        }

        OperationVersion = operationVersion;
        Availability = availability;
        Reasons = StructuralContractText.CanonicalizeReasons(reasons, nameof(reasons));
    }

    public string OperationKind { get; }

    public int OperationVersion { get; }

    public string Availability { get; }

    public IReadOnlyList<StructuralCompatibilityReason> Reasons { get; }
}

public sealed record StructuralCompatibilityAssessment
{
    public StructuralCompatibilityAssessment(
        StructuralSignature signature,
        IReadOnlyList<StructuralProfileMatch> profileMatches,
        IReadOnlyList<OperationCompatibility> operationCapabilities)
    {
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));
        ProfileMatches = CanonicalizeProfileMatches(profileMatches);
        OperationCapabilities = CanonicalizeOperationCapabilities(operationCapabilities);
    }

    public StructuralSignature Signature { get; }

    public IReadOnlyList<StructuralProfileMatch> ProfileMatches { get; }

    public IReadOnlyList<OperationCompatibility> OperationCapabilities { get; }

    private static StructuralProfileMatch[] CanonicalizeProfileMatches(
        IReadOnlyList<StructuralProfileMatch> profileMatches)
    {
        ArgumentNullException.ThrowIfNull(profileMatches);
        var ordered = profileMatches
            .OrderBy(static match => match.Profile.ProfileId, StringComparer.Ordinal)
            .ThenBy(static match => match.Profile.Version)
            .ToArray();
        var duplicate = ordered
            .GroupBy(static match => (match.Profile.ProfileId, match.Profile.Version))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Structural profile '{duplicate.Key.ProfileId}@{duplicate.Key.Version}' appears more than once.",
                nameof(profileMatches));
        }

        return ordered;
    }

    private static OperationCompatibility[] CanonicalizeOperationCapabilities(
        IReadOnlyList<OperationCompatibility> operationCapabilities)
    {
        ArgumentNullException.ThrowIfNull(operationCapabilities);
        var ordered = operationCapabilities
            .OrderBy(static capability => capability.OperationKind, StringComparer.Ordinal)
            .ThenBy(static capability => capability.OperationVersion)
            .ToArray();
        var duplicate = ordered
            .GroupBy(static capability => (capability.OperationKind, capability.OperationVersion))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Operation capability '{duplicate.Key.OperationKind}@{duplicate.Key.OperationVersion}' appears more than once.",
                nameof(operationCapabilities));
        }

        return ordered;
    }
}

internal static class StructuralContractText
{
    private const int MaximumIdentifierLength = 128;
    private const int MaximumTextLength = 1024;

    public static string NormalizeIdentifier(string value, string parameterName)
    {
        var normalized = NormalizeText(value, parameterName).ToLowerInvariant();
        if (normalized.Length > MaximumIdentifierLength
            || !char.IsAsciiLetter(normalized[0])
            || normalized.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                $"{parameterName} must be a lower-case portable identifier of at most {MaximumIdentifierLength} characters.",
                parameterName);
        }

        return normalized;
    }

    public static string NormalizeReasonCode(string value, string parameterName)
    {
        var normalized = NormalizeText(value, parameterName).ToUpperInvariant();
        if (!char.IsAsciiLetter(normalized[0])
            || normalized.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("A compatibility reason code must contain only A-Z, 0-9, and underscores.", parameterName);
        }

        return normalized;
    }

    public static string NormalizeText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized;
        try
        {
            normalized = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Contract text is not valid Unicode.", parameterName, exception);
        }

        if (normalized.Length is 0 or > MaximumTextLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must contain 1-{MaximumTextLength} non-control characters after normalization.",
                parameterName);
        }

        return normalized;
    }

    public static IReadOnlyList<StructuralCompatibilityReason> CanonicalizeReasons(
        IReadOnlyList<StructuralCompatibilityReason> reasons,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reasons, parameterName);
        if (reasons.Count == 0 || reasons.Any(static reason => reason is null))
        {
            throw new ArgumentException("At least one non-null compatibility reason is required.", parameterName);
        }

        var ordered = reasons
            .OrderBy(static reason => reason.Code, StringComparer.Ordinal)
            .ThenBy(static reason => reason.Summary, StringComparer.Ordinal)
            .ToArray();
        var duplicate = ordered
            .GroupBy(static reason => reason.Code, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Compatibility reason code '{duplicate.Key}' appears more than once.", parameterName);
        }

        return ordered;
    }
}

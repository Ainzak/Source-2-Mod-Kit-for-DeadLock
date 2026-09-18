namespace S2ModKit.Domain;

public enum ErrorCategory
{
    CliOrSchema = 2,
    InputOrResolution = 10,
    UnsupportedCapability = 20,
    SelectionOrLod = 30,
    RewriteOrVerification = 40,
    Unexpected = 70,
}

public sealed record S2Error(
    string Code,
    string Boundary,
    string Summary,
    string Remediation,
    ErrorCategory Category,
    IReadOnlyDictionary<string, string>? Context = null);

public sealed class S2ModKitException : Exception
{
    public S2ModKitException(S2Error error, Exception? innerException = null)
        : base(error.Summary, innerException)
    {
        Error = error;
    }

    public S2Error Error { get; }
}

public static class Errors
{
    public static S2ModKitException InvalidRecipe(string code, string summary, string remediation) =>
        new(new S2Error(code, "recipe", summary, remediation, ErrorCategory.CliOrSchema));

    public static S2ModKitException Input(string code, string summary, string remediation) =>
        new(new S2Error(code, "input", summary, remediation, ErrorCategory.InputOrResolution));

    public static S2ModKitException Unsupported(string code, string summary, string remediation) =>
        new(new S2Error(code, "source2_adapter", summary, remediation, ErrorCategory.UnsupportedCapability));

    public static S2ModKitException Selection(string code, string summary, string remediation) =>
        new(new S2Error(code, "selection", summary, remediation, ErrorCategory.SelectionOrLod));

    public static S2ModKitException Verification(string code, string summary, string remediation) =>
        new(new S2Error(code, "verification", summary, remediation, ErrorCategory.RewriteOrVerification));
}


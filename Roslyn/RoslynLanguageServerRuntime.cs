using System.Security.Cryptography;

namespace SystemExplorer.CodeService;

internal enum RoslynLanguageServerRuntimeSource
{
    ExplicitOverride,
    PackagedPrivateRuntime,
}

internal sealed class RoslynLanguageServerRuntime
{
    public const string UpstreamCommit = "3aeb96c9ecc56a5ee483558f9e648e33e7bfe756";
    public const string SemanticReusePatchSha256 = "11076630B66576961CFD3E56120B15C9E95B352E08F3F551053A79A647D2F2BE";
    public const string SemanticReuseSourceCommit = "405fb7f9860";
    public const string CompletionSemanticOriginPatchSha256 = "6818CC1B3A10C97B31782CCE20B7590A4A7F1B39710D7B48DD5B234E1B3BC1FB";
    public const string CurrentSourceFrozenPartialPatchSha256 = "17827506D20D05B63764C3959A698E35584776FC5C3FB559E70B9B9FFCBDB4E6";
    public const string CompletionIncrementalReusePatchSha256 = "39D4217634DCF32304E071B1D8E01FA42778461CA903B07544490E451807F6EC";

    // Materialized production identity from the canonical private Roslyn v4 build.
    // Runtime and pack validation both fail closed if these exact binaries are not present.
    public const string DistributionId = "roslyn-3aeb96c9-systemexplorer-39d4217634dc-win-x64-v4";
    public const string LanguageServerDllSha256 = "B51A8B22928662ED85B8CB4F4FDC252060498BC4B88AA005CE77EBA98CA3F409";
    public const string FeaturesDllSha256 = "AB13A3A17361A76BC37144B02ABDD53E3CAD31905DA6DB70B67F0B1B37177235";
    public const string LanguageServerProtocolDllSha256 = "CE445673436692442A09F84211579FA8B559F0E1728284A64E8A588F6F219FA6";

    private const string ServerDllFileName = "Microsoft.CodeAnalysis.LanguageServer.dll";
    private const string FeaturesDllFileName = "Microsoft.CodeAnalysis.Features.dll";
    private const string LanguageServerProtocolDllFileName = "Microsoft.CodeAnalysis.LanguageServer.Protocol.dll";
    private const string DepsFileName = "Microsoft.CodeAnalysis.LanguageServer.deps.json";
    private const string RuntimeConfigFileName = "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json";
    private const string PendingRuntimeHashSentinel = "V4_BUILD_REQUIRED";
    private const string PendingDistributionIdMarker = "SOURCE-BUILD-REQUIRED";

    private RoslynLanguageServerRuntime(
        RoslynLanguageServerRuntimeSource runtimeSource,
        string runtimeDirectory,
        string serverDllPath,
        string featuresDllPath,
        string languageServerProtocolDllPath,
        string depsJsonPath,
        string runtimeConfigJsonPath)
    {
        RuntimeSource = runtimeSource;
        RuntimeDirectory = runtimeDirectory;
        ServerDllPath = serverDllPath;
        FeaturesDllPath = featuresDllPath;
        LanguageServerProtocolDllPath = languageServerProtocolDllPath;
        DepsJsonPath = depsJsonPath;
        RuntimeConfigJsonPath = runtimeConfigJsonPath;
    }

    public RoslynLanguageServerRuntimeSource RuntimeSource { get; }

    public string RuntimeDistributionId => DistributionId;

    public string RuntimeDirectory { get; }

    public string ServerDllPath { get; }

    public string FeaturesDllPath { get; }

    public string LanguageServerProtocolDllPath { get; }

    public string DepsJsonPath { get; }

    public string RuntimeConfigJsonPath { get; }

    public string VerifiedLanguageServerDllSha256 => LanguageServerDllSha256;

    public string VerifiedFeaturesDllSha256 => FeaturesDllSha256;

    public string VerifiedLanguageServerProtocolDllSha256 => LanguageServerProtocolDllSha256;

    public string VerifiedUpstreamCommit => UpstreamCommit;

    public string VerifiedSemanticReusePatchSha256 => SemanticReusePatchSha256;

    public string VerifiedSemanticReuseSourceCommit => SemanticReuseSourceCommit;

    public string VerifiedCompletionSemanticOriginPatchSha256 => CompletionSemanticOriginPatchSha256;

    public string VerifiedCurrentSourceFrozenPartialPatchSha256 => CurrentSourceFrozenPartialPatchSha256;

    public string VerifiedCompletionIncrementalReusePatchSha256 => CompletionIncrementalReusePatchSha256;

    public static RoslynLanguageServerRuntimeValidationResult TryValidate(
        string? runtimeDirectory,
        RoslynLanguageServerRuntimeSource runtimeSource = RoslynLanguageServerRuntimeSource.ExplicitOverride)
    {
        if (LanguageServerDllSha256 == PendingRuntimeHashSentinel
            || FeaturesDllSha256 == PendingRuntimeHashSentinel
            || LanguageServerProtocolDllSha256 == PendingRuntimeHashSentinel
            || DistributionId.Contains(PendingDistributionIdMarker, StringComparison.Ordinal))
        {
            return RoslynLanguageServerRuntimeValidationResult.Failure(
                "Roslyn v4 source adoption is present, but build-derived runtime identity values have not been materialized yet.");
        }

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return RoslynLanguageServerRuntimeValidationResult.Failure(
                "Roslyn runtime directory must be a non-empty fully-qualified absolute path.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(runtimeDirectory))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    "Roslyn runtime directory must be a fully-qualified absolute path.");
            }

            string normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(runtimeDirectory));

            if (!Directory.Exists(normalizedDirectory))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    "Roslyn runtime directory does not exist.");
            }

            string serverDllPath = Path.Combine(normalizedDirectory, ServerDllFileName);
            string featuresDllPath = Path.Combine(normalizedDirectory, FeaturesDllFileName);
            string languageServerProtocolDllPath = Path.Combine(normalizedDirectory, LanguageServerProtocolDllFileName);
            string depsJsonPath = Path.Combine(normalizedDirectory, DepsFileName);
            string runtimeConfigJsonPath = Path.Combine(normalizedDirectory, RuntimeConfigFileName);

            string? missingFile = GetMissingRequiredFile(
                serverDllPath,
                featuresDllPath,
                languageServerProtocolDllPath,
                depsJsonPath,
                runtimeConfigJsonPath);
            if (missingFile is not null)
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    $"Roslyn runtime is missing required file '{missingFile}'.");
            }

            string? hashError = GetSha256MismatchError(
                serverDllPath,
                ServerDllFileName,
                LanguageServerDllSha256);
            if (hashError is not null)
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(hashError);
            }

            hashError = GetSha256MismatchError(
                featuresDllPath,
                FeaturesDllFileName,
                FeaturesDllSha256);
            if (hashError is not null)
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(hashError);
            }

            hashError = GetSha256MismatchError(
                languageServerProtocolDllPath,
                LanguageServerProtocolDllFileName,
                LanguageServerProtocolDllSha256);
            if (hashError is not null)
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(hashError);
            }

            return RoslynLanguageServerRuntimeValidationResult.Success(
                new RoslynLanguageServerRuntime(
                    runtimeSource,
                    normalizedDirectory,
                    serverDllPath,
                    featuresDllPath,
                    languageServerProtocolDllPath,
                    depsJsonPath,
                    runtimeConfigJsonPath));
        }
        catch (Exception exception) when (IsControlledValidationException(exception))
        {
            return RoslynLanguageServerRuntimeValidationResult.Failure(
                $"Roslyn runtime validation failed: {ToSingleLine(exception.Message)}");
        }
    }

    private static string? GetMissingRequiredFile(
        string serverDllPath,
        string featuresDllPath,
        string languageServerProtocolDllPath,
        string depsJsonPath,
        string runtimeConfigJsonPath)
    {
        if (!File.Exists(serverDllPath))
        {
            return ServerDllFileName;
        }
        if (!File.Exists(featuresDllPath))
        {
            return FeaturesDllFileName;
        }
        if (!File.Exists(languageServerProtocolDllPath))
        {
            return LanguageServerProtocolDllFileName;
        }
        if (!File.Exists(depsJsonPath))
        {
            return DepsFileName;
        }
        if (!File.Exists(runtimeConfigJsonPath))
        {
            return RuntimeConfigFileName;
        }

        return null;
    }

    private static string? GetSha256MismatchError(
        string path,
        string fileName,
        string expectedHash)
    {
        string actualHash = ComputeSha256(path);
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Roslyn runtime file '{fileName}' SHA-256 mismatch; expected {expectedHash}, actual {actualHash}.";
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        using SHA256 sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static bool IsControlledValidationException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException
            or CryptographicException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct RoslynLanguageServerRuntimeValidationResult(
    RoslynLanguageServerRuntime? Runtime,
    string? ErrorMessage)
{
    public bool IsSuccess => Runtime is not null;

    public static RoslynLanguageServerRuntimeValidationResult Success(
        RoslynLanguageServerRuntime runtime)
        => new(runtime, null);

    public static RoslynLanguageServerRuntimeValidationResult Failure(string errorMessage)
        => new(null, errorMessage);
}

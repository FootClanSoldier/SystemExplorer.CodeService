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
    public const string ImportCompletionContractPatchSha256 = "608B4EFA8B50E85EFBD9A7D6CF2BF0C31200809EE7D42E2C9B243A40858770B0";
    public const string ImportCompletionReadinessPatchSha256 = "8DD66DA05D857ECB0737973C343F04A9D20BB7B4C35DA5A010E9CA4109405524";
    public const string ReceiverRelativeSemanticOriginPatchSha256 = "CD4F905C4B2B60CEC000DCAAD83241D18D151EDB1AFF625F4588EFFCC180FE3C";
    public const string TypeReceiverSemanticOriginPatchSha256 = "DF87DA9CF8F7A02217E71341734AE892D653506838680A2768C1177782FBD400";
    public const string QualifiedNameReceiverRecoveryPatchSha256 = "2B9EE3AFF616702AC2B40A3FC1BA70EEDB81C006D891F144B0580C3BA53B4FFD";
    public const string ImportCompletionPathExclusionPatchSha256 = "6276FF5707AC41F47FAB8E8298A226F2486D9FF2746ECEC54568A82F1C7CAAF6";
    public const string CompletionSourceExclusionPatchSha256 = "AEAFDDD7B52A8C1B44A455965E7C5D4B48B5E7E795291EA55F1F3BDC3D3EA054";
    public const string CompletionMethodShapePatchSha256 = "322210505AF78564ED4A2FF4F86099ABCBAFCAE304D35F4F417B64F3435205ED";

    // Materialized production identity from the canonical private Roslyn v12 build.
    // Runtime and pack validation both fail closed if these exact binaries are not present.
    public const string DistributionId = "roslyn-3aeb96c9-systemexplorer-322210505af7-win-x64-v12";
    public const string LanguageServerDllSha256 = "18D22694B282763AEDFD18A92343D7B97C5BA3B82128C7850BAE90DB2FF84871";
    public const string FeaturesDllSha256 = "D181C712F006C16F6EA63A04EFFC8FBFECD632E5A75FA6A5F16949EE4D3A0A56";
    public const string LanguageServerProtocolDllSha256 = "DEC57627912136464BDC2F8957EA12281DAF75AE0C27AB5B31E2EFCE1C9BD5F6";

    private const string ServerDllFileName = "Microsoft.CodeAnalysis.LanguageServer.dll";
    private const string FeaturesDllFileName = "Microsoft.CodeAnalysis.Features.dll";
    private const string LanguageServerProtocolDllFileName = "Microsoft.CodeAnalysis.LanguageServer.Protocol.dll";
    private const string DepsFileName = "Microsoft.CodeAnalysis.LanguageServer.deps.json";
    private const string RuntimeConfigFileName = "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json";

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

    public string VerifiedImportCompletionContractPatchSha256 => ImportCompletionContractPatchSha256;

    public string VerifiedImportCompletionReadinessPatchSha256 => ImportCompletionReadinessPatchSha256;

    public string VerifiedReceiverRelativeSemanticOriginPatchSha256 => ReceiverRelativeSemanticOriginPatchSha256;

    public string VerifiedTypeReceiverSemanticOriginPatchSha256 => TypeReceiverSemanticOriginPatchSha256;

    public string VerifiedQualifiedNameReceiverRecoveryPatchSha256 => QualifiedNameReceiverRecoveryPatchSha256;

    public string VerifiedImportCompletionPathExclusionPatchSha256 => ImportCompletionPathExclusionPatchSha256;

    public string VerifiedCompletionSourceExclusionPatchSha256 => CompletionSourceExclusionPatchSha256;

    public string VerifiedCompletionMethodShapePatchSha256 => CompletionMethodShapePatchSha256;

    public static RoslynLanguageServerRuntimeValidationResult TryValidate(
        string? runtimeDirectory,
        RoslynLanguageServerRuntimeSource runtimeSource = RoslynLanguageServerRuntimeSource.ExplicitOverride)
    {
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

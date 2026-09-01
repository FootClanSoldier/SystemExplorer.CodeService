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
    public const string FixedDllSha256 = "4012B886966A1384E2371186E7C38AA5AF8ED26072DBBD5CA739F2C4470A7467";
    public const string CanonicalFixPatchSha256 = "CB6D1A37CE530D40212CB7537A3A93BE1DA97FFA235CAF8520A5230BF028FE71";
    public const string LocalFixCommit = "405fb7f9860";
    public const string DistributionId = "roslyn-3aeb96c9-systemexplorer-405fb7f9860-win-x64-v1";

    private const string ServerDllFileName = "Microsoft.CodeAnalysis.LanguageServer.dll";
    private const string DepsFileName = "Microsoft.CodeAnalysis.LanguageServer.deps.json";
    private const string RuntimeConfigFileName = "Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json";

    private RoslynLanguageServerRuntime(
        RoslynLanguageServerRuntimeSource runtimeSource,
        string runtimeDirectory,
        string serverDllPath,
        string depsJsonPath,
        string runtimeConfigJsonPath)
    {
        RuntimeSource = runtimeSource;
        RuntimeDirectory = runtimeDirectory;
        ServerDllPath = serverDllPath;
        DepsJsonPath = depsJsonPath;
        RuntimeConfigJsonPath = runtimeConfigJsonPath;
    }

    public RoslynLanguageServerRuntimeSource RuntimeSource { get; }

    public string RuntimeDistributionId => DistributionId;

    public string RuntimeDirectory { get; }

    public string ServerDllPath { get; }

    public string DepsJsonPath { get; }

    public string RuntimeConfigJsonPath { get; }

    public string VerifiedDllSha256 => FixedDllSha256;

    public string VerifiedUpstreamCommit => UpstreamCommit;

    public string VerifiedCanonicalFixPatchSha256 => CanonicalFixPatchSha256;

    public string VerifiedLocalFixCommit => LocalFixCommit;

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
            string depsJsonPath = Path.Combine(normalizedDirectory, DepsFileName);
            string runtimeConfigJsonPath = Path.Combine(normalizedDirectory, RuntimeConfigFileName);

            if (!File.Exists(serverDllPath))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    $"Roslyn runtime is missing required file '{ServerDllFileName}'.");
            }

            if (!File.Exists(depsJsonPath))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    $"Roslyn runtime is missing required file '{DepsFileName}'.");
            }

            if (!File.Exists(runtimeConfigJsonPath))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    $"Roslyn runtime is missing required file '{RuntimeConfigFileName}'.");
            }

            string actualHash = ComputeSha256(serverDllPath);
            if (!string.Equals(actualHash, FixedDllSha256, StringComparison.OrdinalIgnoreCase))
            {
                return RoslynLanguageServerRuntimeValidationResult.Failure(
                    $"Roslyn Language Server DLL SHA-256 mismatch; expected {FixedDllSha256}, actual {actualHash}.");
            }

            return RoslynLanguageServerRuntimeValidationResult.Success(
                new RoslynLanguageServerRuntime(
                    runtimeSource,
                    normalizedDirectory,
                    serverDllPath,
                    depsJsonPath,
                    runtimeConfigJsonPath));
        }
        catch (Exception exception) when (IsControlledValidationException(exception))
        {
            return RoslynLanguageServerRuntimeValidationResult.Failure(
                $"Roslyn runtime validation failed: {ToSingleLine(exception.Message)}");
        }
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

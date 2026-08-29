using System.Runtime.Versioning;
using System.Security;

namespace SystemExplorer.CodeService;

internal sealed class WorkspaceCacheAuthority : IDisposable
{
    private const long LockPosition = 0;
    private const long LockLength = 1;

    private FileStream? _stream;

    private WorkspaceCacheAuthority(FileStream stream, string authorityPath)
    {
        _stream = stream;
        AuthorityPath = authorityPath;
    }

    public string AuthorityPath { get; }

    [UnsupportedOSPlatformGuard("macos")]
    [UnsupportedOSPlatformGuard("ios")]
    [UnsupportedOSPlatformGuard("tvos")]
    [UnsupportedOSPlatformGuard("freebsd")]
    internal static bool IsByteRangeLockSupported()
        => !(OperatingSystem.IsMacOS()
            || OperatingSystem.IsIOS()
            || OperatingSystem.IsTvOS()
            || OperatingSystem.IsFreeBSD());

    public static WorkspaceCacheAuthorityAcquisitionResult TryAcquire(
        WorkspaceCacheLocation cacheLocation)
    {
        if (!IsByteRangeLockSupported())
        {
            return WorkspaceCacheAuthorityAcquisitionResult.Failure(
                "the current FileStream byte-range workspace-cache authority candidate is not supported on this platform.");
        }

        FileStream? stream = null;

        try
        {
            Directory.CreateDirectory(cacheLocation.WorkspaceDirectory);
            stream = new FileStream(
                cacheLocation.MaintenanceAuthorityPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None);

            stream.Lock(LockPosition, LockLength);

            WorkspaceCacheAuthority authority = new(
                stream,
                cacheLocation.MaintenanceAuthorityPath);
            stream = null;
            return WorkspaceCacheAuthorityAcquisitionResult.Success(authority);
        }
        catch (Exception exception) when (IsExpectedAcquisitionFailure(exception))
        {
            return WorkspaceCacheAuthorityAcquisitionResult.Failure(
                $"workspace cache authority was not acquired: {ToSingleLine(exception.Message)}");
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // Failed acquisition cleanup must not escape the bounded arbitration attempt.
                }
            }
        }
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }

    private static bool IsExpectedAcquisitionFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException
            or ArgumentException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct WorkspaceCacheAuthorityAcquisitionResult(
    WorkspaceCacheAuthority? Authority,
    string? ErrorMessage)
{
    public bool IsSuccess => Authority is not null;

    public static WorkspaceCacheAuthorityAcquisitionResult Success(
        WorkspaceCacheAuthority authority)
        => new(authority, null);

    public static WorkspaceCacheAuthorityAcquisitionResult Failure(string errorMessage)
        => new(null, errorMessage);
}

using System.Security;

namespace SystemExplorer.CodeService;

internal sealed class PerOwnerLaunchAuthority : IDisposable
{
    private const long LockPosition = 0;
    private const long LockLength = 1;

    private FileStream? _claimStream;

    private PerOwnerLaunchAuthority(FileStream claimStream, string claimPath)
    {
        _claimStream = claimStream;
        ClaimPath = claimPath;
    }

    internal string ClaimPath { get; }

    public static PerOwnerLaunchAuthorityAcquisitionResult TryAcquire(
        GodotProcessIdentity ownerIdentity)
    {
        if (OperatingSystem.IsMacOS()
            || OperatingSystem.IsIOS()
            || OperatingSystem.IsTvOS()
            || OperatingSystem.IsFreeBSD())
        {
            return PerOwnerLaunchAuthorityAcquisitionResult.Failure(
                "the current FileStream byte-range launch-authority candidate is not supported on this platform.");
        }

        FileStream? claimStream = null;

        try
        {
            string authorityDirectory = SessionRuntimePathResolver.ResolveLaunchAuthorityDirectory();
            Directory.CreateDirectory(authorityDirectory);

            string claimPath = Path.Combine(
                authorityDirectory,
                $"owner_{ownerIdentity.ProcessId}_{ownerIdentity.StartTimeUtcTicks}.claim");

            claimStream = new FileStream(
                claimPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None);

            claimStream.Lock(LockPosition, LockLength);

            PerOwnerLaunchAuthority authority = new(claimStream, claimPath);
            claimStream = null;
            return PerOwnerLaunchAuthorityAcquisitionResult.Success(authority);
        }
        catch (Exception exception) when (IsExpectedAcquisitionFailure(exception))
        {
            return PerOwnerLaunchAuthorityAcquisitionResult.Failure(
                $"per-owner launch authority was not acquired: {ToSingleLine(exception.Message)}");
        }
        finally
        {
            claimStream?.Dispose();
        }
    }

    internal static bool TryDeleteStaleClaimWithExclusiveLock(string claimPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        FileStream? cleanupStream = null;

        try
        {
            cleanupStream = new FileStream(
                claimPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);

            cleanupStream.Lock(LockPosition, LockLength);
            File.Delete(claimPath);
            return true;
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            return false;
        }
        finally
        {
            cleanupStream?.Dispose();
        }
    }

    public void Dispose()
    {
        FileStream? claimStream = Interlocked.Exchange(ref _claimStream, null);
        claimStream?.Dispose();
    }

    private static bool IsExpectedAcquisitionFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException;

    private static bool IsExpectedCleanupFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException
            or ArgumentException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct PerOwnerLaunchAuthorityAcquisitionResult(
    PerOwnerLaunchAuthority? Authority,
    string? ErrorMessage)
{
    public bool IsSuccess => Authority is not null;

    public static PerOwnerLaunchAuthorityAcquisitionResult Success(
        PerOwnerLaunchAuthority authority)
        => new(authority, null);

    public static PerOwnerLaunchAuthorityAcquisitionResult Failure(string errorMessage)
        => new(null, errorMessage);
}

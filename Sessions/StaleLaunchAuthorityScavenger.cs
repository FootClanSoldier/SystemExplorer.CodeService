using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;

namespace SystemExplorer.CodeService;

internal static class StaleLaunchAuthorityScavenger
{
    private const int MaxClaimEntriesExamined = 4096;
    private const string ClaimPrefix = "owner_";
    private const string ClaimSuffix = ".claim";

    public static void Scavenge(GodotProcessIdentity currentOwnerIdentity)
    {
        string authorityDirectory = SessionRuntimePathResolver.ResolveLaunchAuthorityDirectory();
        int examined = 0;

        try
        {
            foreach (string claimPath in Directory.EnumerateFiles(
                authorityDirectory,
                "*.claim",
                SearchOption.TopDirectoryOnly))
            {
                if (examined >= MaxClaimEntriesExamined)
                {
                    break;
                }

                examined++;

                if (!TryParseClaimIdentity(Path.GetFileName(claimPath), out GodotProcessIdentity claimIdentity))
                {
                    continue;
                }

                if (claimIdentity.ProcessId == currentOwnerIdentity.ProcessId
                    && claimIdentity.StartTimeUtcTicks == currentOwnerIdentity.StartTimeUtcTicks)
                {
                    continue;
                }

                ExactOwnerProcessObservation observation = ObserveExactOwnerProcess(claimIdentity);
                if (observation != ExactOwnerProcessObservation.ExactInstanceDead)
                {
                    continue;
                }

                if (!OperatingSystem.IsWindows())
                {
                    continue;
                }

                PerOwnerLaunchAuthority.TryDeleteStaleClaimWithExclusiveLock(claimPath);
            }
        }
        catch (Exception exception) when (IsExpectedTraversalFailure(exception))
        {
            // Best-effort maintenance: preserve remaining artifacts and continue startup.
        }
    }

    private static bool TryParseClaimIdentity(
        string fileName,
        out GodotProcessIdentity identity)
    {
        identity = default;

        if (!fileName.StartsWith(ClaimPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(ClaimSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string payload = fileName.Substring(
            ClaimPrefix.Length,
            fileName.Length - ClaimPrefix.Length - ClaimSuffix.Length);

        int separatorIndex = payload.IndexOf('_');
        if (separatorIndex <= 0
            || separatorIndex != payload.LastIndexOf('_')
            || separatorIndex >= payload.Length - 1)
        {
            return false;
        }

        string processIdText = payload[..separatorIndex];
        string startTimeText = payload[(separatorIndex + 1)..];

        if (!int.TryParse(
                processIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId)
            || !long.TryParse(
                startTimeText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long startTimeUtcTicks)
            || processId <= 0
            || startTimeUtcTicks <= 0
            || !string.Equals(
                processIdText,
                processId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(
                startTimeText,
                startTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return false;
        }

        identity = new GodotProcessIdentity(processId, startTimeUtcTicks);
        return true;
    }

    private static ExactOwnerProcessObservation ObserveExactOwnerProcess(
        GodotProcessIdentity claimIdentity)
    {
        Process? candidateProcess = null;

        try
        {
            try
            {
                candidateProcess = Process.GetProcessById(claimIdentity.ProcessId);
            }
            catch (ArgumentException)
            {
                return ExactOwnerProcessObservation.ExactInstanceDead;
            }
            catch (Exception exception) when (IsAmbiguousProcessObservationFailure(exception))
            {
                return ExactOwnerProcessObservation.Ambiguous;
            }

            try
            {
                if (candidateProcess.HasExited)
                {
                    return ExactOwnerProcessObservation.ExactInstanceDead;
                }

                long actualStartTimeUtcTicks = candidateProcess.StartTime.ToUniversalTime().Ticks;
                if (actualStartTimeUtcTicks != claimIdentity.StartTimeUtcTicks)
                {
                    return ExactOwnerProcessObservation.ExactInstanceDead;
                }

                if (candidateProcess.HasExited)
                {
                    return ExactOwnerProcessObservation.ExactInstanceDead;
                }

                return ExactOwnerProcessObservation.ExactInstanceAlive;
            }
            catch (Exception exception) when (IsAmbiguousProcessObservationFailure(exception))
            {
                return ExactOwnerProcessObservation.Ambiguous;
            }
        }
        finally
        {
            candidateProcess?.Dispose();
        }
    }

    private static bool IsAmbiguousProcessObservationFailure(Exception exception)
        => exception is InvalidOperationException
            or Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException
            or IOException
            or ArgumentException;

    private static bool IsExpectedTraversalFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException
            or ArgumentException;

    private enum ExactOwnerProcessObservation
    {
        ExactInstanceAlive,
        ExactInstanceDead,
        Ambiguous,
    }
}

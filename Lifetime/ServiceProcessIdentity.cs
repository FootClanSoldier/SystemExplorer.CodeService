using System.ComponentModel;
using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal readonly record struct ServiceProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks)
{
    public static ServiceProcessIdentityCaptureResult TryCaptureCurrent()
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();

            int processId = currentProcess.Id;
            long startTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks;

            if (processId <= 0 || startTimeUtcTicks <= 0)
            {
                return ServiceProcessIdentityCaptureResult.Failure(
                    "current CodeService process identity was invalid.");
            }

            if (Environment.ProcessId != processId)
            {
                return ServiceProcessIdentityCaptureResult.Failure(
                    "current CodeService process identity was inconsistent.");
            }

            return ServiceProcessIdentityCaptureResult.Success(
                new ServiceProcessIdentity(processId, startTimeUtcTicks));
        }
        catch (Exception exception) when (IsExpectedProcessObservationFailure(exception))
        {
            return ServiceProcessIdentityCaptureResult.Failure(
                $"could not capture current CodeService process identity: {ToSingleLine(exception.Message)}");
        }
    }

    private static bool IsExpectedProcessObservationFailure(Exception exception)
        => exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException
            or UnauthorizedAccessException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct ServiceProcessIdentityCaptureResult(
    ServiceProcessIdentity? Identity,
    string? ErrorMessage)
{
    public bool IsSuccess => Identity.HasValue;

    public static ServiceProcessIdentityCaptureResult Success(ServiceProcessIdentity identity)
        => new(identity, null);

    public static ServiceProcessIdentityCaptureResult Failure(string errorMessage)
        => new(null, errorMessage);
}

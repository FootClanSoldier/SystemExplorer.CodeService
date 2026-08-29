using System.ComponentModel;
using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class GodotProcessLifetime : IDisposable
{
    private readonly Process _validatedOwnerProcess;
    private bool _disposed;

    private GodotProcessLifetime(Process validatedOwnerProcess)
    {
        _validatedOwnerProcess = validatedOwnerProcess;
    }

    public static GodotProcessLifetimeAttachResult TryAttach(GodotProcessIdentity expectedIdentity)
    {
        if (expectedIdentity.ProcessId == Environment.ProcessId)
        {
            return GodotProcessLifetimeAttachResult.Failure(
                "the CodeService process cannot be its own Godot lifetime owner.");
        }

        Process? candidateProcess = null;

        try
        {
            candidateProcess = Process.GetProcessById(expectedIdentity.ProcessId);

            if (candidateProcess.HasExited)
            {
                return GodotProcessLifetimeAttachResult.Failure(
                    $"Godot owner process {expectedIdentity.ProcessId} has already exited.");
            }

            long actualStartTimeUtcTicks = candidateProcess.StartTime.ToUniversalTime().Ticks;
            if (actualStartTimeUtcTicks != expectedIdentity.StartTimeUtcTicks)
            {
                return GodotProcessLifetimeAttachResult.Failure(
                    $"Godot owner process {expectedIdentity.ProcessId} start identity does not match.");
            }

            if (candidateProcess.HasExited)
            {
                return GodotProcessLifetimeAttachResult.Failure(
                    $"Godot owner process {expectedIdentity.ProcessId} exited during validation.");
            }

            GodotProcessLifetime lifetime = new(candidateProcess);
            candidateProcess = null;
            return GodotProcessLifetimeAttachResult.Success(lifetime);
        }
        catch (Exception exception) when (IsExpectedProcessObservationFailure(exception))
        {
            return GodotProcessLifetimeAttachResult.Failure(
                $"could not validate Godot owner process {expectedIdentity.ProcessId}: {exception.Message}");
        }
        finally
        {
            candidateProcess?.Dispose();
        }
    }

    public async Task WaitForOwnerExitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _validatedOwnerProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _validatedOwnerProcess.Dispose();
    }

    private static bool IsExpectedProcessObservationFailure(Exception exception)
        => exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException
            or UnauthorizedAccessException;
}

internal readonly record struct GodotProcessLifetimeAttachResult(
    GodotProcessLifetime? Lifetime,
    string? ErrorMessage)
{
    public bool IsSuccess => Lifetime is not null;

    public static GodotProcessLifetimeAttachResult Success(GodotProcessLifetime lifetime)
        => new(lifetime, null);

    public static GodotProcessLifetimeAttachResult Failure(string errorMessage)
        => new(null, errorMessage);
}

using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal readonly record struct RoslynProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    long RoslynGeneration);

internal readonly record struct RoslynProcessExitObservation(
    RoslynProcessIdentity Identity,
    int? ExitCode,
    bool RetirementRequested);

internal sealed class RoslynLanguageServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BoundedTextCapture _stderrCapture;
    private readonly CancellationTokenSource _stderrDrainCancellation = new();
    private readonly Task _stderrDrainTask;
    private readonly Task _exitObservationTask;
    private readonly Action<RoslynProcessExitObservation> _exitObserver;
    private readonly SemaphoreSlim _retirementGate = new(1, 1);
    private int _retirementRequested;
    private int _disposed;

    private RoslynLanguageServerProcess(
        Process process,
        RoslynProcessIdentity identity,
        Action<RoslynProcessExitObservation> exitObserver)
    {
        _process = process;
        Identity = identity;
        _exitObserver = exitObserver ?? throw new ArgumentNullException(nameof(exitObserver));
        _stderrCapture = new BoundedTextCapture(RoslynLanguageServerConstants.MaxCapturedStderrBytes);
        _stderrDrainTask = DrainStderrAsync(_stderrDrainCancellation.Token);
        _exitObservationTask = ObserveExitAsync();
    }

    public RoslynProcessIdentity Identity { get; }

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public bool HasExited => SafeHasExited(_process);

    public static Task<RoslynLanguageServerProcess> StartAsync(
        RoslynLanguageServerRuntime runtime,
        WorkspaceIdentity workspaceIdentity,
        long roslynGeneration,
        Action<RoslynProcessExitObservation> exitObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        ArgumentNullException.ThrowIfNull(exitObserver);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(workspaceIdentity.ProjectRoot))
        {
            throw new DirectoryNotFoundException(
                "Roslyn Language Server working directory no longer exists.");
        }

        ProcessStartInfo startInfo = CreateStartInfo(runtime, workspaceIdentity.ProjectRoot);
        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Process.Start returned false for Roslyn Language Server.");
            }

            long startTimeUtcTicks;
            try
            {
                startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception exception)
            {
                TryKillNoThrow(process);
                throw new InvalidOperationException(
                    "Roslyn Language Server started but its process start time could not be captured.",
                    exception);
            }

            RoslynLanguageServerProcess owner = new(
                process,
                new RoslynProcessIdentity(process.Id, startTimeUtcTicks, roslynGeneration),
                exitObserver);

            return Task.FromResult(owner);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task RetireAsync(CancellationToken cancellationToken)
    {
        await _retirementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _retirementRequested, 1);

            if (!HasExited)
            {
                try
                {
                    await _exitObservationTask
                        .WaitAsync(RoslynLanguageServerConstants.GracefulShutdownTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await ForceKillAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (!HasExited)
            {
                await ForceKillAsync(cancellationToken).ConfigureAwait(false);
            }

            await AwaitOwnedTasksAsync(cancellationToken).ConfigureAwait(false);
            DisposeTerminalResources();
        }
        finally
        {
            _retirementGate.Release();
        }
    }

    public BoundedTextCaptureSnapshot CaptureStderr()
        => _stderrCapture.Capture();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RetireAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _retirementGate.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        RoslynLanguageServerRuntime runtime,
        string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ResolveDotnetHost(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(runtime.ServerDllPath);
        startInfo.ArgumentList.Add("--stdio");
        startInfo.ArgumentList.Add("--logLevel");
        startInfo.ArgumentList.Add("Warning");
        startInfo.ArgumentList.Add("--telemetryLevel");
        startInfo.ArgumentList.Add("off");
        return startInfo;
    }

    private static string ResolveDotnetHost()
    {
        string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost)
            && Path.IsPathFullyQualified(configuredHost)
            && File.Exists(configuredHost))
        {
            return Path.GetFullPath(configuredHost);
        }

        return "dotnet";
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            RoslynProcessExitObservation observation = new(
                Identity,
                TryGetExitCode(),
                Volatile.Read(ref _retirementRequested) != 0);

            try
            {
                _exitObserver(observation);
            }
            catch
            {
                // Process terminal observation must never fault the owned observation task.
            }
        }
    }

    private async Task DrainStderrAsync(CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        try
        {
            while (true)
            {
                int read = await _process.StandardError
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _stderrCapture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Explicit owner retirement can cancel a drain that did not finish after process exit.
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _retirementRequested) != 0)
        {
            // Terminal cleanup may close redirected streams after process retirement.
        }
    }

    private async Task ForceKillAsync(CancellationToken cancellationToken)
    {
        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (PlatformNotSupportedException)
            {
                if (!HasExited)
                {
                    try
                    {
                        _process.Kill();
                    }
                    catch (InvalidOperationException) when (HasExited)
                    {
                    }
                }
            }
            catch (InvalidOperationException) when (HasExited)
            {
            }
        }

        if (!HasExited)
        {
            try
            {
                await _exitObservationTask
                    .WaitAsync(RoslynLanguageServerConstants.ForcedExitTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Roslyn Language Server process {Identity.ProcessId} did not exit after forced termination.",
                    exception);
            }
        }
    }

    private async Task AwaitOwnedTasksAsync(CancellationToken cancellationToken)
    {
        await _exitObservationTask.ConfigureAwait(false);

        try
        {
            await _stderrDrainTask
                .WaitAsync(RoslynLanguageServerConstants.ForcedExitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _stderrDrainCancellation.Cancel();
            await _stderrDrainTask.ConfigureAwait(false);
        }
    }

    private void DisposeTerminalResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stderrDrainCancellation.Cancel();
        _stderrDrainCancellation.Dispose();
        _process.Dispose();
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void TryKillNoThrow(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (PlatformNotSupportedException)
                {
                    process.Kill();
                }
            }
        }
        catch
        {
        }
    }
}

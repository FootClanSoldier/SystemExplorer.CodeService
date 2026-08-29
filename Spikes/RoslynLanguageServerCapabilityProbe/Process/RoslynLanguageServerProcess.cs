using System.Diagnostics;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal sealed class RoslynLanguageServerProcess : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly BoundedTextCapture _stderr;
    private readonly CancellationTokenSource _stderrDrainCancellation = new();
    private readonly Task _stderrDrainTask;
    private readonly Task _exitObservationTask;
    private bool _forcedKill;
    private RoslynProcessMetrics? _lastLiveMetrics;
    private readonly SemaphoreSlim _retirementGate = new(1, 1);
    private int _retired;

    private RoslynLanguageServerProcess(
        System.Diagnostics.Process process,
        RoslynLanguageServerLaunchSpec launchSpec,
        long scenarioGeneration,
        BoundedTextCapture stderr)
    {
        _process = process;
        _stderr = stderr;
        DateTimeOffset? startTimeUtc = TryGetStartTimeUtc(process);
        Identity = new RoslynProcessIdentity(
            process.Id,
            startTimeUtc,
            startTimeUtc?.UtcDateTime.Ticks,
            TryGetActualExecutablePath(process) ?? launchSpec.LauncherExecutablePath,
            launchSpec.ServerCommandPath,
            launchSpec.LaunchKind,
            scenarioGeneration);
        _exitObservationTask = process.WaitForExitAsync();
        _stderrDrainTask = DrainStderrAsync(_stderrDrainCancellation.Token);
    }

    public RoslynProcessIdentity Identity { get; }
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public bool HasExited => SafeHasExited(_process);

    public static async Task<RoslynLanguageServerProcess> StartAsync(
        RoslynLanguageServerLaunchSpec launchSpec,
        string workingDirectory,
        long scenarioGeneration,
        bool autoLoadProjects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Path.IsPathFullyQualified(launchSpec.ServerCommandPath) || !File.Exists(launchSpec.ServerCommandPath))
            throw new ProbeServerSetupException($"Server command is not a valid absolute file path: {launchSpec.ServerCommandPath}");
        if (!Directory.Exists(workingDirectory))
            throw new ProbeServerSetupException($"Working directory does not exist: {workingDirectory}");

        ProcessStartInfo startInfo = launchSpec.CreateProcessStartInfo(workingDirectory, autoLoadProjects);
        System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new ProbeServerSetupException("Process.Start returned false for Roslyn Language Server launch root.");
        }
        catch (Exception exception) when (exception is not ProbeServerSetupException)
        {
            process.Dispose();
            throw new ProbeServerSetupException(
                $"Unable to start Roslyn Language Server launch root ({launchSpec.LaunchKind}): {exception.Message}",
                exception);
        }

        BoundedTextCapture stderr = new(ProbeConstants.MaxCapturedStderrBytes);
        RoslynLanguageServerProcess owner = new(process, launchSpec, scenarioGeneration, stderr);
        _ = owner.CaptureMetrics();
        if (owner.HasExited)
        {
            int? exitCode = owner.TryGetExitCode();
            await owner.DisposeAsync().ConfigureAwait(false);
            throw new ProbeServerSetupException(
                $"Roslyn Language Server launch root exited immediately with code {exitCode} ({launchSpec.LaunchKind}).");
        }

        return owner;
    }

    public RoslynProcessMetrics CaptureMetrics()
    {
        if (HasExited)
            return new RoslynProcessMetrics(DateTimeOffset.UtcNow, null, null);

        long? workingSet = TryRead(() => _process.WorkingSet64);
        long? privateMemory = TryRead(() => _process.PrivateMemorySize64);
        RoslynProcessMetrics metrics = new(DateTimeOffset.UtcNow, workingSet, privateMemory);
        _lastLiveMetrics = metrics;
        return metrics;
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExited)
            return;

        try
        {
            await _exitObservationTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Owned Roslyn launch root {Identity.ProcessId} did not exit within {timeout}.");
        }
    }

    public async Task ForceKillAsync(CancellationToken cancellationToken)
    {
        bool killIssued = false;
        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                killIssued = true;
            }
            catch (PlatformNotSupportedException)
            {
                if (!HasExited)
                {
                    try
                    {
                        _process.Kill();
                        killIssued = true;
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

        if (killIssued)
            _forcedKill = true;

        await WaitForExitAsync(ProbeConstants.ForcedExitTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoslynLanguageServerProcessResult> RetireAsync(
        bool requestForcedKill,
        CancellationToken cancellationToken)
    {
        await _retirementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _retired) != 0)
                return CreateResult();

            if (!HasExited)
                _ = CaptureMetrics();

            if (requestForcedKill && !HasExited)
                await ForceKillAsync(cancellationToken).ConfigureAwait(false);

            if (!HasExited)
            {
                try
                {
                    await WaitForExitAsync(ProbeConstants.GracefulShutdownTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await ForceKillAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                await _stderrDrainTask.WaitAsync(ProbeConstants.ForcedExitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _stderrDrainCancellation.Cancel();
                try { await _stderrDrainTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            RoslynLanguageServerProcessResult result = CreateResult();
            Volatile.Write(ref _retired, 1);
            return result;
        }
        finally
        {
            _retirementGate.Release();
        }
    }

    private async Task DrainStderrAsync(CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await _process.StandardError.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
                break;
            _stderr.Append(buffer.AsSpan(0, read));
        }
    }

    private RoslynLanguageServerProcessResult CreateResult() => new(
        Identity,
        HasExited,
        TryGetExitCode(),
        _forcedKill,
        _stderr.Truncated,
        _stderr.GetText(),
        _lastLiveMetrics);

    private int? TryGetExitCode()
    {
        try { return HasExited ? _process.ExitCode : null; }
        catch { return null; }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static DateTimeOffset? TryGetStartTimeUtc(System.Diagnostics.Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch { return null; }
    }

    private static string? TryGetActualExecutablePath(System.Diagnostics.Process process)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryRead(Func<long> reader)
    {
        try { return reader(); }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!HasExited)
                await ForceKillAsync(CancellationToken.None).ConfigureAwait(false);
            await RetireAsync(requestForcedKill: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (!_stderrDrainTask.IsCompleted)
            {
                try
                {
                    await _stderrDrainTask.WaitAsync(ProbeConstants.ForcedExitTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _stderrDrainCancellation.Cancel();
                    try { await _stderrDrainTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }

            _stderrDrainCancellation.Dispose();
            _retirementGate.Dispose();
            _process.Dispose();
        }
    }
}

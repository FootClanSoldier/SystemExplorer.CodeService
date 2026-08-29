using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal static class ProbeFixtureRestorer
{
    public static async Task<ProbeFixtureRestoreResult> RestoreAsync(
        ProbeFixtureWorkspace fixture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        cancellationToken.ThrowIfCancellationRequested();

        string projectPath = Path.GetFullPath(fixture.ProjectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new ProbeFixtureSetupException("Unable to derive the controlled fixture project directory.");
        string assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        string dotnetHost = ResolveDotnetHost();

        ProcessStartInfo startInfo = new()
        {
            FileName = dotnetHost,
            WorkingDirectory = fixture.RootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");

        using System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
                throw new ProbeFixtureSetupException("Process.Start returned false for controlled fixture restore.");
        }
        catch (Exception exception) when (exception is not ProbeFixtureSetupException)
        {
            throw new ProbeFixtureSetupException(
                $"Unable to start controlled fixture restore: {exception.Message}",
                exception);
        }

        BoundedTextCapture stdout = new(ProbeConstants.MaxFixtureRestoreOutputBytes);
        BoundedTextCapture stderr = new(ProbeConstants.MaxFixtureRestoreOutputBytes);
        using CancellationTokenSource drainCancellation = new();
        Task stdoutDrain = DrainAsync(process.StandardOutput, stdout, drainCancellation.Token);
        Task stderrDrain = DrainAsync(process.StandardError, stderr, drainCancellation.Token);

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(ProbeConstants.FixtureRestoreTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ForceTerminalAsync(process).ConfigureAwait(false);
                await DrainToTerminalAsync(stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                await ForceTerminalAsync(process).ConfigureAwait(false);
                await DrainToTerminalAsync(stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);
                throw new ProbeFixtureSetupException(
                    $"Controlled fixture restore did not terminate within {ProbeConstants.FixtureRestoreTimeout.TotalSeconds:0} seconds.");
            }

            stopwatch.Stop();
            await DrainToTerminalAsync(stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (stdout.Truncated || stderr.Truncated)
            {
                throw new ProbeFixtureSetupException(
                    $"Controlled fixture restore output exceeded the {ProbeConstants.MaxFixtureRestoreOutputBytes} byte per-stream capture bound.");
            }

            int exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                string stderrText = stderr.GetText();
                string outputEvidence = string.IsNullOrWhiteSpace(stderrText)
                    ? $" stdout: {Concise(stdout.GetText())}"
                    : $" stderr: {Concise(stderrText)}";
                throw new ProbeFixtureSetupException(
                    $"Controlled fixture restore exited with code {exitCode}.{outputEvidence}");
            }

            if (!File.Exists(assetsPath))
            {
                throw new ProbeFixtureSetupException(
                    "Controlled fixture restore exited with code 0 but obj/project.assets.json was not created.");
            }

            long assetsLength = new FileInfo(assetsPath).Length;
            if (assetsLength <= 0)
            {
                throw new ProbeFixtureSetupException(
                    "Controlled fixture restore exited with code 0 but obj/project.assets.json was empty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProbeFixtureRestoreResult(
                assetsPath,
                assetsLength,
                stopwatch.Elapsed.TotalMilliseconds,
                exitCode);
        }
        finally
        {
            if (!SafeHasExited(process))
                await ForceTerminalAsync(process).ConfigureAwait(false);

            if (!stdoutDrain.IsCompleted || !stderrDrain.IsCompleted)
            {
                drainCancellation.Cancel();
                try
                {
                    await Task.WhenAll(stdoutDrain, stderrDrain)
                        .WaitAsync(ProbeConstants.ForcedExitTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException exception)
                {
                    throw new ProbeFixtureSetupException(
                        "Controlled fixture restore output drains did not retire after process terminality.",
                        exception);
                }
            }
        }
    }

    private static string ResolveDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured)
            && Path.IsPathFullyQualified(configured)
            && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        return "dotnet";
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedTextCapture capture,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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

            capture.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task DrainToTerminalAsync(
        Task stdoutDrain,
        Task stderrDrain,
        CancellationTokenSource drainCancellation)
    {
        try
        {
            await Task.WhenAll(stdoutDrain, stderrDrain)
                .WaitAsync(ProbeConstants.ForcedExitTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            drainCancellation.Cancel();
            try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw new ProbeFixtureSetupException(
                "Controlled fixture restore output streams did not retire after process exit.",
                exception);
        }
    }

    private static async Task ForceTerminalAsync(System.Diagnostics.Process process)
    {
        if (!SafeHasExited(process))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (PlatformNotSupportedException)
            {
                if (!SafeHasExited(process))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException) when (SafeHasExited(process))
                    {
                    }
                }
            }
            catch (InvalidOperationException) when (SafeHasExited(process))
            {
            }
            catch (Exception exception)
            {
                throw new ProbeFixtureSetupException(
                    $"Unable to terminate controlled fixture restore process tree: {exception.Message}",
                    exception);
            }
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProbeConstants.ForcedExitTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ProbeFixtureSetupException(
                $"Controlled fixture restore process did not become terminal within {ProbeConstants.ForcedExitTimeout.TotalSeconds:0} seconds after forced retirement.",
                exception);
        }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static string Concise(string text)
    {
        const int maxCharacters = 512;
        string compact = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length == 0)
            return "<empty>";
        return compact.Length <= maxCharacters ? compact : compact[..maxCharacters] + "...";
    }
}

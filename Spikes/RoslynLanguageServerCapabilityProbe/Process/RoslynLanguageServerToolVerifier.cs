using System.Diagnostics;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal static class RoslynLanguageServerToolVerifier
{
    private const string PackageId = "roslyn-language-server";
    private const string CommandName = "roslyn-language-server";

    public static async Task<RoslynLanguageServerToolVerificationResult> VerifyAsync(
        string serverCommandPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedCommandPath = ValidateServerCommandPath(serverCommandPath);
        string toolPath = Path.GetDirectoryName(normalizedCommandPath)
            ?? throw new ProbeServerSetupException("Unable to derive the private Roslyn tool path from --server.");
        string dotnetHost = ResolveDotnetHost();

        ProcessStartInfo startInfo = new()
        {
            FileName = dotnetHost,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tool");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--tool-path");
        startInfo.ArgumentList.Add(toolPath);
        startInfo.ArgumentList.Add(PackageId);

        using System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new ProbeServerSetupException("Process.Start returned false for private Roslyn tool inventory verification.");
        }
        catch (Exception exception) when (exception is not ProbeServerSetupException)
        {
            throw new ProbeServerSetupException(
                $"Unable to start dotnet tool inventory verification: {exception.Message}",
                exception);
        }

        BoundedTextCapture stdout = new(ProbeConstants.MaxToolInventoryOutputBytes);
        BoundedTextCapture stderr = new(ProbeConstants.MaxToolInventoryOutputBytes);
        using CancellationTokenSource drainCancellation = new();
        Task stdoutDrain = DrainAsync(process.StandardOutput, stdout, drainCancellation.Token);
        Task stderrDrain = DrainAsync(process.StandardError, stderr, drainCancellation.Token);

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(ProbeConstants.ToolInventoryTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await ForceTerminalAsync(process).ConfigureAwait(false);
                throw new ProbeServerSetupException(
                    $"dotnet tool inventory did not terminate within {ProbeConstants.ToolInventoryTimeout.TotalSeconds:0} seconds.");
            }

            await DrainToTerminalAsync(stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new ProbeServerSetupException(
                    $"dotnet tool inventory exited with code {process.ExitCode}. stderr: {Concise(stderr.GetText())}");
            }

            if (stdout.Truncated || stderr.Truncated)
            {
                throw new ProbeServerSetupException(
                    $"dotnet tool inventory output exceeded the {ProbeConstants.MaxToolInventoryOutputBytes} byte capture bound.");
            }

            ToolInventoryRow row = ParseInventory(stdout.GetText());
            string expectedVersion = ProbeConstants.RoslynLanguageServerVersion;
            if (!string.Equals(row.Version, expectedVersion, StringComparison.Ordinal))
            {
                throw new ProbeServerSetupException(
                    $"Roslyn Language Server version mismatch. Expected: {expectedVersion}; Actual: {row.Version}.");
            }

            if (!row.Commands.Contains(CommandName, StringComparer.Ordinal))
            {
                throw new ProbeServerSetupException(
                    $"Private tool inventory row for {PackageId} does not publish the expected command {CommandName}.");
            }

            return new RoslynLanguageServerToolVerificationResult(
                expectedVersion,
                row.Version,
                toolPath,
                normalizedCommandPath,
                CommandName);
        }
        finally
        {
            if (!SafeHasExited(process))
                await ForceTerminalAsync(process).ConfigureAwait(false);

            if (!stdoutDrain.IsCompleted || !stderrDrain.IsCompleted)
            {
                drainCancellation.Cancel();
                try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static string ValidateServerCommandPath(string serverCommandPath)
    {
        if (string.IsNullOrWhiteSpace(serverCommandPath) || !Path.IsPathFullyQualified(serverCommandPath))
            throw new ProbeServerSetupException($"Server command is not a fully-qualified absolute path: {serverCommandPath}");

        string normalized = Path.GetFullPath(serverCommandPath);
        if (!File.Exists(normalized))
            throw new ProbeServerSetupException($"Server command does not exist: {normalized}");

        string? parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new ProbeServerSetupException($"Server command parent directory does not exist: {parent ?? "<null>"}");

        string fileName = Path.GetFileName(normalized);
        bool expectedName = OperatingSystem.IsWindows()
            ? string.Equals(fileName, CommandName + ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, CommandName + ".exe", StringComparison.OrdinalIgnoreCase)
            : string.Equals(fileName, CommandName, StringComparison.Ordinal);

        if (!expectedName)
        {
            string expected = OperatingSystem.IsWindows()
                ? $"{CommandName}.cmd or {CommandName}.exe"
                : CommandName;
            throw new ProbeServerSetupException(
                $"--server must name the private Roslyn tool command ({expected}); actual filename: {fileName}");
        }

        return normalized;
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

    private static ToolInventoryRow ParseInventory(string stdout)
    {
        List<ToolInventoryRow> matchingRows = [];
        List<string> unexpectedPackageIds = [];

        foreach (string rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.All(static character => character == '-'))
                continue;

            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                continue;

            if (string.Equals(tokens[0], PackageId, StringComparison.Ordinal))
            {
                if (tokens.Length < 3 || string.IsNullOrWhiteSpace(tokens[1]))
                {
                    throw new ProbeServerSetupException(
                        $"Malformed private tool inventory row for {PackageId}: {Concise(line)}");
                }

                string[] commands = tokens.Skip(2).ToArray();
                if (commands.Length == 0 || commands.Any(string.IsNullOrWhiteSpace))
                {
                    throw new ProbeServerSetupException(
                        $"Private tool inventory row for {PackageId} is missing its command column.");
                }

                matchingRows.Add(new ToolInventoryRow(tokens[0], tokens[1], commands));
                continue;
            }

            if (LooksLikeToolRow(tokens))
                unexpectedPackageIds.Add(tokens[0]);
        }

        if (matchingRows.Count == 0)
        {
            if (unexpectedPackageIds.Count > 0)
            {
                throw new ProbeServerSetupException(
                    $"Private tool inventory returned unexpected package identity: {string.Join(", ", unexpectedPackageIds.Distinct(StringComparer.Ordinal))}");
            }

            throw new ProbeServerSetupException(
                $"Private tool inventory did not contain a matching row for package {PackageId}; exit code 0 alone is not version evidence.");
        }

        if (matchingRows.Count != 1)
        {
            throw new ProbeServerSetupException(
                $"Private tool inventory contained {matchingRows.Count} rows for package {PackageId}; exactly one is required.");
        }

        ToolInventoryRow row = matchingRows[0];
        if (!string.Equals(row.PackageId, PackageId, StringComparison.Ordinal))
            throw new ProbeServerSetupException($"Unexpected package identity in private tool inventory: {row.PackageId}");
        if (string.IsNullOrWhiteSpace(row.Version))
            throw new ProbeServerSetupException($"Private tool inventory row for {PackageId} is missing a version.");
        if (!row.Commands.Contains(CommandName, StringComparer.Ordinal))
            throw new ProbeServerSetupException($"Private tool inventory row for {PackageId} is missing command {CommandName}.");

        return row;
    }

    private static bool LooksLikeToolRow(IReadOnlyList<string> tokens) =>
        tokens.Count >= 3
        && tokens[1].Any(char.IsDigit)
        && tokens[0].Any(static character => char.IsLetterOrDigit(character));

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
        catch (TimeoutException)
        {
            drainCancellation.Cancel();
            try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw new ProbeServerSetupException("dotnet tool inventory streams did not retire after process exit.");
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
                    process.Kill();
            }
            catch (InvalidOperationException) when (SafeHasExited(process))
            {
            }
        }

        if (SafeHasExited(process))
            return;

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProbeConstants.ForcedExitTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ProbeServerSetupException("Unable to retire dotnet tool inventory process after forced kill.", exception);
        }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static string Concise(string text)
    {
        string normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
    }

    private sealed record ToolInventoryRow(string PackageId, string Version, IReadOnlyList<string> Commands);
}

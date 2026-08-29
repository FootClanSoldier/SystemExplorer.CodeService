using System.Diagnostics;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal enum RoslynLanguageServerLaunchKind
{
    DirectCommand,
    WindowsCommandShim,
}

internal sealed record RoslynLanguageServerLaunchSpec(
    string ServerCommandPath,
    string LauncherExecutablePath,
    RoslynLanguageServerLaunchKind LaunchKind)
{
    public static RoslynLanguageServerLaunchSpec Create(RoslynLanguageServerToolVerificationResult verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        if (OperatingSystem.IsWindows()
            && string.Equals(Path.GetExtension(verification.ServerCommandPath), ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            string commandInterpreter = ResolveWindowsCommandInterpreter();
            return new RoslynLanguageServerLaunchSpec(
                verification.ServerCommandPath,
                commandInterpreter,
                RoslynLanguageServerLaunchKind.WindowsCommandShim);
        }

        return new RoslynLanguageServerLaunchSpec(
            verification.ServerCommandPath,
            verification.ServerCommandPath,
            RoslynLanguageServerLaunchKind.DirectCommand);
    }

    public ProcessStartInfo CreateProcessStartInfo(string workingDirectory, bool autoLoadProjects)
    {
        if (!Directory.Exists(workingDirectory))
            throw new ProbeServerSetupException($"Working directory does not exist: {workingDirectory}");

        string[] serverArguments = CreateServerArguments(autoLoadProjects);
        ProcessStartInfo startInfo = new()
        {
            FileName = LauncherExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        switch (LaunchKind)
        {
            case RoslynLanguageServerLaunchKind.DirectCommand:
                foreach (string argument in serverArguments)
                    startInfo.ArgumentList.Add(argument);
                break;

            case RoslynLanguageServerLaunchKind.WindowsCommandShim:
                startInfo.Arguments = WindowsCommandShimInvocation.BuildProcessArguments(ServerCommandPath, serverArguments);
                break;

            default:
                throw new ProbeServerSetupException($"Unsupported Roslyn launch kind: {LaunchKind}");
        }

        return startInfo;
    }

    private static string[] CreateServerArguments(bool autoLoadProjects)
    {
        List<string> arguments =
        [
            "--stdio",
            "--logLevel",
            "Warning",
            "--telemetryLevel",
            "off",
        ];
        if (autoLoadProjects)
            arguments.Add("--autoLoadProjects");
        return arguments.ToArray();
    }

    private static string ResolveWindowsCommandInterpreter()
    {
        string? comSpec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrWhiteSpace(comSpec)
            && Path.IsPathFullyQualified(comSpec)
            && File.Exists(comSpec))
        {
            return Path.GetFullPath(comSpec);
        }

        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory) && Path.IsPathFullyQualified(systemDirectory))
        {
            string systemCmd = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(systemCmd))
                return Path.GetFullPath(systemCmd);
        }

        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot) && Path.IsPathFullyQualified(systemRoot))
        {
            string systemCmd = Path.Combine(systemRoot, "System32", "cmd.exe");
            if (File.Exists(systemCmd))
                return Path.GetFullPath(systemCmd);
        }

        throw new ProbeServerSetupException(
            "Windows roslyn-language-server.cmd launch requires an existing absolute COMSPEC/cmd.exe path.");
    }
}

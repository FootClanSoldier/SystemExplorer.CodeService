using System.IO;
using System.Reflection;

namespace SystemExplorer.CodeService;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int UsageErrorExitCode = 2;

    private static int Main(string[] args)
    {
        if (
            args.Length == 1
            && string.Equals(args[0], "--version", StringComparison.Ordinal)
        )
        {
            Console.WriteLine(GetInformationalVersion());
            return SuccessExitCode;
        }

        if (
            args.Length == 0
            || (
                args.Length == 1
                && (
                    string.Equals(args[0], "--help", StringComparison.Ordinal)
                    || string.Equals(args[0], "-h", StringComparison.Ordinal)
                )
            )
        )
        {
            WriteHelp(Console.Out);
            return SuccessExitCode;
        }

        Console.Error.WriteLine("Unsupported arguments.");
        WriteHelp(Console.Error);
        return UsageErrorExitCode;
    }

    private static string GetInformationalVersion()
    {
        var versionAttribute =
            typeof(Program)
                .Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        return string.IsNullOrWhiteSpace(versionAttribute?.InformationalVersion)
            ? "unknown"
            : versionAttribute.InformationalVersion;
    }

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("SystemExplorer.CodeService");
        writer.WriteLine("Usage:");
        writer.WriteLine("  system-explorer-code --version");
        writer.WriteLine("  system-explorer-code --help");
    }
}

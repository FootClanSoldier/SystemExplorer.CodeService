using System.Globalization;

namespace SystemExplorer.CodeService;

internal enum CodeServiceStartupMode
{
    Server,
}

internal sealed class CodeServiceStartupOptions
{
    private const string ServerModeArgument = "server";
    private const string GodotProcessIdOption = "--godot-pid";
    private const string GodotStartTimeUtcTicksOption = "--godot-start-time-utc-ticks";
    private const string DiagnosticLogOption = "--diagnostic-log";

    private CodeServiceStartupOptions(
        CodeServiceStartupMode mode,
        GodotProcessIdentity godotOwnerIdentity,
        bool diagnosticLoggingEnabled)
    {
        Mode = mode;
        GodotOwnerIdentity = godotOwnerIdentity;
        DiagnosticLoggingEnabled = diagnosticLoggingEnabled;
    }

    public CodeServiceStartupMode Mode { get; }

    public GodotProcessIdentity GodotOwnerIdentity { get; }

    public bool DiagnosticLoggingEnabled { get; }

    public static CodeServiceStartupOptionsParseResult TryParse(string[] args)
    {
        if (args.Length == 0)
        {
            return CodeServiceStartupOptionsParseResult.Failure(
                "startup mode is required; expected 'server'.");
        }

        if (!string.Equals(args[0], ServerModeArgument, StringComparison.Ordinal))
        {
            return CodeServiceStartupOptionsParseResult.Failure(
                $"unknown startup mode '{args[0]}'; expected 'server'.");
        }

        int? godotProcessId = null;
        long? godotStartTimeUtcTicks = null;
        bool diagnosticLoggingEnabled = false;

        int index = 1;
        while (index < args.Length)
        {
            string option = args[index];

            switch (option)
            {
                case GodotProcessIdOption:
                    if (godotProcessId.HasValue)
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"duplicate option '{GodotProcessIdOption}'.");
                    }

                    if (!TryReadOptionValue(args, ref index, option, out string processIdValue, out string? processIdError))
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(processIdError!);
                    }

                    if (!int.TryParse(
                            processIdValue,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsedProcessId))
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"option '{GodotProcessIdOption}' requires a valid invariant integer value.");
                    }

                    if (parsedProcessId <= 0)
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"option '{GodotProcessIdOption}' must be greater than zero.");
                    }

                    godotProcessId = parsedProcessId;
                    break;

                case GodotStartTimeUtcTicksOption:
                    if (godotStartTimeUtcTicks.HasValue)
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"duplicate option '{GodotStartTimeUtcTicksOption}'.");
                    }

                    if (!TryReadOptionValue(args, ref index, option, out string startTimeValue, out string? startTimeError))
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(startTimeError!);
                    }

                    if (!long.TryParse(
                            startTimeValue,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out long parsedStartTimeUtcTicks))
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"option '{GodotStartTimeUtcTicksOption}' requires a valid invariant long value.");
                    }

                    if (parsedStartTimeUtcTicks <= 0)
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"option '{GodotStartTimeUtcTicksOption}' must be greater than zero.");
                    }

                    godotStartTimeUtcTicks = parsedStartTimeUtcTicks;
                    break;

                case DiagnosticLogOption:
                    if (diagnosticLoggingEnabled)
                    {
                        return CodeServiceStartupOptionsParseResult.Failure(
                            $"duplicate option '{DiagnosticLogOption}'.");
                    }

                    diagnosticLoggingEnabled = true;
                    break;

                default:
                    return CodeServiceStartupOptionsParseResult.Failure(
                        $"unknown option '{option}'.");
            }

            index++;
        }

        if (!godotProcessId.HasValue)
        {
            return CodeServiceStartupOptionsParseResult.Failure(
                $"required option '{GodotProcessIdOption}' is missing.");
        }

        if (!godotStartTimeUtcTicks.HasValue)
        {
            return CodeServiceStartupOptionsParseResult.Failure(
                $"required option '{GodotStartTimeUtcTicksOption}' is missing.");
        }

        GodotProcessIdentity ownerIdentity = new(
            godotProcessId.Value,
            godotStartTimeUtcTicks.Value);

        return CodeServiceStartupOptionsParseResult.Success(
            new CodeServiceStartupOptions(
                CodeServiceStartupMode.Server,
                ownerIdentity,
                diagnosticLoggingEnabled));
    }

    private static bool TryReadOptionValue(
        string[] args,
        ref int optionIndex,
        string option,
        out string value,
        out string? errorMessage)
    {
        int valueIndex = optionIndex + 1;
        if (valueIndex >= args.Length || args[valueIndex].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            errorMessage = $"option '{option}' requires a value.";
            return false;
        }

        optionIndex = valueIndex;
        value = args[valueIndex];
        errorMessage = null;
        return true;
    }
}

internal readonly record struct CodeServiceStartupOptionsParseResult(
    CodeServiceStartupOptions? Options,
    string? ErrorMessage)
{
    public bool IsSuccess => Options is not null;

    public static CodeServiceStartupOptionsParseResult Success(CodeServiceStartupOptions options)
        => new(options, null);

    public static CodeServiceStartupOptionsParseResult Failure(string errorMessage)
        => new(null, errorMessage);
}

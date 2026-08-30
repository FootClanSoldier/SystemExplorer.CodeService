namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe;

internal sealed record ProbeOptions(
    string ServerCommandPath,
    string? StateTraceServerPath,
    string? StateTraceProvenancePath,
    string? SolutionPath,
    string? ProjectPath,
    string? DocumentPath,
    int? CompletionLine,
    int? CompletionCharacter,
    int? DefinitionLine,
    int? DefinitionCharacter,
    string? ExpectedDefinitionPath,
    string? ReportPath,
    bool KeepArtifacts,
    bool RunAutoLoadComparison,
    bool RunStaleVersionExperiment)
{
    public bool CompletionSmokeSelected => CompletionLine is not null && CompletionCharacter is not null;
    public bool DefinitionSmokeSelected => DefinitionLine is not null && DefinitionCharacter is not null && ExpectedDefinitionPath is not null;
    public bool FullRealSemanticValidationSelected => CompletionSmokeSelected && DefinitionSmokeSelected;
    public bool StateTraceSelected => StateTraceServerPath is not null && StateTraceProvenancePath is not null;

    public static ProbeOptionsParseResult TryParse(string[] args)
    {
        string? server = null;
        string? stateTraceServer = null;
        string? stateTraceProvenance = null;
        string? solution = null;
        string? project = null;
        string? document = null;
        string? expectedDefinition = null;
        string? report = null;
        int? completionLine = null;
        int? completionCharacter = null;
        int? definitionLine = null;
        int? definitionCharacter = null;
        bool keepArtifacts = false;
        bool autoLoad = true;
        bool stale = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--server":
                    if (!TryTakeValue(args, ref i, out server))
                        return ProbeOptionsParseResult.Failure("--server requires a value.");
                    break;
                case "--state-trace-server":
                    if (!TryTakeValue(args, ref i, out stateTraceServer))
                        return ProbeOptionsParseResult.Failure("--state-trace-server requires a value.");
                    break;
                case "--state-trace-provenance":
                    if (!TryTakeValue(args, ref i, out stateTraceProvenance))
                        return ProbeOptionsParseResult.Failure("--state-trace-provenance requires a value.");
                    break;
                case "--solution":
                    if (!TryTakeValue(args, ref i, out solution))
                        return ProbeOptionsParseResult.Failure("--solution requires a value.");
                    break;
                case "--project":
                    if (!TryTakeValue(args, ref i, out project))
                        return ProbeOptionsParseResult.Failure("--project requires a value.");
                    break;
                case "--document":
                    if (!TryTakeValue(args, ref i, out document))
                        return ProbeOptionsParseResult.Failure("--document requires a value.");
                    break;
                case "--completion-line":
                    if (!TryTakeInt(args, ref i, out completionLine) || completionLine < 0)
                        return ProbeOptionsParseResult.Failure("--completion-line requires a non-negative integer.");
                    break;
                case "--completion-character":
                    if (!TryTakeInt(args, ref i, out completionCharacter) || completionCharacter < 0)
                        return ProbeOptionsParseResult.Failure("--completion-character requires a non-negative integer.");
                    break;
                case "--definition-line":
                    if (!TryTakeInt(args, ref i, out definitionLine) || definitionLine < 0)
                        return ProbeOptionsParseResult.Failure("--definition-line requires a non-negative integer.");
                    break;
                case "--definition-character":
                    if (!TryTakeInt(args, ref i, out definitionCharacter) || definitionCharacter < 0)
                        return ProbeOptionsParseResult.Failure("--definition-character requires a non-negative integer.");
                    break;
                case "--expected-definition":
                    if (!TryTakeValue(args, ref i, out expectedDefinition))
                        return ProbeOptionsParseResult.Failure("--expected-definition requires a value.");
                    break;
                case "--report":
                    if (!TryTakeValue(args, ref i, out report))
                        return ProbeOptionsParseResult.Failure("--report requires a value.");
                    break;
                case "--keep-artifacts":
                    keepArtifacts = true;
                    break;
                case "--no-auto-load-comparison":
                    autoLoad = false;
                    break;
                case "--stale-version-experiment":
                    stale = true;
                    break;
                case "--help":
                case "-h":
                    return ProbeOptionsParseResult.Help();
                default:
                    return ProbeOptionsParseResult.Failure($"Unknown argument: {arg}");
            }
        }

        if (server is null)
            return ProbeOptionsParseResult.Failure("--server <absolute-path> is required.");

        bool anyStateTraceOption = stateTraceServer is not null || stateTraceProvenance is not null;
        if (anyStateTraceOption && (stateTraceServer is null || stateTraceProvenance is null))
        {
            return ProbeOptionsParseResult.Failure(
                "--state-trace-server and --state-trace-provenance must be supplied together.");
        }

        if (stateTraceServer is not null && !Path.IsPathFullyQualified(stateTraceServer))
            return ProbeOptionsParseResult.Failure("--state-trace-server must be an absolute path.");
        if (stateTraceProvenance is not null && !Path.IsPathFullyQualified(stateTraceProvenance))
            return ProbeOptionsParseResult.Failure("--state-trace-provenance must be an absolute path.");

        if (solution is not null && project is not null)
            return ProbeOptionsParseResult.Failure("Specify either --solution or --project, not both.");

        if (solution is not null && !Path.IsPathFullyQualified(solution))
            return ProbeOptionsParseResult.Failure("--solution must be an absolute path.");
        if (project is not null && !Path.IsPathFullyQualified(project))
            return ProbeOptionsParseResult.Failure("--project must be an absolute path.");
        if (document is not null && !Path.IsPathFullyQualified(document))
            return ProbeOptionsParseResult.Failure("--document must be an absolute path.");
        if (expectedDefinition is not null && !Path.IsPathFullyQualified(expectedDefinition))
            return ProbeOptionsParseResult.Failure("--expected-definition must be an absolute path.");

        bool anyCompletionPosition = completionLine is not null || completionCharacter is not null;
        if (anyCompletionPosition && (completionLine is null || completionCharacter is null))
            return ProbeOptionsParseResult.Failure("--completion-line and --completion-character must be supplied together.");

        bool anyDefinitionSelection = definitionLine is not null || definitionCharacter is not null || expectedDefinition is not null;
        if (anyDefinitionSelection && (definitionLine is null || definitionCharacter is null || expectedDefinition is null))
        {
            return ProbeOptionsParseResult.Failure(
                "--definition-line, --definition-character, and --expected-definition must be supplied together.");
        }

        if ((anyCompletionPosition || anyDefinitionSelection) && document is null)
            return ProbeOptionsParseResult.Failure("Real semantic positions require --document.");

        if (document is not null && !anyCompletionPosition && !anyDefinitionSelection)
        {
            return ProbeOptionsParseResult.Failure(
                "--document requires a complete completion or definition selection.");
        }

        if (document is not null && solution is null && project is null)
            return ProbeOptionsParseResult.Failure("--document requires --solution or --project.");

        if (stateTraceServer is not null) stateTraceServer = Path.GetFullPath(stateTraceServer);
        if (stateTraceProvenance is not null) stateTraceProvenance = Path.GetFullPath(stateTraceProvenance);
        if (solution is not null) solution = Path.GetFullPath(solution);
        if (project is not null) project = Path.GetFullPath(project);
        if (document is not null) document = Path.GetFullPath(document);
        if (expectedDefinition is not null) expectedDefinition = Path.GetFullPath(expectedDefinition);
        if (report is not null) report = Path.GetFullPath(report);

        if (stateTraceServer is not null && !File.Exists(stateTraceServer))
            return ProbeOptionsParseResult.Failure($"State trace server does not exist: {stateTraceServer}");
        if (stateTraceProvenance is not null && !File.Exists(stateTraceProvenance))
            return ProbeOptionsParseResult.Failure($"State trace provenance does not exist: {stateTraceProvenance}");
        if (solution is not null && !File.Exists(solution))
            return ProbeOptionsParseResult.Failure($"Solution does not exist: {solution}");
        if (solution is not null && !IsExtension(solution, ".sln", ".slnx"))
            return ProbeOptionsParseResult.Failure("--solution must point to a .sln or .slnx file.");
        if (project is not null && !File.Exists(project))
            return ProbeOptionsParseResult.Failure($"Project does not exist: {project}");
        if (project is not null && !IsExtension(project, ".csproj"))
            return ProbeOptionsParseResult.Failure("--project must point to a .csproj file.");
        if (document is not null && !File.Exists(document))
            return ProbeOptionsParseResult.Failure($"Document does not exist: {document}");
        if (document is not null && !IsExtension(document, ".cs"))
            return ProbeOptionsParseResult.Failure("--document must point to an existing .cs file.");
        if (expectedDefinition is not null && !File.Exists(expectedDefinition))
            return ProbeOptionsParseResult.Failure($"Expected definition does not exist: {expectedDefinition}");
        if (expectedDefinition is not null && !IsExtension(expectedDefinition, ".cs"))
            return ProbeOptionsParseResult.Failure("--expected-definition must point to an existing .cs file.");

        return ProbeOptionsParseResult.Success(new ProbeOptions(
            server,
            stateTraceServer,
            stateTraceProvenance,
            solution,
            project,
            document,
            completionLine,
            completionCharacter,
            definitionLine,
            definitionCharacter,
            expectedDefinition,
            report,
            keepArtifacts,
            autoLoad,
            stale));
    }

    private static bool IsExtension(string path, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool TryTakeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryTakeInt(string[] args, ref int index, out int? value)
    {
        if (!TryTakeValue(args, ref index, out string? text) || !int.TryParse(text, out int parsed))
        {
            value = null;
            return false;
        }

        value = parsed;
        return true;
    }
}

internal sealed record ProbeOptionsParseResult(bool IsSuccess, bool ShowHelp, ProbeOptions? Options, string? ErrorMessage)
{
    public static ProbeOptionsParseResult Success(ProbeOptions options) => new(true, false, options, null);
    public static ProbeOptionsParseResult Failure(string message) => new(false, false, null, message);
    public static ProbeOptionsParseResult Help() => new(false, true, null, null);
}

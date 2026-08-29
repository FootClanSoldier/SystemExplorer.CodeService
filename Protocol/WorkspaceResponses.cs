using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record WorkspaceInitializeResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("projectRoot")] string? ProjectRoot,
    [property: JsonPropertyName("reusedExistingWorkspace")] bool ReusedExistingWorkspace,
    [property: JsonPropertyName("sourceFileCount")] int SourceFileCount,
    [property: JsonPropertyName("projectFileCount")] int ProjectFileCount,
    [property: JsonPropertyName("solutionFileCount")] int SolutionFileCount,
    [property: JsonPropertyName("faultKind")] string? FaultKind);

internal sealed record WorkspaceStatusResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("projectRoot")] string? ProjectRoot,
    [property: JsonPropertyName("sourceFileCount")] int SourceFileCount,
    [property: JsonPropertyName("projectFileCount")] int ProjectFileCount,
    [property: JsonPropertyName("solutionFileCount")] int SolutionFileCount,
    [property: JsonPropertyName("faultKind")] string? FaultKind);

internal sealed record WorkspaceFailureResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("projectRoot")] string? ProjectRoot,
    [property: JsonPropertyName("sourceFileCount")] int SourceFileCount,
    [property: JsonPropertyName("projectFileCount")] int ProjectFileCount,
    [property: JsonPropertyName("solutionFileCount")] int SolutionFileCount,
    [property: JsonPropertyName("faultKind")] string? FaultKind);

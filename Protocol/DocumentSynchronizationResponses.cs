using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record DocumentEpochResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("clientGeneration")] long? ClientGeneration,
    [property: JsonPropertyName("epochId")] string? EpochId,
    [property: JsonPropertyName("workspaceGeneration")] long? WorkspaceGeneration,
    [property: JsonPropertyName("workspacePublicationVersion")] long? WorkspacePublicationVersion,
    [property: JsonPropertyName("roslynGeneration")] long? RoslynGeneration,
    [property: JsonPropertyName("declaredOpenDocumentCount")] int DeclaredOpenDocumentCount,
    [property: JsonPropertyName("retainedDocumentCount")] int RetainedDocumentCount,
    [property: JsonPropertyName("closedDocumentCount")] int ClosedDocumentCount);

internal sealed record DocumentSnapshotResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("clientGeneration")] long? ClientGeneration,
    [property: JsonPropertyName("epochId")] string? EpochId,
    [property: JsonPropertyName("documentPath")] string? DocumentPath,
    [property: JsonPropertyName("acceptedClientVersion")] long? AcceptedClientVersion,
    [property: JsonPropertyName("workspaceGeneration")] long? WorkspaceGeneration,
    [property: JsonPropertyName("workspacePublicationVersion")] long? WorkspacePublicationVersion,
    [property: JsonPropertyName("roslynGeneration")] long? RoslynGeneration,
    [property: JsonPropertyName("roslynDocumentVersion")] int? RoslynDocumentVersion);

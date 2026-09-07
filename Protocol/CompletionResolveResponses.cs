using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record DocumentCompletionResolveResponse(
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
    [property: JsonPropertyName("roslynDocumentVersion")] int? RoslynDocumentVersion,
    [property: JsonPropertyName("roslynOverlayRevision")] long? RoslynOverlayRevision,
    [property: JsonPropertyName("edits")] IReadOnlyList<DocumentCompletionResolveResponseEdit> Edits);

internal sealed record DocumentCompletionResolveResponseEdit(
    [property: JsonPropertyName("range")] DocumentCompletionResolveResponseRange Range,
    [property: JsonPropertyName("newText")] string NewText);

internal sealed record DocumentCompletionResolveResponseRange(
    [property: JsonPropertyName("start")] DocumentCompletionResolveResponsePosition Start,
    [property: JsonPropertyName("end")] DocumentCompletionResolveResponsePosition End);

internal sealed record DocumentCompletionResolveResponsePosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

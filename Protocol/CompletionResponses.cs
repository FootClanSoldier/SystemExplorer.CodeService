using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record DocumentCompletionResponse(
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
    [property: JsonPropertyName("isIncomplete")] bool IsIncomplete,
    [property: JsonPropertyName("items")] IReadOnlyList<DocumentCompletionResponseItem> Items);

internal sealed record DocumentCompletionResponseItem(
    [property: JsonPropertyName("kind")] int? Kind,
    [property: JsonPropertyName("displayText")] string DisplayText,
    [property: JsonPropertyName("insertText")] string InsertText,
    [property: JsonPropertyName("filterText")] string FilterText,
    [property: JsonPropertyName("sortText")] string SortText,
    [property: JsonPropertyName("preselect")] bool Preselect);

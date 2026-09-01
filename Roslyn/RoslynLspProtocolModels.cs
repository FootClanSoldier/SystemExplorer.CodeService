using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record RoslynWorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

internal sealed record RoslynClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

internal sealed class RoslynInitializeParams
{
    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("clientInfo")]
    public required RoslynClientInfo ClientInfo { get; init; }

    [JsonPropertyName("rootUri")]
    public required string RootUri { get; init; }

    [JsonPropertyName("workspaceFolders")]
    public required IReadOnlyList<RoslynWorkspaceFolder> WorkspaceFolders { get; init; }

    [JsonPropertyName("capabilities")]
    public required object Capabilities { get; init; }

    [JsonPropertyName("trace")]
    public string Trace { get; init; } = "off";
}

internal sealed record RoslynSolutionOpenParams(
    [property: JsonPropertyName("solution")] string Solution);

internal sealed record RoslynProjectOpenParams(
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

internal sealed record RoslynConfigurationItem(
    [property: JsonPropertyName("scopeUri")] string? ScopeUri,
    [property: JsonPropertyName("section")] string? Section);

internal sealed record RoslynConfigurationParams(
    [property: JsonPropertyName("items")] IReadOnlyList<RoslynConfigurationItem> Items);

internal sealed record RoslynTextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

internal sealed record RoslynTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

internal sealed record RoslynVersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

internal sealed record RoslynTextDocumentContentChangeEvent(
    [property: JsonPropertyName("text")] string Text);

internal sealed record RoslynDidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] RoslynTextDocumentItem TextDocument);

internal sealed record RoslynDidChangeTextDocumentParams(
    [property: JsonPropertyName("textDocument")] RoslynVersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] IReadOnlyList<RoslynTextDocumentContentChangeEvent> ContentChanges);

internal sealed record RoslynDidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] RoslynTextDocumentIdentifier TextDocument);

internal sealed record RoslynDocumentDiagnosticParams(
    [property: JsonPropertyName("textDocument")] RoslynTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("identifier")] string? Identifier = null,
    [property: JsonPropertyName("previousResultId")] string? PreviousResultId = null);

internal sealed record RoslynPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

internal sealed record RoslynTextDocumentPositionParams(
    [property: JsonPropertyName("textDocument")] RoslynTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] RoslynPosition Position);

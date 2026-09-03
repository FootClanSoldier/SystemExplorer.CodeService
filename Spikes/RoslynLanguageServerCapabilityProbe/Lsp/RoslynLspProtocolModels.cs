using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal sealed record LspPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

internal sealed record LspRange(
    [property: JsonPropertyName("start")] LspPosition Start,
    [property: JsonPropertyName("end")] LspPosition End);

internal sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

internal sealed record VersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

internal sealed record TextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

internal sealed record DidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);

internal sealed record DidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

internal sealed record TextDocumentContentChangeEvent(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("range")] LspRange? Range = null,
    [property: JsonPropertyName("rangeLength")] int? RangeLength = null);

internal sealed record DidChangeTextDocumentParams(
    [property: JsonPropertyName("textDocument")] VersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);

internal sealed record TextDocumentPositionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] LspPosition Position);

internal sealed record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

internal sealed record ReferenceParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] LspPosition Position,
    [property: JsonPropertyName("context")] ReferenceContext Context);

internal sealed record RenameParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] LspPosition Position,
    [property: JsonPropertyName("newName")] string NewName);

internal sealed record DocumentDiagnosticParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("identifier")] string? Identifier = null,
    [property: JsonPropertyName("previousResultId")] string? PreviousResultId = null);

internal sealed record WorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

internal sealed record ClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

internal sealed class InitializeParams
{
    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("clientInfo")]
    public required ClientInfo ClientInfo { get; init; }

    [JsonPropertyName("rootUri")]
    public required string RootUri { get; init; }

    [JsonPropertyName("workspaceFolders")]
    public required IReadOnlyList<WorkspaceFolder> WorkspaceFolders { get; init; }

    [JsonPropertyName("capabilities")]
    public required object Capabilities { get; init; }

    [JsonPropertyName("trace")]
    public string Trace { get; init; } = "off";
}

internal sealed record SolutionOpenParams(
    [property: JsonPropertyName("solution")] string Solution);

internal sealed record ProjectOpenParams(
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

internal sealed record ConfigurationItem(
    [property: JsonPropertyName("scopeUri")] string? ScopeUri,
    [property: JsonPropertyName("section")] string? Section);

internal sealed record ConfigurationParams(
    [property: JsonPropertyName("items")] IReadOnlyList<ConfigurationItem> Items);

internal enum CompletionSemanticOriginKind
{
    Unknown,
    Local,
    CurrentType,
    BaseType,
    OtherUserCode,
    FrameworkOrOther,
}

internal sealed record CompletionItemSummary(
    string Label,
    int? Kind,
    string? Detail,
    CompletionSemanticOriginKind? SemanticOrigin = null,
    int? InheritanceDepth = null,
    bool SemanticOriginMetadataMalformed = false);

internal enum CompletionResponseResultKind
{
    Null,
    Undefined,
    Array,
    CompletionList,
    UnexpectedObject,
    UnexpectedValueKind,
}

internal sealed record CompletionResponseEvidence(
    CompletionResponseResultKind ResultKind,
    int RawItemCount,
    bool? IsIncomplete);

internal sealed record CompletionRequestResult(
    IReadOnlyList<CompletionItemSummary> Items,
    double DurationMs,
    CompletionResponseEvidence Evidence)
{
    public void Deconstruct(out IReadOnlyList<CompletionItemSummary> items, out double durationMs)
    {
        items = Items;
        durationMs = DurationMs;
    }
}


internal sealed record LspLocationSummary(string Uri, LspRange Range);

internal sealed record DynamicRegistrationSummary(string Id, string Method, bool Registered);

internal sealed record DiagnosticSummary(string? Code, int? Severity, string Message, LspRange? Range);

internal static class LspJson
{
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }
}

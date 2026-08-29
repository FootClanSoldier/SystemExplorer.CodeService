using System.Text.Json;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal sealed record RoslynServerCapabilities(
    string TextDocumentSync,
    bool CompletionProvider,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool RenameProvider,
    bool PrepareRenameProvider,
    bool DiagnosticProvider,
    bool WorkspaceFoldersSupported,
    IReadOnlyList<string> DynamicRegistrationMethods)
{
    public static RoslynServerCapabilities FromInitializeResult(
        JsonElement initializeResult,
        IReadOnlyList<DynamicRegistrationSummary>? dynamicRegistrations = null)
    {
        JsonElement capabilities = initializeResult;
        if (LspJson.TryGetProperty(initializeResult, "capabilities", out JsonElement nested))
            capabilities = nested;

        string sync = Describe(capabilities, "textDocumentSync");
        bool completion = Present(capabilities, "completionProvider");
        bool definition = Truthy(capabilities, "definitionProvider");
        bool references = Truthy(capabilities, "referencesProvider");
        bool rename = Truthy(capabilities, "renameProvider");
        bool prepareRename = false;
        if (LspJson.TryGetProperty(capabilities, "renameProvider", out JsonElement renameElement)
            && renameElement.ValueKind == JsonValueKind.Object
            && renameElement.TryGetProperty("prepareProvider", out JsonElement prepare))
        {
            prepareRename = prepare.ValueKind == JsonValueKind.True;
        }

        bool diagnostic = Present(capabilities, "diagnosticProvider");
        bool workspaceFolders = false;
        if (LspJson.TryGetProperty(capabilities, "workspace", out JsonElement workspace)
            && workspace.TryGetProperty("workspaceFolders", out JsonElement folders))
        {
            workspaceFolders = folders.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Object when folders.TryGetProperty("supported", out JsonElement supported)
                    => supported.ValueKind == JsonValueKind.True,
                _ => false,
            };
        }

        string[] dynamicMethods = dynamicRegistrations is null
            ? []
            : dynamicRegistrations
                .GroupBy(static registration => registration.Id, StringComparer.Ordinal)
                .Select(static registrations => registrations.Last())
                .Where(static registration => registration.Registered)
                .Select(static registration => registration.Method)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static method => method, StringComparer.Ordinal)
                .ToArray();

        return new RoslynServerCapabilities(
            sync, completion, definition, references, rename, prepareRename,
            diagnostic, workspaceFolders, dynamicMethods);
    }

    public bool HasDynamicRegistration(string method) =>
        DynamicRegistrationMethods.Contains(method, StringComparer.Ordinal);

    private static bool Present(JsonElement capabilities, string name) =>
        capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.False and not JsonValueKind.Undefined;

    private static bool Truthy(JsonElement capabilities, string name)
    {
        if (!LspJson.TryGetProperty(capabilities, name, out JsonElement value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object => true,
            _ => false,
        };
    }

    private static string Describe(JsonElement capabilities, string name)
    {
        if (!LspJson.TryGetProperty(capabilities, name, out JsonElement value))
            return "not-advertised";

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
        {
            return numeric switch
            {
                0 => "None",
                1 => "Full",
                2 => "Incremental",
                _ => numeric.ToString(),
            };
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("change", out JsonElement change) && change.TryGetInt32(out int changeKind))
                return changeKind == 2 ? "Incremental" : changeKind == 1 ? "Full" : changeKind.ToString();
            return "object";
        }

        return value.ToString();
    }
}

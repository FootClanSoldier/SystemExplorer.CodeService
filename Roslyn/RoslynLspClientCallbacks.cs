using System.Text.Json;
using StreamJsonRpc;

namespace SystemExplorer.CodeService;

internal sealed class RoslynLspClientCallbacks
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<bool> _projectInitializationComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, RoslynDynamicRegistration> _registrations = new(StringComparer.Ordinal);

    [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
    public object?[] WorkspaceConfiguration(RoslynConfigurationParams request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new object?[request.Items.Count];
    }

    [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
    public object? RegisterCapability(JsonElement request)
    {
        if (request.TryGetProperty("registrations", out JsonElement registrations) && registrations.ValueKind == JsonValueKind.Array)
        {
            lock (_sync)
            {
                foreach (JsonElement item in registrations.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? id = idElement.GetString();
                    string? method = methodElement.GetString();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method))
                    {
                        continue;
                    }

                    string? identifier = null;
                    bool? interFileDependencies = null;
                    bool? workspaceDiagnostics = null;
                    if (item.TryGetProperty("registerOptions", out JsonElement options)
                        && options.ValueKind == JsonValueKind.Object)
                    {
                        if (options.TryGetProperty("identifier", out JsonElement identifierElement)
                            && identifierElement.ValueKind == JsonValueKind.String)
                        {
                            identifier = identifierElement.GetString();
                        }
                        if (options.TryGetProperty("interFileDependencies", out JsonElement interFileElement)
                            && interFileElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            interFileDependencies = interFileElement.GetBoolean();
                        }
                        if (options.TryGetProperty("workspaceDiagnostics", out JsonElement workspaceElement)
                            && workspaceElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            workspaceDiagnostics = workspaceElement.GetBoolean();
                        }
                    }

                    _registrations[id] = new RoslynDynamicRegistration(id, method, identifier, interFileDependencies, workspaceDiagnostics);
                }
            }
        }

        return null;
    }

    [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
    public object? UnregisterCapability(JsonElement request)
    {
        JsonElement entries;
        if (!(request.TryGetProperty("unregistrations", out entries) || request.TryGetProperty("unregisterations", out entries))
            || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        lock (_sync)
        {
            foreach (JsonElement item in entries.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    string? id = idElement.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _registrations.Remove(id);
                    }
                }
            }
        }

        return null;
    }

    [JsonRpcMethod("workspace/diagnostic/refresh")]
    public object? WorkspaceDiagnosticRefresh() => null;

    public bool HasDynamicDiagnosticRegistration()
        => HasDynamicRegistration("textDocument/diagnostic");

    public bool HasDynamicCompletionRegistration()
        => HasDynamicRegistration("textDocument/completion");

    private bool HasDynamicRegistration(string method)
    {
        lock (_sync)
        {
            return _registrations.Values.Any(registration =>
                string.Equals(registration.Method, method, StringComparison.Ordinal));
        }
    }

    [JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)] public object? WindowShowMessageRequest(JsonElement request) => null;
    [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)] public object? WorkDoneProgressCreate(JsonElement request) => null;
    [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)] public void WindowLogMessage(JsonElement request) { }
    [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)] public void WindowShowMessage(JsonElement request) { }
    [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)] public void Progress(JsonElement request) { }
    [JsonRpcMethod("workspace/projectInitializationComplete")] public void ProjectInitializationComplete() => _projectInitializationComplete.TrySetResult(true);

    public async Task WaitForProjectInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _projectInitializationComplete.Task.WaitAsync(RoslynLanguageServerConstants.ProjectInitializationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("Roslyn workspace/projectInitializationComplete was not observed within the bounded project-load timeout.", exception);
        }
    }
}

internal readonly record struct RoslynDynamicRegistration(string Id, string Method, string? DiagnosticIdentifier, bool? InterFileDependencies, bool? WorkspaceDiagnostics);

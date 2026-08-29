using System.Text.Json;
using StreamJsonRpc;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal sealed class RoslynLspClientCallbacks
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<bool> _projectInitializationComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DynamicRegistrationSummary> _registrations = [];
    private readonly List<string> _configurationRequests = [];
    private readonly Dictionary<string, PublishedDiagnosticsState> _publishedDiagnostics =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _diagnosticUriOrder = new();
    private readonly List<string> _messages = [];
    private readonly List<string> _serverRequests = [];
    private readonly List<string> _unsupportedServerRequests = [];
    private long _diagnosticPublicationSequence;

    public bool ProjectInitializationNotificationObserved => _projectInitializationComplete.Task.IsCompleted;

    public IReadOnlyList<DynamicRegistrationSummary> DynamicRegistrations
    {
        get { lock (_sync) return _registrations.ToArray(); }
    }

    public IReadOnlyList<string> ConfigurationRequests
    {
        get { lock (_sync) return _configurationRequests.ToArray(); }
    }

    public IReadOnlyList<string> Messages
    {
        get { lock (_sync) return _messages.ToArray(); }
    }

    public IReadOnlyList<string> ServerRequests
    {
        get { lock (_sync) return _serverRequests.ToArray(); }
    }

    public IReadOnlyList<string> UnsupportedServerRequests
    {
        get { lock (_sync) return _unsupportedServerRequests.ToArray(); }
    }

    internal void CaptureUnsupportedServerRequest(string description)
    {
        lock (_sync)
            AddBounded(_unsupportedServerRequests, Truncate(description, 1024));
    }

    [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
    public object?[] WorkspaceConfiguration(ConfigurationParams request)
    {
        object?[] response = new object?[request.Items.Count];
        lock (_sync)
        {
            foreach (ConfigurationItem item in request.Items)
            {
                AddBounded(_configurationRequests,
                    $"section={item.Section ?? "<null>"}; value=null");
            }
            AddBounded(_serverRequests, "workspace/configuration");
        }
        return response;
    }

    [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
    public object? RegisterCapability(JsonElement request)
    {
        CaptureRegistrations(request, registered: true);
        CaptureServerRequest("client/registerCapability");
        return null;
    }

    [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
    public object? UnregisterCapability(JsonElement request)
    {
        CaptureRegistrations(request, registered: false);
        CaptureServerRequest("client/unregisterCapability");
        return null;
    }

    [JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)]
    public object? WindowShowMessageRequest(JsonElement request)
    {
        CaptureMessage("show-request", request);
        CaptureServerRequest("window/showMessageRequest");
        return null;
    }

    [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
    public object? WorkDoneProgressCreate(JsonElement request)
    {
        CaptureMessage("progress-create", request);
        CaptureServerRequest("window/workDoneProgress/create");
        return null;
    }

    [JsonRpcMethod("workspace/diagnostic/refresh")]
    public object? WorkspaceDiagnosticRefresh()
    {
        CaptureServerRequest("workspace/diagnostic/refresh");
        return null;
    }

    [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
    public void WindowLogMessage(JsonElement request) => CaptureMessage("log", request);

    [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
    public void WindowShowMessage(JsonElement request) => CaptureMessage("show", request);

    [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
    public void Progress(JsonElement request) => CaptureMessage("progress", request);

    [JsonRpcMethod("workspace/projectInitializationComplete")]
    public void ProjectInitializationComplete()
    {
        _projectInitializationComplete.TrySetResult(true);
    }

    [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
    public void PublishDiagnostics(JsonElement request)
    {
        if (!request.TryGetProperty("uri", out JsonElement uriElement))
            return;
        string? uri = uriElement.GetString();
        if (uri is null)
            return;

        List<DiagnosticSummary> diagnostics = [];
        if (request.TryGetProperty("diagnostics", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray().Take(ProbeConstants.MaxDiagnosticItems))
                diagnostics.Add(ParseDiagnostic(item));
        }

        lock (_sync)
        {
            if (!_publishedDiagnostics.ContainsKey(uri))
            {
                if (_publishedDiagnostics.Count >= ProbeConstants.MaxPublishedDiagnosticDocuments
                    && _diagnosticUriOrder.TryDequeue(out string? oldest))
                {
                    _publishedDiagnostics.Remove(oldest);
                }
                _diagnosticUriOrder.Enqueue(uri);
            }
            _publishedDiagnostics[uri] = new PublishedDiagnosticsState(
                diagnostics,
                ++_diagnosticPublicationSequence);
        }
    }

    public async Task<bool> WaitForProjectInitializationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await _projectInitializationComplete.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public IReadOnlyList<DiagnosticSummary> GetPublishedDiagnostics(string uri)
    {
        lock (_sync)
            return _publishedDiagnostics.TryGetValue(uri, out PublishedDiagnosticsState? value)
                ? value.Diagnostics.ToArray()
                : [];
    }

    public long GetPublishedDiagnosticsSequence(string uri)
    {
        lock (_sync)
            return _publishedDiagnostics.TryGetValue(uri, out PublishedDiagnosticsState? value)
                ? value.Sequence
                : 0;
    }

    public async Task<IReadOnlyList<DiagnosticSummary>> WaitForPublishedDiagnosticsAsync(
        string uri,
        Func<IReadOnlyList<DiagnosticSummary>, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        long afterSequence = 0)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishedDiagnosticsState? state = GetPublishedDiagnosticsState(uri);
            if (state is not null && state.Sequence > afterSequence && predicate(state.Diagnostics))
                return state.Diagnostics.ToArray();
            await Task.Delay(ProbeConstants.CallbackObservationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        PublishedDiagnosticsState? final = GetPublishedDiagnosticsState(uri);
        return final?.Diagnostics.ToArray() ?? [];
    }

    private PublishedDiagnosticsState? GetPublishedDiagnosticsState(string uri)
    {
        lock (_sync)
            return _publishedDiagnostics.TryGetValue(uri, out PublishedDiagnosticsState? value)
                ? value
                : null;
    }

    private void CaptureRegistrations(JsonElement request, bool registered)
    {
        string arrayName = registered ? "registrations" : "unregisterations";
        if (!request.TryGetProperty(arrayName, out JsonElement array))
        {
            if (!registered && request.TryGetProperty("unregistrations", out JsonElement corrected))
                array = corrected;
            else
                return;
        }

        if (array.ValueKind != JsonValueKind.Array)
            return;

        lock (_sync)
        {
            foreach (JsonElement registration in array.EnumerateArray())
            {
                string id = registration.TryGetProperty("id", out JsonElement idElement)
                    ? idElement.GetString() ?? "<unknown>"
                    : "<unknown>";
                string method = registration.TryGetProperty("method", out JsonElement methodElement)
                    ? methodElement.GetString() ?? "<unknown>"
                    : "<unknown>";
                AddBounded(_registrations, new DynamicRegistrationSummary(id, method, registered));
            }
        }
    }

    private void CaptureServerRequest(string method)
    {
        lock (_sync)
            AddBounded(_serverRequests, method);
    }

    private void CaptureMessage(string kind, JsonElement request)
    {
        string summary = kind;
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("message", out JsonElement message))
        {
            string text = message.GetString() ?? string.Empty;
            summary = $"{kind}: {Truncate(text, 512)}";
        }
        lock (_sync)
            AddBounded(_messages, summary);
    }

    private static DiagnosticSummary ParseDiagnostic(JsonElement item)
    {
        string? code = null;
        if (item.TryGetProperty("code", out JsonElement codeElement))
            code = codeElement.ToString();
        int? severity = item.TryGetProperty("severity", out JsonElement severityElement)
            && severityElement.TryGetInt32(out int parsedSeverity) ? parsedSeverity : null;
        string message = item.TryGetProperty("message", out JsonElement messageElement)
            ? Truncate(messageElement.GetString() ?? string.Empty, 1024)
            : string.Empty;
        LspRange? range = item.TryGetProperty("range", out JsonElement rangeElement)
            ? RoslynLspClient.ParseRange(rangeElement)
            : null;
        return new DiagnosticSummary(code, severity, message, range);
    }

    private sealed record PublishedDiagnosticsState(
        IReadOnlyList<DiagnosticSummary> Diagnostics,
        long Sequence);

    private static void AddBounded<T>(List<T> list, T value)
    {
        if (list.Count >= ProbeConstants.MaxCallbackEvents)
            list.RemoveAt(0);
        list.Add(value);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];
}

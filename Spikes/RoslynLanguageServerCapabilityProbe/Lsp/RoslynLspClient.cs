using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal enum RoslynLspClientCapabilityProfile
{
    ProbeBaseline,
    ProductionCompletionWire,
}

internal sealed class RoslynLspClient : IAsyncDisposable
{
    private readonly RoslynLanguageServerProcess _process;
    private readonly JsonRpc _rpc;
    private readonly RoslynLspTraceListener _traceListener;
    private int _disposed;
    private readonly RoslynLspClientCapabilityProfile _capabilityProfile;

    public RoslynLspClient(RoslynLanguageServerProcess process, RoslynLspClientCapabilityProfile capabilityProfile = RoslynLspClientCapabilityProfile.ProbeBaseline)
    {
        _process = process;
        _capabilityProfile = capabilityProfile;
        Callbacks = new RoslynLspClientCallbacks();
        SystemTextJsonFormatter formatter = new();
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        HeaderDelimitedMessageHandler handler = new(process.StandardInput, process.StandardOutput, formatter);
        _rpc = new JsonRpc(handler);
        _traceListener = new RoslynLspTraceListener(Callbacks);
        _rpc.TraceSource.Listeners.Clear();
        _rpc.TraceSource.Listeners.Add(_traceListener);
        _rpc.TraceSource.Switch.Level = SourceLevels.Warning;
        _rpc.AddLocalRpcTarget(Callbacks);
        _rpc.StartListening();
    }

    public RoslynLspClientCallbacks Callbacks { get; }
    public JsonElement InitializeResult { get; private set; }
    public RoslynServerCapabilities? ServerCapabilities { get; private set; }

    public async Task<JsonElement> InitializeAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        string rootUri = LspJson.FileUri(workspaceRoot);
        InitializeParams request = new()
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new ClientInfo("SystemExplorer.CodeService Roslyn capability probe", ProbeConstants.ProbeVersion),
            RootUri = rootUri,
            WorkspaceFolders = [new WorkspaceFolder(rootUri, Path.GetFileName(workspaceRoot))],
            Capabilities = CreateClientCapabilities(_capabilityProfile),
        };

        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.InitializeTimeout, cancellationToken);
        InitializeResult = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize", request, deadline.Token).ConfigureAwait(false);
        ServerCapabilities = RoslynServerCapabilities.FromInitializeResult(InitializeResult);
        await SendParameterObjectNotificationAsync("initialized", new { }, deadline.Token).ConfigureAwait(false);
        return InitializeResult;
    }

    public async Task OpenWorkspaceAsync(string solutionOrProjectPath, CancellationToken cancellationToken)
    {
        string uri = LspJson.FileUri(solutionOrProjectPath);
        string extension = Path.GetExtension(solutionOrProjectPath);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            await SendParameterObjectNotificationAsync("solution/open", new SolutionOpenParams(uri), cancellationToken).ConfigureAwait(false);
        }
        else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            await SendParameterObjectNotificationAsync("project/open", new ProjectOpenParams([uri]), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported workspace file: {solutionOrProjectPath}");
        }
    }

    public Task DidOpenAsync(string path, string text, int version, CancellationToken cancellationToken) =>
        SendParameterObjectNotificationAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(LspJson.FileUri(path), "csharp", version, text)),
            cancellationToken);

    public Task DidCloseAsync(string path, CancellationToken cancellationToken) =>
        SendParameterObjectNotificationAsync(
            "textDocument/didClose",
            new DidCloseTextDocumentParams(new TextDocumentIdentifier(LspJson.FileUri(path))),
            cancellationToken);

    public Task DidChangeFullAsync(string path, string text, int version, CancellationToken cancellationToken) =>
        SendParameterObjectNotificationAsync(
            "textDocument/didChange",
            new DidChangeTextDocumentParams(
                new VersionedTextDocumentIdentifier(LspJson.FileUri(path), version),
                [new TextDocumentContentChangeEvent(text)]),
            cancellationToken);

    public Task DidChangeIncrementalAsync(
        string path,
        string newText,
        int version,
        LspRange range,
        int? rangeLength,
        CancellationToken cancellationToken) =>
        SendParameterObjectNotificationAsync(
            "textDocument/didChange",
            new DidChangeTextDocumentParams(
                new VersionedTextDocumentIdentifier(LspJson.FileUri(path), version),
                [new TextDocumentContentChangeEvent(newText, range, rangeLength)]),
            cancellationToken);

    public async Task<CompletionRequestResult> CompletionAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(LspJson.FileUri(path)), position),
            deadline.Token).ConfigureAwait(false);
        stopwatch.Stop();

        (IReadOnlyList<CompletionItemSummary> items, CompletionResponseEvidence evidence) =
            NormalizeCompletionResponse(result);
        return new CompletionRequestResult(items, stopwatch.Elapsed.TotalMilliseconds, evidence);
    }

    public async Task<IReadOnlyList<LspLocationSummary>> DefinitionAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/definition",
            new TextDocumentPositionParams(new TextDocumentIdentifier(LspJson.FileUri(path)), position),
            deadline.Token).ConfigureAwait(false);
        return NormalizeLocations(result);
    }

    public async Task<IReadOnlyList<LspLocationSummary>> ReferencesAsync(
        string path,
        LspPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/references",
            new ReferenceParams(
                new TextDocumentIdentifier(LspJson.FileUri(path)), position,
                new ReferenceContext(includeDeclaration)),
            deadline.Token).ConfigureAwait(false);
        return NormalizeLocations(result);
    }

    public async Task<JsonElement> PrepareRenameAsync(
        string path,
        LspPosition position,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/prepareRename",
            new TextDocumentPositionParams(new TextDocumentIdentifier(LspJson.FileUri(path)), position),
            deadline.Token).ConfigureAwait(false);
    }

    public async Task<JsonElement> RenameAsync(
        string path,
        LspPosition position,
        string newName,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/rename",
            new RenameParams(new TextDocumentIdentifier(LspJson.FileUri(path)), position, newName),
            deadline.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticSummary>> PullDiagnosticsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.RequestTimeout, cancellationToken);
        JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "textDocument/diagnostic",
            new DocumentDiagnosticParams(new TextDocumentIdentifier(LspJson.FileUri(path))),
            deadline.Token).ConfigureAwait(false);
        return NormalizeDiagnosticReport(result);
    }

    public void RefreshDynamicCapabilities()
    {
        ServerCapabilities = RoslynServerCapabilities.FromInitializeResult(
            InitializeResult,
            Callbacks.DynamicRegistrations);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited)
            return;

        using CancellationTokenSource deadline = CreateDeadline(ProbeConstants.GracefulShutdownTimeout, cancellationToken);
        await _rpc.InvokeWithCancellationAsync("shutdown", null, deadline.Token).ConfigureAwait(false);
        await SendNoParameterNotificationAsync("exit", deadline.Token).ConfigureAwait(false);
    }

    public static LspRange? ParseRange(JsonElement element)
    {
        if (!element.TryGetProperty("start", out JsonElement start)
            || !element.TryGetProperty("end", out JsonElement end))
            return null;
        return new LspRange(ParsePosition(start), ParsePosition(end));
    }

    private static LspPosition ParsePosition(JsonElement element) => new(
        element.GetProperty("line").GetInt32(),
        element.GetProperty("character").GetInt32());

    private static (IReadOnlyList<CompletionItemSummary> Items, CompletionResponseEvidence Evidence)
        NormalizeCompletionResponse(JsonElement result)
    {
        JsonElement items;
        CompletionResponseResultKind resultKind;
        int rawItemCount;
        bool? isIncomplete = null;

        switch (result.ValueKind)
        {
            case JsonValueKind.Null:
                return ([], new CompletionResponseEvidence(CompletionResponseResultKind.Null, 0, null));

            case JsonValueKind.Undefined:
                return ([], new CompletionResponseEvidence(CompletionResponseResultKind.Undefined, 0, null));

            case JsonValueKind.Array:
                items = result;
                resultKind = CompletionResponseResultKind.Array;
                rawItemCount = result.GetArrayLength();
                break;

            case JsonValueKind.Object:
                if (result.TryGetProperty("isIncomplete", out JsonElement isIncompleteElement)
                    && (isIncompleteElement.ValueKind == JsonValueKind.True
                        || isIncompleteElement.ValueKind == JsonValueKind.False))
                {
                    isIncomplete = isIncompleteElement.GetBoolean();
                }

                if (!result.TryGetProperty("items", out JsonElement objectItems)
                    || objectItems.ValueKind != JsonValueKind.Array)
                {
                    return ([], new CompletionResponseEvidence(
                        CompletionResponseResultKind.UnexpectedObject,
                        0,
                        isIncomplete));
                }

                items = objectItems;
                resultKind = CompletionResponseResultKind.CompletionList;
                rawItemCount = objectItems.GetArrayLength();
                break;

            default:
                return ([], new CompletionResponseEvidence(
                    CompletionResponseResultKind.UnexpectedValueKind,
                    0,
                    null));
        }

        List<CompletionItemSummary> normalized = [];
        foreach (JsonElement item in items.EnumerateArray().Take(ProbeConstants.MaxCompletionItems))
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out JsonElement labelElement)
                || labelElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? label = labelElement.GetString();
            if (string.IsNullOrEmpty(label))
                continue;
            int? kind = item.TryGetProperty("kind", out JsonElement kindElement)
                && kindElement.ValueKind == JsonValueKind.Number
                && kindElement.TryGetInt32(out int parsedKind) ? parsedKind : null;
            string? detail = item.TryGetProperty("detail", out JsonElement detailElement)
                && detailElement.ValueKind == JsonValueKind.String
                    ? detailElement.GetString()
                    : null;
            ParseSemanticOriginMetadata(item, out CompletionSemanticOriginKind? semanticOrigin, out int? inheritanceDepth, out bool semanticOriginMalformed);
            normalized.Add(new CompletionItemSummary(label, kind, detail, semanticOrigin, inheritanceDepth, semanticOriginMalformed));
        }

        return (normalized, new CompletionResponseEvidence(resultKind, rawItemCount, isIncomplete));
    }

    private static void ParseSemanticOriginMetadata(
        JsonElement item,
        out CompletionSemanticOriginKind? origin,
        out int? depth,
        out bool malformed)
    {
        origin = null;
        depth = null;
        malformed = false;

        bool hasOrigin = item.TryGetProperty(ProbeConstants.CompletionSemanticOriginJsonPropertyName, out JsonElement originElement);
        bool hasDepth = item.TryGetProperty(ProbeConstants.CompletionInheritanceDepthJsonPropertyName, out JsonElement depthElement);
        if (!hasOrigin && !hasDepth)
            return;

        if (!hasOrigin || originElement.ValueKind != JsonValueKind.String
            || !Enum.TryParse(originElement.GetString(), ignoreCase: false, out CompletionSemanticOriginKind parsedOrigin)
            || !Enum.IsDefined(parsedOrigin))
        {
            malformed = true;
            return;
        }

        origin = parsedOrigin;
        if (hasDepth)
        {
            if (depthElement.ValueKind != JsonValueKind.Number || !depthElement.TryGetInt32(out int parsedDepth))
            {
                malformed = true;
                return;
            }
            depth = parsedDepth;
        }

        bool requiresDepth = parsedOrigin is CompletionSemanticOriginKind.CurrentType or CompletionSemanticOriginKind.BaseType;
        if (requiresDepth != hasDepth
            || (parsedOrigin == CompletionSemanticOriginKind.CurrentType && depth != 0)
            || (parsedOrigin == CompletionSemanticOriginKind.BaseType && depth < 1))
        {
            malformed = true;
        }
    }

    private static IReadOnlyList<LspLocationSummary> NormalizeLocations(JsonElement result)
    {
        List<LspLocationSummary> locations = [];
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
            return locations;

        if (result.ValueKind == JsonValueKind.Object)
        {
            if (TryNormalizeLocation(result, out LspLocationSummary? single))
                locations.Add(single);
            return locations;
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in result.EnumerateArray().Take(ProbeConstants.MaxCompletionItems))
            {
                if (TryNormalizeLocation(item, out LspLocationSummary? location))
                    locations.Add(location);
            }
        }
        return locations;
    }

    private static bool TryNormalizeLocation(
        JsonElement item,
        [NotNullWhen(true)] out LspLocationSummary? location)
    {
        location = null;
        if (item.TryGetProperty("uri", out JsonElement uriElement)
            && item.TryGetProperty("range", out JsonElement rangeElement))
        {
            LspRange? range = ParseRange(rangeElement);
            if (range is null)
                return false;
            location = new LspLocationSummary(uriElement.GetString() ?? string.Empty, range);
            return true;
        }

        if (item.TryGetProperty("targetUri", out JsonElement targetUri)
            && item.TryGetProperty("targetSelectionRange", out JsonElement targetRange))
        {
            LspRange? range = ParseRange(targetRange);
            if (range is null)
                return false;
            location = new LspLocationSummary(targetUri.GetString() ?? string.Empty, range);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<DiagnosticSummary> NormalizeDiagnosticReport(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        List<DiagnosticSummary> diagnostics = [];
        foreach (JsonElement item in items.EnumerateArray().Take(ProbeConstants.MaxDiagnosticItems))
        {
            string? code = item.TryGetProperty("code", out JsonElement codeElement) ? codeElement.ToString() : null;
            int? severity = item.TryGetProperty("severity", out JsonElement severityElement)
                && severityElement.TryGetInt32(out int parsedSeverity) ? parsedSeverity : null;
            string message = item.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? string.Empty : string.Empty;
            LspRange? range = item.TryGetProperty("range", out JsonElement rangeElement) ? ParseRange(rangeElement) : null;
            diagnostics.Add(new DiagnosticSummary(code, severity, message, range));
        }
        return diagnostics;
    }

    private static object CreateClientCapabilities(RoslynLspClientCapabilityProfile profile) => profile switch
    {
        RoslynLspClientCapabilityProfile.ProbeBaseline => new
        {
            workspace = new
            {
                configuration = true,
                workspaceFolders = true,
                didChangeConfiguration = new { dynamicRegistration = true },
            },
            textDocument = new
            {
                synchronization = new { dynamicRegistration = true, didSave = false, willSave = false, willSaveWaitUntil = false },
                completion = new { dynamicRegistration = true, completionItem = new { snippetSupport = false } },
                definition = new { dynamicRegistration = true },
                references = new { dynamicRegistration = true },
                rename = new { dynamicRegistration = true, prepareSupport = true },
                diagnostic = new { dynamicRegistration = true, relatedDocumentSupport = false },
                publishDiagnostics = new { relatedInformation = true, versionSupport = true },
            },
            window = new { workDoneProgress = false },
        },
        RoslynLspClientCapabilityProfile.ProductionCompletionWire => new
        {
            _vs_supportsVisualStudioExtensions = true,
            workspace = new { configuration = true, workspaceFolders = true },
            textDocument = new
            {
                synchronization = new { dynamicRegistration = false, didSave = false, willSave = false, willSaveWaitUntil = false },
                diagnostic = new { dynamicRegistration = true },
                completion = new
                {
                    dynamicRegistration = true,
                    completionItem = new { snippetSupport = false, preselectSupport = true },
                },
            },
            window = new { workDoneProgress = false },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };


    private async Task SendParameterObjectNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        Task send = _rpc.NotifyWithParameterObjectAsync(method, parameters);
        await send.WaitAsync(ProbeConstants.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoParameterNotificationAsync(string method, CancellationToken cancellationToken)
    {
        Task send = _rpc.NotifyAsync(method);
        await send.WaitAsync(ProbeConstants.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _rpc.Dispose();
            _traceListener.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

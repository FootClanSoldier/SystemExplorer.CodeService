using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace SystemExplorer.CodeService;

internal sealed class RoslynLspClient : IAsyncDisposable
{
    private const string VsResolveTextEditOnCommitPropertyName = "_vs_resolveTextEditOnCommit";

    private readonly RoslynLanguageServerProcess _process;
    private readonly string _serviceVersion;
    private readonly RoslynLspClientCallbacks _callbacks;
    private readonly JsonRpc _rpc;
    private int _initialized;
    private int _disposed;
    private bool _staticDiagnosticProvider;
    private bool _staticCompletionProvider;

    public RoslynLspClient(
        RoslynLanguageServerProcess process,
        string serviceVersion)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _serviceVersion = string.IsNullOrWhiteSpace(serviceVersion)
            ? throw new ArgumentException("service version is required.", nameof(serviceVersion))
            : serviceVersion;
        _callbacks = new RoslynLspClientCallbacks();

        SystemTextJsonFormatter formatter = new();
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        HeaderDelimitedMessageHandler handler = new(
            _process.StandardInput,
            _process.StandardOutput,
            formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(_callbacks);
        _rpc.StartListening();
    }

    public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    public Task Completion => _rpc.Completion;

    public async Task InitializeAsync(
        WorkspaceIdentity workspaceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        string rootUri = ToFileUri(workspaceIdentity.ProjectRoot);
        string workspaceName = Path.GetFileName(workspaceIdentity.ProjectRoot);
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            workspaceName = "workspace";
        }

        RoslynInitializeParams request = new()
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new RoslynClientInfo("SystemExplorer.CodeService", _serviceVersion),
            RootUri = rootUri,
            WorkspaceFolders = [new RoslynWorkspaceFolder(rootUri, workspaceName)],
            Capabilities = CreateClientCapabilities(),
        };

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.InitializeTimeout);

        try
        {
            JsonElement initializeResult = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "initialize",
                request,
                deadline.Token).ConfigureAwait(false);
            _staticDiagnosticProvider = HasStaticDiagnosticProvider(initializeResult);
            _staticCompletionProvider = HasStaticCompletionProvider(initializeResult);
            await SendParameterObjectNotificationAsync(
                "initialized",
                new { },
                deadline.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _initialized, 1);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Roslyn initialize did not complete within the bounded initialization timeout.",
                exception);
        }
    }

    public async Task OpenProjectAsync(
        RoslynProjectLoadTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        string uri = ToFileUri(target.AbsolutePath);

        switch (target.LoadKind)
        {
            case RoslynProjectLoadKind.Solution:
                await SendParameterObjectNotificationAsync(
                    "solution/open",
                    new RoslynSolutionOpenParams(uri),
                    cancellationToken).ConfigureAwait(false);
                break;

            case RoslynProjectLoadKind.Project:
                await SendParameterObjectNotificationAsync(
                    "project/open",
                    new RoslynProjectOpenParams([uri]),
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    target.LoadKind,
                    "unknown Roslyn project load kind.");
        }
    }

    public Task WaitForProjectInitializationAsync(CancellationToken cancellationToken)
        => _callbacks.WaitForProjectInitializationAsync(cancellationToken);

    public Task DidOpenAsync(
        string absolutePath,
        int version,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        ArgumentNullException.ThrowIfNull(text);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Roslyn document version must be positive.");
        }

        RoslynDidOpenTextDocumentParams parameters = new(
            new RoslynTextDocumentItem(
                ToFileUri(absolutePath),
                "csharp",
                version,
                text));

        return SendDocumentNotificationAsync("textDocument/didOpen", parameters, cancellationToken);
    }

    public Task DidChangeFullAsync(
        string absolutePath,
        int version,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        ArgumentNullException.ThrowIfNull(text);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Roslyn document version must be positive.");
        }

        RoslynDidChangeTextDocumentParams parameters = new(
            new RoslynVersionedTextDocumentIdentifier(ToFileUri(absolutePath), version),
            [new RoslynTextDocumentContentChangeEvent(text)]);

        return SendDocumentNotificationAsync("textDocument/didChange", parameters, cancellationToken);
    }

    public Task DidCloseAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        RoslynDidCloseTextDocumentParams parameters = new(
            new RoslynTextDocumentIdentifier(ToFileUri(absolutePath)));
        return SendDocumentNotificationAsync("textDocument/didClose", parameters, cancellationToken);
    }


    public bool IsDiagnosticCapabilityAvailable
        => _staticDiagnosticProvider || _callbacks.HasDynamicDiagnosticRegistration();

    public async Task<RoslynDiagnosticPullResult> PullDocumentDiagnosticsAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        if (!IsDiagnosticCapabilityAvailable)
        {
            return RoslynDiagnosticPullResult.Unavailable();
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.SemanticReadinessTimeout);
        try
        {
            JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/diagnostic",
                new RoslynDocumentDiagnosticParams(new RoslynTextDocumentIdentifier(ToFileUri(absolutePath)), Identifier: null, PreviousResultId: null),
                deadline.Token).ConfigureAwait(false);
            return RoslynDiagnosticPullResult.Success(CountDiagnostics(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return RoslynDiagnosticPullResult.Timeout();
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return RoslynDiagnosticPullResult.Timeout();
        }
    }

    public bool IsCompletionCapabilityAvailable
        => _staticCompletionProvider || _callbacks.HasDynamicCompletionRegistration();

    public async Task<RoslynCompletionClientResult> CompletionAsync(
        string absolutePath,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        if (line < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line, "LSP completion line must be non-negative.");
        }
        if (character < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(character), character, "LSP completion character must be non-negative.");
        }
        if (!IsCompletionCapabilityAvailable)
        {
            return RoslynCompletionClientResult.Unavailable();
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.CompletionTimeout);

        try
        {
            JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/completion",
                new RoslynTextDocumentPositionParams(
                    new RoslynTextDocumentIdentifier(ToFileUri(absolutePath)),
                    new RoslynPosition(line, character)),
                deadline.Token).ConfigureAwait(false);

            return NormalizeCompletionResponse(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return RoslynCompletionClientResult.Timeout();
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return RoslynCompletionClientResult.Timeout();
        }
    }

    private static RoslynCompletionClientResult NormalizeCompletionResponse(JsonElement result)
    {
        JsonElement items;
        bool serverIsIncomplete = false;

        switch (result.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return RoslynCompletionClientResult.Success([], isIncomplete: false, rawItemCount: 0);

            case JsonValueKind.Array:
                items = result;
                break;

            case JsonValueKind.Object:
                if (result.TryGetProperty("isIncomplete", out JsonElement isIncompleteElement))
                {
                    if (isIncompleteElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        return RoslynCompletionClientResult.Malformed();
                    }
                    serverIsIncomplete = isIncompleteElement.GetBoolean();
                }

                if (!result.TryGetProperty("items", out items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    return RoslynCompletionClientResult.Malformed();
                }
                break;

            default:
                return RoslynCompletionClientResult.Malformed();
        }

        int rawItemCount = items.GetArrayLength();
        bool isIncomplete = serverIsIncomplete || rawItemCount > DocumentCompletionLimits.MaxCompletionItems;
        int normalizedTextUtf8Bytes = 0;
        List<RoslynCompletionItem> normalized = new(Math.Min(rawItemCount, DocumentCompletionLimits.MaxCompletionItems));
        int inspectedCount = 0;

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (inspectedCount >= DocumentCompletionLimits.MaxCompletionItems)
            {
                isIncomplete = true;
                break;
            }
            inspectedCount++;

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("label", out JsonElement labelElement)
                || labelElement.ValueKind != JsonValueKind.String)
            {
                isIncomplete = true;
                continue;
            }

            string? label = labelElement.GetString();
            if (string.IsNullOrEmpty(label))
            {
                isIncomplete = true;
                continue;
            }

            string filterText = label;
            if (item.TryGetProperty("filterText", out JsonElement filterTextElement)
                && filterTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (filterTextElement.ValueKind != JsonValueKind.String
                    || filterTextElement.GetString() is not string explicitFilterText)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                filterText = explicitFilterText;
            }

            string sortText = label;
            if (item.TryGetProperty("sortText", out JsonElement sortTextElement)
                && sortTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (sortTextElement.ValueKind != JsonValueKind.String
                    || sortTextElement.GetString() is not string explicitSortText)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                sortText = explicitSortText;
            }

            bool preselect = false;
            if (item.TryGetProperty("preselect", out JsonElement preselectElement)
                && preselectElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (preselectElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                preselect = preselectElement.GetBoolean();
            }

            if (item.TryGetProperty(VsResolveTextEditOnCommitPropertyName, out JsonElement resolveTextEditElement))
            {
                if (resolveTextEditElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                if (resolveTextEditElement.GetBoolean())
                {
                    isIncomplete = true;
                    continue;
                }
            }

            if (item.TryGetProperty("command", out JsonElement commandElement)
                && commandElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                isIncomplete = true;
                continue;
            }

            if (item.TryGetProperty("additionalTextEdits", out JsonElement additionalTextEditsElement)
                && additionalTextEditsElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (additionalTextEditsElement.ValueKind != JsonValueKind.Array)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                if (additionalTextEditsElement.GetArrayLength() != 0)
                {
                    isIncomplete = true;
                    continue;
                }
            }

            if (item.TryGetProperty("insertTextFormat", out JsonElement insertTextFormatElement))
            {
                if (insertTextFormatElement.ValueKind != JsonValueKind.Number
                    || !insertTextFormatElement.TryGetInt32(out int insertTextFormat))
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                if (insertTextFormat == 2)
                {
                    isIncomplete = true;
                    continue;
                }

                if (insertTextFormat != 1)
                {
                    isIncomplete = true;
                    continue;
                }
            }

            string insertText = label;
            if (item.TryGetProperty("textEdit", out JsonElement textEditElement)
                && textEditElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (textEditElement.ValueKind != JsonValueKind.Object
                    || !textEditElement.TryGetProperty("newText", out JsonElement newTextElement)
                    || newTextElement.ValueKind != JsonValueKind.String
                    || newTextElement.GetString() is not string newText)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                insertText = newText;
            }
            else if (item.TryGetProperty("insertText", out JsonElement insertTextElement)
                && insertTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (insertTextElement.ValueKind != JsonValueKind.String
                    || insertTextElement.GetString() is not string explicitInsertText)
                {
                    return RoslynCompletionClientResult.Malformed();
                }

                insertText = explicitInsertText;
            }

            if (insertText.Length == 0)
            {
                isIncomplete = true;
                continue;
            }

            if (!TryGetBoundedUtf8ByteCount(label, DocumentCompletionLimits.MaxDisplayTextUtf8Bytes, out int labelBytes)
                || !TryGetBoundedUtf8ByteCount(insertText, DocumentCompletionLimits.MaxInsertTextUtf8Bytes, out int insertTextBytes)
                || !TryGetBoundedUtf8ByteCount(filterText, DocumentCompletionLimits.MaxFilterTextUtf8Bytes, out int filterTextBytes)
                || !TryGetBoundedUtf8ByteCount(sortText, DocumentCompletionLimits.MaxSortTextUtf8Bytes, out int sortTextBytes))
            {
                isIncomplete = true;
                continue;
            }

            int itemTextBytes = checked(labelBytes + insertTextBytes + filterTextBytes + sortTextBytes);
            if (normalizedTextUtf8Bytes > DocumentCompletionLimits.MaxNormalizedCompletionTextUtf8Bytes - itemTextBytes)
            {
                isIncomplete = true;
                break;
            }

            int? kind = null;
            if (item.TryGetProperty("kind", out JsonElement kindElement)
                && kindElement.ValueKind == JsonValueKind.Number
                && kindElement.TryGetInt32(out int kindValue))
            {
                kind = kindValue;
            }

            normalized.Add(new RoslynCompletionItem(label, insertText, kind, filterText, sortText, preselect));
            normalizedTextUtf8Bytes += itemTextBytes;
        }

        return RoslynCompletionClientResult.Success(normalized, isIncomplete, rawItemCount);
    }

    private static bool TryGetBoundedUtf8ByteCount(string value, int maximumBytes, out int byteCount)
    {
        if (value.Length > maximumBytes)
        {
            byteCount = 0;
            return false;
        }

        byteCount = Encoding.UTF8.GetByteCount(value);
        return byteCount <= maximumBytes;
    }

    private static bool HasStaticDiagnosticProvider(JsonElement initializeResult)
        => initializeResult.ValueKind == JsonValueKind.Object
            && initializeResult.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("diagnosticProvider", out JsonElement provider)
            && provider.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined and not JsonValueKind.False;

    private static bool HasStaticCompletionProvider(JsonElement initializeResult)
        => initializeResult.ValueKind == JsonValueKind.Object
            && initializeResult.TryGetProperty("capabilities", out JsonElement capabilities)
            && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("completionProvider", out JsonElement provider)
            && provider.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined and not JsonValueKind.False;

    private static int CountDiagnostics(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.GetArrayLength();
        }

        return 0;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited || !IsInitialized)
        {
            return;
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.GracefulShutdownTimeout);

        try
        {
            await _rpc.InvokeWithCancellationAsync(
                "shutdown",
                null,
                deadline.Token).ConfigureAwait(false);
            await _rpc.NotifyAsync("exit")
                .WaitAsync(RoslynLanguageServerConstants.GracefulShutdownTimeout, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Roslyn graceful LSP shutdown did not complete within the bounded shutdown timeout.",
                exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _rpc.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static object CreateClientCapabilities()
        => new
        {
            // Roslyn's public LSP completion shape intentionally defers complex text edits to
            // completionItem/resolve without exposing a generic "complex edit" discriminator.
            // The VS extension marker lets this v1 client fail closed on those items instead of
            // misrepresenting them as plain insertText completions. No VS command/resolve payload
            // is executed by CodeService.
            _vs_supportsVisualStudioExtensions = true,
            workspace = new
            {
                configuration = true,
                workspaceFolders = true,
            },
            textDocument = new
            {
                synchronization = new
                {
                    dynamicRegistration = false,
                    didSave = false,
                    willSave = false,
                    willSaveWaitUntil = false,
                },
                diagnostic = new
                {
                    dynamicRegistration = true,
                },
                completion = new
                {
                    dynamicRegistration = true,
                    completionItem = new
                    {
                        snippetSupport = false,
                        preselectSupport = true,
                    },
                },
            },
            window = new
            {
                workDoneProgress = false,
            },
        };

    private async Task SendParameterObjectNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        Task send = _rpc.NotifyWithParameterObjectAsync(method, parameters);
        await send
            .WaitAsync(RoslynLanguageServerConstants.InitializeTimeout, cancellationToken)
            .ConfigureAwait(false);
    }


    private async Task SendDocumentNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task send = _rpc.NotifyWithParameterObjectAsync(method, parameters);
        try
        {
            await send
                .WaitAsync(RoslynLanguageServerConstants.DocumentSynchronizationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Roslyn document notification '{method}' did not complete within the bounded document synchronization timeout.",
                exception);
        }
    }

    private static string ToFileUri(string path)
        => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}


internal enum RoslynDiagnosticPullOutcome { Success, Unavailable, Timeout }
internal readonly record struct RoslynDiagnosticPullResult(RoslynDiagnosticPullOutcome Outcome, int DiagnosticCount)
{
    public static RoslynDiagnosticPullResult Success(int count) => new(RoslynDiagnosticPullOutcome.Success, count);
    public static RoslynDiagnosticPullResult Unavailable() => new(RoslynDiagnosticPullOutcome.Unavailable, 0);
    public static RoslynDiagnosticPullResult Timeout() => new(RoslynDiagnosticPullOutcome.Timeout, 0);
}

internal sealed record RoslynCompletionItem(
    string DisplayText,
    string InsertText,
    int? Kind,
    string FilterText,
    string SortText,
    bool Preselect);

internal enum RoslynCompletionClientOutcome
{
    Success,
    Unavailable,
    Timeout,
    MalformedResponse,
}

internal readonly record struct RoslynCompletionClientResult(
    RoslynCompletionClientOutcome Outcome,
    IReadOnlyList<RoslynCompletionItem> Items,
    bool IsIncomplete,
    int RawItemCount)
{
    public static RoslynCompletionClientResult Success(IReadOnlyList<RoslynCompletionItem> items, bool isIncomplete, int rawItemCount)
        => new(RoslynCompletionClientOutcome.Success, items, isIncomplete, rawItemCount);

    public static RoslynCompletionClientResult Unavailable()
        => new(RoslynCompletionClientOutcome.Unavailable, [], false, 0);

    public static RoslynCompletionClientResult Timeout()
        => new(RoslynCompletionClientOutcome.Timeout, [], false, 0);

    public static RoslynCompletionClientResult Malformed()
        => new(RoslynCompletionClientOutcome.MalformedResponse, [], false, 0);
}

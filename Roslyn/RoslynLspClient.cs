using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace SystemExplorer.CodeService;

internal sealed class RoslynLspClient : IAsyncDisposable
{
    private const string VsResolveTextEditOnCommitPropertyName = "_vs_resolveTextEditOnCommit";
    private const string SystemExplorerCompletionSemanticOriginPropertyName = "_systemExplorer_completionSemanticOrigin";
    private const string SystemExplorerCompletionInheritanceDepthPropertyName = "_systemExplorer_completionInheritanceDepth";
    private const string SystemExplorerCompletionRequiresImportPropertyName = "_systemExplorer_completionRequiresImport";

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
        RoslynDocumentDiagnosticScope diagnosticScope,
        CancellationToken cancellationToken,
        bool captureTimings)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        string? diagnosticIdentifier = RoslynLanguageServerConstants.GetDocumentDiagnosticIdentifier(diagnosticScope);
        long operationStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
        if (!IsDiagnosticCapabilityAvailable)
        {
            return RoslynDiagnosticPullResult.Unavailable(
                CreateDiagnosticTiming(captureTimings, operationStarted, null, null));
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.SemanticReadinessTimeout);
        long rpcStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
        try
        {
            JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/diagnostic",
                new RoslynDocumentDiagnosticParams(new RoslynTextDocumentIdentifier(ToFileUri(absolutePath)), Identifier: diagnosticIdentifier, PreviousResultId: null),
                deadline.Token).ConfigureAwait(false);
            double? rpcDurationMs = GetElapsedMilliseconds(captureTimings, rpcStarted);

            long inspectionStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
            int diagnosticCount = CountDiagnostics(result);
            double? inspectionDurationMs = GetElapsedMilliseconds(captureTimings, inspectionStarted);

            return RoslynDiagnosticPullResult.Success(
                diagnosticCount,
                CreateDiagnosticTiming(
                    captureTimings,
                    operationStarted,
                    rpcDurationMs,
                    inspectionDurationMs));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return RoslynDiagnosticPullResult.Timeout(
                CreateDiagnosticTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return RoslynDiagnosticPullResult.Timeout(
                CreateDiagnosticTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
    }

    public bool IsCompletionCapabilityAvailable
        => _staticCompletionProvider || _callbacks.HasDynamicCompletionRegistration();

    public async Task<RoslynCompletionClientResult> CompletionAsync(
        string absolutePath,
        int line,
        int character,
        CancellationToken cancellationToken,
        bool captureTimings)
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

        long operationStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
        if (!IsCompletionCapabilityAvailable)
        {
            return RoslynCompletionClientResult.Unavailable(
                CreateCompletionTiming(captureTimings, operationStarted, null, null));
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.CompletionTimeout);

        long rpcStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
        try
        {
            JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "textDocument/completion",
                new RoslynTextDocumentPositionParams(
                    new RoslynTextDocumentIdentifier(ToFileUri(absolutePath)),
                    new RoslynPosition(line, character)),
                deadline.Token).ConfigureAwait(false);
            double? rpcDurationMs = GetElapsedMilliseconds(captureTimings, rpcStarted);

            int observedRawItemCount = GetObservedRawCompletionItemCount(result);
            long normalizationStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
            RoslynCompletionClientResult normalized = NormalizeCompletionResponse(result);
            double? normalizationDurationMs = GetElapsedMilliseconds(captureTimings, normalizationStarted);

            return normalized with
            {
                RawItemCount = Math.Max(normalized.RawItemCount, observedRawItemCount),
                Timing = CreateCompletionTiming(
                    captureTimings,
                    operationStarted,
                    rpcDurationMs,
                    normalizationDurationMs),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return RoslynCompletionClientResult.Timeout(
                CreateCompletionTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return RoslynCompletionClientResult.Timeout(
                CreateCompletionTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
    }

    public async Task<RoslynCompletionResolveClientResult> ResolveImportCompletionAsync(
        RoslynCompletionResolvePayload payload,
        CancellationToken cancellationToken,
        bool captureTimings)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        long operationStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;

        if (payload.SerializedCompletionItemUtf8Length <= 0
            || payload.SerializedCompletionItemUtf8Length > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes)
        {
            return RoslynCompletionResolveClientResult.Malformed(
                CreateCompletionResolveTiming(captureTimings, operationStarted, null, null));
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RoslynLanguageServerConstants.CompletionResolveTimeout);

        long rpcStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
        try
        {
            using JsonDocument requestDocument = JsonDocument.Parse(
                payload.SerializedCompletionItemUtf8,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });

            if (requestDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RoslynCompletionResolveClientResult.Malformed(
                    CreateCompletionResolveTiming(captureTimings, operationStarted, null, null));
            }

            JsonElement result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "completionItem/resolve",
                requestDocument.RootElement,
                deadline.Token).ConfigureAwait(false);
            double? rpcDurationMs = GetElapsedMilliseconds(captureTimings, rpcStarted);

            long normalizationStarted = captureTimings ? Stopwatch.GetTimestamp() : 0;
            RoslynCompletionResolveClientResult normalized = NormalizeCompletionResolveResponse(result, payload);
            double? normalizationDurationMs = GetElapsedMilliseconds(captureTimings, normalizationStarted);

            return normalized with
            {
                Timing = CreateCompletionResolveTiming(
                    captureTimings,
                    operationStarted,
                    rpcDurationMs,
                    normalizationDurationMs),
            };
        }
        catch (JsonException)
        {
            return RoslynCompletionResolveClientResult.Malformed(
                CreateCompletionResolveTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return RoslynCompletionResolveClientResult.Timeout(
                CreateCompletionResolveTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            return RoslynCompletionResolveClientResult.Timeout(
                CreateCompletionResolveTiming(
                    captureTimings,
                    operationStarted,
                    GetElapsedMilliseconds(captureTimings, rpcStarted),
                    null));
        }
    }

    private static RoslynCompletionResolveClientTiming CreateCompletionResolveTiming(
        bool captureTimings,
        long operationStarted,
        double? rpcDurationMs,
        double? normalizationDurationMs)
        => new(
            rpcDurationMs,
            normalizationDurationMs,
            GetElapsedMilliseconds(captureTimings, operationStarted));

    private static RoslynDiagnosticPullTiming CreateDiagnosticTiming(
        bool captureTimings,
        long operationStarted,
        double? rpcDurationMs,
        double? responseInspectionDurationMs)
        => new(
            rpcDurationMs,
            responseInspectionDurationMs,
            GetElapsedMilliseconds(captureTimings, operationStarted));

    private static RoslynCompletionClientTiming CreateCompletionTiming(
        bool captureTimings,
        long operationStarted,
        double? rpcDurationMs,
        double? normalizationDurationMs)
        => new(
            rpcDurationMs,
            normalizationDurationMs,
            GetElapsedMilliseconds(captureTimings, operationStarted));

    private static double? GetElapsedMilliseconds(bool captureTimings, long started)
        => captureTimings
            ? Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds
            : null;

    private static int GetObservedRawCompletionItemCount(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Array)
        {
            return result.GetArrayLength();
        }

        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("items", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.GetArrayLength();
        }

        return 0;
    }

    private const int CompletionItemKindClass = 7;
    private const int CompletionItemKindInterface = 8;
    private const int CompletionItemKindEnum = 13;
    private const int CompletionItemKindStruct = 22;

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
        bool isIncomplete = serverIsIncomplete || rawItemCount > DocumentCompletionLimits.MaxInspectedRoslynCompletionItems;
        int normalizedTextUtf8Bytes = 0;
        int resolvePayloadUtf8Bytes = 0;
        List<RoslynCompletionItem> normalized = new(Math.Min(rawItemCount, DocumentCompletionLimits.MaxInspectedRoslynCompletionItems));
        int inspectedCount = 0;

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (inspectedCount >= DocumentCompletionLimits.MaxInspectedRoslynCompletionItems)
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

            if (!TryNormalizeSemanticOriginMetadata(
                    item,
                    out CompletionSemanticOrigin semanticOrigin,
                    out int? inheritanceDepth))
            {
                return RoslynCompletionClientResult.Malformed(rawItemCount);
            }

            string filterText = label;
            if (item.TryGetProperty("filterText", out JsonElement filterTextElement)
                && filterTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (filterTextElement.ValueKind != JsonValueKind.String
                    || filterTextElement.GetString() is not string explicitFilterText)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                filterText = explicitFilterText;
            }

            string? rawSortText = null;
            string sortText = label;
            if (item.TryGetProperty("sortText", out JsonElement sortTextElement)
                && sortTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (sortTextElement.ValueKind != JsonValueKind.String
                    || sortTextElement.GetString() is not string explicitSortText)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                rawSortText = explicitSortText;
                sortText = explicitSortText;
            }

            bool preselect = false;
            if (item.TryGetProperty("preselect", out JsonElement preselectElement)
                && preselectElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (preselectElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                preselect = preselectElement.GetBoolean();
            }

            bool requiresImport = false;
            if (item.TryGetProperty(SystemExplorerCompletionRequiresImportPropertyName, out JsonElement requiresImportElement))
            {
                if (requiresImportElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                requiresImport = requiresImportElement.GetBoolean();
            }

            bool resolveTextEditOnCommit = false;
            if (item.TryGetProperty(VsResolveTextEditOnCommitPropertyName, out JsonElement resolveTextEditElement))
            {
                if (resolveTextEditElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                resolveTextEditOnCommit = resolveTextEditElement.GetBoolean();
            }

            if (requiresImport && !resolveTextEditOnCommit)
            {
                return RoslynCompletionClientResult.Malformed(rawItemCount);
            }

            int? kind = null;
            if (item.TryGetProperty("kind", out JsonElement kindElement)
                && kindElement.ValueKind == JsonValueKind.Number
                && kindElement.TryGetInt32(out int kindValue))
            {
                kind = kindValue;
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
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
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
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
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

            if (requiresImport)
            {
                if (!IsSupportedImportNamedTypeKind(kind))
                {
                    isIncomplete = true;
                    continue;
                }

                if (!item.TryGetProperty("data", out JsonElement dataElement)
                    || dataElement.ValueKind != JsonValueKind.Object)
                {
                    isIncomplete = true;
                    continue;
                }

                if ((item.TryGetProperty("textEdit", out JsonElement initialTextEditElement)
                        && initialTextEditElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    || (item.TryGetProperty("insertText", out JsonElement initialInsertTextElement)
                        && initialInsertTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined))
                {
                    isIncomplete = true;
                    continue;
                }

                if (!TryGetBoundedUtf8ByteCount(label, DocumentCompletionLimits.MaxDisplayTextUtf8Bytes, out int labelBytes)
                    || !TryGetBoundedUtf8ByteCount(filterText, DocumentCompletionLimits.MaxFilterTextUtf8Bytes, out int filterTextBytes)
                    || !TryGetBoundedUtf8ByteCount(sortText, DocumentCompletionLimits.MaxSortTextUtf8Bytes, out int sortTextBytes))
                {
                    isIncomplete = true;
                    continue;
                }

                int itemTextBytes = checked(labelBytes + filterTextBytes + sortTextBytes);
                if (normalizedTextUtf8Bytes > DocumentCompletionLimits.MaxNormalizedCompletionTextUtf8Bytes - itemTextBytes)
                {
                    isIncomplete = true;
                    break;
                }

                int remainingResolvePayloadUtf8Bytes =
                    DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadAggregateUtf8Bytes - resolvePayloadUtf8Bytes;
                if (remainingResolvePayloadUtf8Bytes <= 0)
                {
                    isIncomplete = true;
                    continue;
                }

                int serializedItemLimit = Math.Min(
                    DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes,
                    remainingResolvePayloadUtf8Bytes);
                if (!TrySerializeCompletionResolvePayload(item, serializedItemLimit, out byte[]? serializedItem))
                {
                    isIncomplete = true;
                    continue;
                }

                RoslynCompletionResolvePayload resolvePayload = new(
                    serializedItem,
                    label,
                    rawSortText);
                normalized.Add(RoslynCompletionItem.Import(
                    label,
                    kind,
                    filterText,
                    sortText,
                    preselect,
                    semanticOrigin,
                    inheritanceDepth,
                    resolvePayload));
                normalizedTextUtf8Bytes += itemTextBytes;
                resolvePayloadUtf8Bytes += serializedItem.Length;
                continue;
            }

            if (resolveTextEditOnCommit)
            {
                isIncomplete = true;
                continue;
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
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                insertText = newText;
            }
            else if (item.TryGetProperty("insertText", out JsonElement insertTextElement)
                && insertTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (insertTextElement.ValueKind != JsonValueKind.String
                    || insertTextElement.GetString() is not string explicitInsertText)
                {
                    return RoslynCompletionClientResult.Malformed(rawItemCount);
                }

                insertText = explicitInsertText;
            }

            if (insertText.Length == 0)
            {
                isIncomplete = true;
                continue;
            }

            if (!TryGetBoundedUtf8ByteCount(label, DocumentCompletionLimits.MaxDisplayTextUtf8Bytes, out int ordinaryLabelBytes)
                || !TryGetBoundedUtf8ByteCount(insertText, DocumentCompletionLimits.MaxInsertTextUtf8Bytes, out int insertTextBytes)
                || !TryGetBoundedUtf8ByteCount(filterText, DocumentCompletionLimits.MaxFilterTextUtf8Bytes, out int ordinaryFilterTextBytes)
                || !TryGetBoundedUtf8ByteCount(sortText, DocumentCompletionLimits.MaxSortTextUtf8Bytes, out int ordinarySortTextBytes))
            {
                isIncomplete = true;
                continue;
            }

            int ordinaryItemTextBytes = checked(ordinaryLabelBytes + insertTextBytes + ordinaryFilterTextBytes + ordinarySortTextBytes);
            if (normalizedTextUtf8Bytes > DocumentCompletionLimits.MaxNormalizedCompletionTextUtf8Bytes - ordinaryItemTextBytes)
            {
                isIncomplete = true;
                break;
            }

            normalized.Add(RoslynCompletionItem.Direct(
                label,
                insertText,
                kind,
                filterText,
                sortText,
                preselect,
                semanticOrigin,
                inheritanceDepth));
            normalizedTextUtf8Bytes += ordinaryItemTextBytes;
        }

        return RoslynCompletionClientResult.Success(normalized, isIncomplete, rawItemCount);
    }

    private static bool TrySerializeCompletionResolvePayload(
        JsonElement item,
        int maximumUtf8Bytes,
        [NotNullWhen(true)] out byte[]? serializedItem)
    {
        serializedItem = null;
        if (maximumUtf8Bytes <= 0
            || maximumUtf8Bytes > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes)
        {
            return false;
        }

        try
        {
            using BoundedCompletionResolvePayloadWriter buffer = new(maximumUtf8Bytes);
            using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });
            item.WriteTo(writer);
            writer.Flush();
            serializedItem = buffer.ToArray();
            return serializedItem.Length > 0;
        }
        catch (CompletionResolvePayloadLimitExceededException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsSupportedImportNamedTypeKind(int? kind)
        => kind is CompletionItemKindClass
            or CompletionItemKindInterface
            or CompletionItemKindEnum
            or CompletionItemKindStruct;

    private static RoslynCompletionResolveClientResult NormalizeCompletionResolveResponse(
        JsonElement result,
        RoslynCompletionResolvePayload payload)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("label", out JsonElement labelElement)
            || labelElement.ValueKind != JsonValueKind.String
            || !string.Equals(labelElement.GetString(), payload.ExpectedLabel, StringComparison.Ordinal))
        {
            return RoslynCompletionResolveClientResult.Malformed();
        }

        string? rawSortText = null;
        if (result.TryGetProperty("sortText", out JsonElement sortTextElement)
            && sortTextElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (sortTextElement.ValueKind != JsonValueKind.String
                || sortTextElement.GetString() is not string explicitSortText)
            {
                return RoslynCompletionResolveClientResult.Malformed();
            }

            rawSortText = explicitSortText;
        }

        if (!string.Equals(rawSortText, payload.ExpectedSortText, StringComparison.Ordinal)
            || !HasRequiredTrueBoolean(result, SystemExplorerCompletionRequiresImportPropertyName)
            || !HasRequiredTrueBoolean(result, VsResolveTextEditOnCommitPropertyName))
        {
            return RoslynCompletionResolveClientResult.Malformed();
        }

        if (result.TryGetProperty("command", out JsonElement commandElement)
            && commandElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return RoslynCompletionResolveClientResult.Malformed();
        }

        if (result.TryGetProperty("additionalTextEdits", out JsonElement additionalTextEditsElement)
            && additionalTextEditsElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (additionalTextEditsElement.ValueKind != JsonValueKind.Array
                || additionalTextEditsElement.GetArrayLength() != 0)
            {
                return RoslynCompletionResolveClientResult.Malformed();
            }
        }

        if (result.TryGetProperty("insertTextFormat", out JsonElement insertTextFormatElement)
            && insertTextFormatElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (insertTextFormatElement.ValueKind != JsonValueKind.Number
                || !insertTextFormatElement.TryGetInt32(out int insertTextFormat)
                || insertTextFormat != 1)
            {
                return RoslynCompletionResolveClientResult.Malformed();
            }
        }

        if (!result.TryGetProperty("textEdit", out JsonElement textEditElement)
            || textEditElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return RoslynCompletionResolveClientResult.Expired();
        }

        if (!TryParseResolvedTextEdit(textEditElement, out DocumentCompletionTextEdit? edit))
        {
            return RoslynCompletionResolveClientResult.Malformed();
        }

        return RoslynCompletionResolveClientResult.Success(edit!);
    }

    private static bool HasRequiredTrueBoolean(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;

    private static bool TryParseResolvedTextEdit(
        JsonElement textEditElement,
        out DocumentCompletionTextEdit? edit)
    {
        edit = null;
        if (textEditElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int rangeCount = 0;
        int newTextCount = 0;
        JsonElement rangeElement = default;
        string? newText = null;

        foreach (JsonProperty property in textEditElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "range":
                    rangeCount++;
                    if (rangeCount != 1 || property.Value.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }
                    rangeElement = property.Value;
                    break;

                case "newText":
                    newTextCount++;
                    if (newTextCount != 1
                        || property.Value.ValueKind != JsonValueKind.String
                        || property.Value.GetString() is not string parsedNewText)
                    {
                        return false;
                    }
                    newText = parsedNewText;
                    break;

                default:
                    return false;
            }
        }

        if (rangeCount != 1
            || newTextCount != 1
            || string.IsNullOrEmpty(newText)
            || !TryGetBoundedUtf8ByteCount(newText, DocumentCompletionLimits.MaxCompletionResolveEditNewTextUtf8Bytes, out _)
            || !TryParseResolvedTextRange(rangeElement, out DocumentCompletionTextRange range))
        {
            return false;
        }

        edit = new DocumentCompletionTextEdit(range, newText);
        return true;
    }

    private static bool TryParseResolvedTextRange(
        JsonElement rangeElement,
        out DocumentCompletionTextRange range)
    {
        range = default;
        int startCount = 0;
        int endCount = 0;
        DocumentCompletionTextPosition start = default;
        DocumentCompletionTextPosition end = default;

        foreach (JsonProperty property in rangeElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "start":
                    startCount++;
                    if (startCount != 1 || !TryParseResolvedTextPosition(property.Value, out start))
                    {
                        return false;
                    }
                    break;

                case "end":
                    endCount++;
                    if (endCount != 1 || !TryParseResolvedTextPosition(property.Value, out end))
                    {
                        return false;
                    }
                    break;

                default:
                    return false;
            }
        }

        if (startCount != 1 || endCount != 1)
        {
            return false;
        }

        if (start.Line > end.Line
            || (start.Line == end.Line && start.Character > end.Character))
        {
            return false;
        }

        range = new DocumentCompletionTextRange(start, end);
        return true;
    }

    private static bool TryParseResolvedTextPosition(
        JsonElement positionElement,
        out DocumentCompletionTextPosition position)
    {
        position = default;
        if (positionElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int lineCount = 0;
        int characterCount = 0;
        int line = 0;
        int character = 0;

        foreach (JsonProperty property in positionElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "line":
                    lineCount++;
                    if (lineCount != 1
                        || property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out line)
                        || line < 0
                        || line > DocumentCompletionLimits.MaxCompletionLine)
                    {
                        return false;
                    }
                    break;

                case "character":
                    characterCount++;
                    if (characterCount != 1
                        || property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out character)
                        || character < 0
                        || character > DocumentCompletionLimits.MaxCompletionCharacter)
                    {
                        return false;
                    }
                    break;

                default:
                    return false;
            }
        }

        if (lineCount != 1 || characterCount != 1)
        {
            return false;
        }

        position = new DocumentCompletionTextPosition(line, character);
        return true;
    }

    private static bool TryNormalizeSemanticOriginMetadata(
        JsonElement item,
        out CompletionSemanticOrigin semanticOrigin,
        out int? inheritanceDepth)
    {
        semanticOrigin = CompletionSemanticOrigin.Unknown;
        inheritanceDepth = null;

        bool hasOrigin = item.TryGetProperty(
            SystemExplorerCompletionSemanticOriginPropertyName,
            out JsonElement originElement);
        bool hasInheritanceDepth = item.TryGetProperty(
            SystemExplorerCompletionInheritanceDepthPropertyName,
            out JsonElement inheritanceDepthElement);

        if (!hasOrigin)
        {
            return !hasInheritanceDepth;
        }

        if (originElement.ValueKind != JsonValueKind.String
            || originElement.GetString() is not string originText)
        {
            return false;
        }

        CompletionSemanticOrigin? parsedOrigin = originText switch
        {
            "Unknown" => CompletionSemanticOrigin.Unknown,
            "Local" => CompletionSemanticOrigin.Local,
            "CurrentType" => CompletionSemanticOrigin.CurrentType,
            "BaseType" => CompletionSemanticOrigin.BaseType,
            "OtherUserCode" => CompletionSemanticOrigin.OtherUserCode,
            "FrameworkOrOther" => CompletionSemanticOrigin.FrameworkOrOther,
            _ => null,
        };

        if (parsedOrigin is null)
        {
            return false;
        }

        semanticOrigin = parsedOrigin.Value;

        if (hasInheritanceDepth)
        {
            if (inheritanceDepthElement.ValueKind != JsonValueKind.Number
                || !inheritanceDepthElement.TryGetInt32(out int parsedDepth))
            {
                return false;
            }

            inheritanceDepth = parsedDepth;
        }

        return semanticOrigin switch
        {
            CompletionSemanticOrigin.CurrentType => inheritanceDepth == 0,
            CompletionSemanticOrigin.BaseType => inheritanceDepth is int depth && depth >= 1,
            CompletionSemanticOrigin.Unknown
                or CompletionSemanticOrigin.Local
                or CompletionSemanticOrigin.OtherUserCode
                or CompletionSemanticOrigin.FrameworkOrOther => inheritanceDepth is null,
            _ => false,
        };
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
            // The VS extension marker lets this commit-safe client fail closed on those items instead of
            // misrepresenting them as plain insertText completions. Private Roslyn 0005 additionally lets
            // SystemExplorer discover import completion while retaining this VS-internal shape. Schema v5
            // stores only exact marked named-type import items as bounded opaque resolve payloads; other
            // complex items, commands and unsupported commit shapes remain fail-closed.
            _vs_supportsVisualStudioExtensions = true,
            _systemExplorer_supportsImportCompletion = true,
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

internal readonly record struct RoslynDiagnosticPullTiming(
    double? RpcDurationMs,
    double? ResponseInspectionDurationMs,
    double? TotalDurationMs);

internal readonly record struct RoslynDiagnosticPullResult(
    RoslynDiagnosticPullOutcome Outcome,
    int DiagnosticCount,
    RoslynDiagnosticPullTiming Timing)
{
    public static RoslynDiagnosticPullResult Success(int count, RoslynDiagnosticPullTiming timing)
        => new(RoslynDiagnosticPullOutcome.Success, count, timing);

    public static RoslynDiagnosticPullResult Unavailable(RoslynDiagnosticPullTiming timing)
        => new(RoslynDiagnosticPullOutcome.Unavailable, 0, timing);

    public static RoslynDiagnosticPullResult Timeout(RoslynDiagnosticPullTiming timing)
        => new(RoslynDiagnosticPullOutcome.Timeout, 0, timing);
}

internal sealed class BoundedCompletionResolvePayloadWriter : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacityBytes = 256;

    private readonly int _maximumBytes;
    private byte[]? _buffer;
    private int _written;
    private bool _disposed;

    public BoundedCompletionResolvePayloadWriter(int maximumBytes)
    {
        if (maximumBytes <= 0
            || maximumBytes > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = maximumBytes;
        int initialCapacity = Math.Min(maximumBytes, InitialCapacityBytes);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public void Advance(int count)
    {
        byte[] buffer = GetOwnedBuffer();
        int logicalCapacity = Math.Min(buffer.Length, _maximumBytes);
        if (count < 0
            || count > _maximumBytes - _written
            || count > logicalCapacity - _written)
        {
            throw new CompletionResolvePayloadLimitExceededException();
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] buffer = GetOwnedBuffer();
        int logicalCapacity = Math.Min(buffer.Length, _maximumBytes);
        return buffer.AsMemory(_written, logicalCapacity - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] buffer = GetOwnedBuffer();
        int logicalCapacity = Math.Min(buffer.Length, _maximumBytes);
        return buffer.AsSpan(_written, logicalCapacity - _written);
    }

    public byte[] ToArray()
        => GetOwnedBuffer().AsSpan(0, _written).ToArray();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        byte[] buffer = GetOwnedBuffer();
        int requiredSize = sizeHint == 0 ? 1 : sizeHint;
        if (requiredSize > _maximumBytes - _written)
        {
            throw new CompletionResolvePayloadLimitExceededException();
        }

        int currentLogicalCapacity = Math.Min(buffer.Length, _maximumBytes);
        if (requiredSize <= currentLogicalCapacity - _written)
        {
            return;
        }

        int requiredCapacity = _written + requiredSize;
        int doubledCapacity = currentLogicalCapacity > _maximumBytes / 2
            ? _maximumBytes
            : currentLogicalCapacity * 2;
        int newLogicalCapacity = Math.Min(
            _maximumBytes,
            Math.Max(requiredCapacity, doubledCapacity));

        byte[] replacement = ArrayPool<byte>.Shared.Rent(newLogicalCapacity);
        if (replacement.Length < requiredCapacity)
        {
            ArrayPool<byte>.Shared.Return(replacement);
            throw new CompletionResolvePayloadLimitExceededException();
        }

        buffer.AsSpan(0, _written).CopyTo(replacement);
        _buffer = replacement;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private byte[] GetOwnedBuffer()
    {
        if (_disposed || _buffer is null)
        {
            throw new ObjectDisposedException(nameof(BoundedCompletionResolvePayloadWriter));
        }

        return _buffer;
    }
}

internal sealed class CompletionResolvePayloadLimitExceededException : Exception
{
}

internal sealed record RoslynCompletionItem(
    string DisplayText,
    string? InsertText,
    int? Kind,
    string FilterText,
    string SortText,
    bool Preselect,
    CompletionSemanticOrigin SemanticOrigin,
    int? InheritanceDepth,
    bool RequiresImport,
    RoslynCompletionResolvePayload? ResolvePayload)
{
    public bool HasValidCommitContract
        => RequiresImport
            ? InsertText is null && ResolvePayload is not null
            : !string.IsNullOrEmpty(InsertText) && ResolvePayload is null;

    public static RoslynCompletionItem Direct(
        string displayText,
        string insertText,
        int? kind,
        string filterText,
        string sortText,
        bool preselect,
        CompletionSemanticOrigin semanticOrigin,
        int? inheritanceDepth)
        => new(
            displayText,
            insertText,
            kind,
            filterText,
            sortText,
            preselect,
            semanticOrigin,
            inheritanceDepth,
            RequiresImport: false,
            ResolvePayload: null);

    public static RoslynCompletionItem Import(
        string displayText,
        int? kind,
        string filterText,
        string sortText,
        bool preselect,
        CompletionSemanticOrigin semanticOrigin,
        int? inheritanceDepth,
        RoslynCompletionResolvePayload resolvePayload)
        => new(
            displayText,
            null,
            kind,
            filterText,
            sortText,
            preselect,
            semanticOrigin,
            inheritanceDepth,
            true,
            resolvePayload);
}

internal sealed class RoslynCompletionResolvePayload
{
    private readonly byte[] _serializedCompletionItemUtf8;

    public RoslynCompletionResolvePayload(
        byte[] serializedCompletionItemUtf8,
        string expectedLabel,
        string? expectedSortText)
    {
        ArgumentNullException.ThrowIfNull(serializedCompletionItemUtf8);
        if (serializedCompletionItemUtf8.Length <= 0
            || serializedCompletionItemUtf8.Length > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serializedCompletionItemUtf8));
        }

        if (string.IsNullOrEmpty(expectedLabel)
            || expectedLabel.Length > DocumentCompletionLimits.MaxDisplayTextUtf8Bytes
            || Encoding.UTF8.GetByteCount(expectedLabel) > DocumentCompletionLimits.MaxDisplayTextUtf8Bytes)
        {
            throw new ArgumentException("expected completion label is invalid or exceeds the completion label bound.", nameof(expectedLabel));
        }

        if (expectedSortText is not null
            && (expectedSortText.Length > DocumentCompletionLimits.MaxSortTextUtf8Bytes
                || Encoding.UTF8.GetByteCount(expectedSortText) > DocumentCompletionLimits.MaxSortTextUtf8Bytes))
        {
            throw new ArgumentException("expected completion sortText exceeds the completion sortText bound.", nameof(expectedSortText));
        }

        _serializedCompletionItemUtf8 = serializedCompletionItemUtf8;
        ExpectedLabel = expectedLabel;
        ExpectedSortText = expectedSortText;
    }

    public ReadOnlyMemory<byte> SerializedCompletionItemUtf8 => _serializedCompletionItemUtf8;
    public int SerializedCompletionItemUtf8Length => _serializedCompletionItemUtf8.Length;
    public string ExpectedLabel { get; }
    public string? ExpectedSortText { get; }
}

internal enum RoslynCompletionClientOutcome
{
    Success,
    Unavailable,
    Timeout,
    MalformedResponse,
}

internal readonly record struct RoslynCompletionClientTiming(
    double? RpcDurationMs,
    double? NormalizationDurationMs,
    double? TotalDurationMs);

internal readonly record struct RoslynCompletionClientResult(
    RoslynCompletionClientOutcome Outcome,
    IReadOnlyList<RoslynCompletionItem> Items,
    bool IsIncomplete,
    int RawItemCount,
    RoslynCompletionClientTiming Timing)
{
    public static RoslynCompletionClientResult Success(IReadOnlyList<RoslynCompletionItem> items, bool isIncomplete, int rawItemCount)
        => new(RoslynCompletionClientOutcome.Success, items, isIncomplete, rawItemCount, default);

    public static RoslynCompletionClientResult Unavailable(RoslynCompletionClientTiming timing = default)
        => new(RoslynCompletionClientOutcome.Unavailable, [], false, 0, timing);

    public static RoslynCompletionClientResult Timeout(RoslynCompletionClientTiming timing = default)
        => new(RoslynCompletionClientOutcome.Timeout, [], false, 0, timing);

    public static RoslynCompletionClientResult Malformed(int rawItemCount = 0)
        => new(RoslynCompletionClientOutcome.MalformedResponse, [], false, rawItemCount, default);
}


internal enum RoslynCompletionResolveClientOutcome
{
    Success,
    CompletionExpired,
    Unavailable,
    Timeout,
    MalformedResponse,
}

internal readonly record struct RoslynCompletionResolveClientTiming(
    double? RpcDurationMs,
    double? NormalizationDurationMs,
    double? TotalDurationMs);

internal readonly record struct RoslynCompletionResolveClientResult(
    RoslynCompletionResolveClientOutcome Outcome,
    DocumentCompletionTextEdit? Edit,
    RoslynCompletionResolveClientTiming Timing)
{
    public static RoslynCompletionResolveClientResult Success(DocumentCompletionTextEdit edit)
        => new(RoslynCompletionResolveClientOutcome.Success, edit, default);

    public static RoslynCompletionResolveClientResult Expired(RoslynCompletionResolveClientTiming timing = default)
        => new(RoslynCompletionResolveClientOutcome.CompletionExpired, null, timing);

    public static RoslynCompletionResolveClientResult Unavailable(RoslynCompletionResolveClientTiming timing = default)
        => new(RoslynCompletionResolveClientOutcome.Unavailable, null, timing);

    public static RoslynCompletionResolveClientResult Timeout(RoslynCompletionResolveClientTiming timing = default)
        => new(RoslynCompletionResolveClientOutcome.Timeout, null, timing);

    public static RoslynCompletionResolveClientResult Malformed(RoslynCompletionResolveClientTiming timing = default)
        => new(RoslynCompletionResolveClientOutcome.MalformedResponse, null, timing);
}

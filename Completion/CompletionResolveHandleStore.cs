namespace SystemExplorer.CodeService;

internal readonly record struct CompletionResolveBatchContext(
    long ClientGeneration,
    Guid EpochId,
    string DocumentPath,
    long ClientVersion,
    WorkspacePublicationIdentity WorkspacePublicationIdentity,
    long RoslynGeneration,
    int RoslynDocumentVersion,
    long RoslynOverlayRevision);

internal readonly record struct CompletionResolveBatchToken(long BatchId);

internal readonly record struct CompletionResolveHandleEntry(
    CompletionResolveBatchContext Context,
    RoslynCompletionResolvePayload Payload);

internal sealed class CompletionResolveHandleStore : IDisposable
{
    private readonly object _sync = new();
    private readonly List<CompletionResolveBatch> _batches = [];
    private long _nextBatchId;
    private bool _shuttingDown;
    private bool _disposed;

    public bool TryObserveBatch(
        CompletionResolveBatchContext context,
        out CompletionResolveBatchToken token)
    {
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                token = default;
                return false;
            }

            long batchId = checked(++_nextBatchId);
            _batches.Add(new CompletionResolveBatch(batchId, context));
            while (_batches.Count > DocumentCompletionLimits.MaxRetainedCompletionResolveBatches)
            {
                _batches.RemoveAt(0);
            }

            token = new CompletionResolveBatchToken(batchId);
            return true;
        }
    }

    public bool TryRegister(
        CompletionResolveBatchToken token,
        RoslynCompletionResolvePayload payload,
        out Guid handle)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                handle = default;
                return false;
            }

            CompletionResolveBatch? batch = null;
            for (int index = 0; index < _batches.Count; index++)
            {
                if (_batches[index].BatchId == token.BatchId)
                {
                    batch = _batches[index];
                    break;
                }
            }

            if (batch is null
                || batch.Handles.Count >= DocumentCompletionLimits.MaxInspectedRoslynCompletionItems
                || payload.SerializedCompletionItemUtf8Length > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadUtf8Bytes
                || batch.PayloadUtf8Bytes > DocumentCompletionLimits.MaxRoslynCompletionResolvePayloadAggregateUtf8Bytes - payload.SerializedCompletionItemUtf8Length)
            {
                handle = default;
                return false;
            }

            Guid candidate;
            do
            {
                candidate = Guid.NewGuid();
            }
            while (candidate == Guid.Empty || ContainsHandleLocked(candidate));

            batch.Handles.Add(candidate, payload);
            batch.PayloadUtf8Bytes += payload.SerializedCompletionItemUtf8Length;
            handle = candidate;
            return true;
        }
    }

    public bool TryGet(Guid handle, out CompletionResolveHandleEntry entry)
    {
        if (handle == Guid.Empty)
        {
            entry = default;
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                entry = default;
                return false;
            }

            for (int index = _batches.Count - 1; index >= 0; index--)
            {
                CompletionResolveBatch batch = _batches[index];
                if (batch.Handles.TryGetValue(handle, out RoslynCompletionResolvePayload? payload)
                    && payload is not null)
                {
                    entry = new CompletionResolveHandleEntry(batch.Context, payload);
                    return true;
                }
            }

            entry = default;
            return false;
        }
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _shuttingDown = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _shuttingDown = true;
            _disposed = true;
            _batches.Clear();
        }
    }

    private bool ContainsHandleLocked(Guid handle)
    {
        for (int index = 0; index < _batches.Count; index++)
        {
            if (_batches[index].Handles.ContainsKey(handle))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CompletionResolveBatch
    {
        public CompletionResolveBatch(long batchId, CompletionResolveBatchContext context)
        {
            BatchId = batchId;
            Context = context;
        }

        public long BatchId { get; }
        public CompletionResolveBatchContext Context { get; }
        public Dictionary<Guid, RoslynCompletionResolvePayload> Handles { get; } = [];
        public int PayloadUtf8Bytes { get; set; }
    }
}

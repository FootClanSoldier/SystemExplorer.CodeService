namespace SystemExplorer.CodeService;

internal static class DocumentSynchronizationLimits
{
    public const int MaxTrackedOpenDocuments = 256;
    public const int MaxDocumentPathLength = 2048;
    public const int MaxDocumentTextUtf8Bytes = 3 * 1024 * 1024;
    public const long MaxTotalTrackedSnapshotUtf8Bytes = 64L * 1024 * 1024;
    public const int MaxEpochRequestBodySizeBytes = 1 * 1024 * 1024;
    public const int MaxSnapshotRequestBodySizeBytes = 16 * 1024 * 1024;
}

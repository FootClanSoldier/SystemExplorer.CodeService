namespace SystemExplorer.CodeService;

internal static class DocumentCompletionLimits
{
    public const int MaxRequestBodySizeBytes = 16 * 1024;
    public const int MaxResponseBodySizeBytes = 4 * 1024 * 1024;
    public const int MaxCompletionItems = 1024;
    public const int MaxDisplayTextUtf8Bytes = 2048;
    public const int MaxInsertTextUtf8Bytes = 4096;
    public const int MaxFilterTextUtf8Bytes = 2048;
    public const int MaxSortTextUtf8Bytes = 2048;
    public const int MaxNormalizedCompletionTextUtf8Bytes = 1024 * 1024;
    public const int MaxCompletionLine = 1_000_000;
    public const int MaxCompletionCharacter = 1_000_000;
}

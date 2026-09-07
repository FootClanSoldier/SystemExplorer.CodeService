using System.Text;

namespace SystemExplorer.CodeService;

internal static class DocumentCompletionLimits
{
    public const int MaxRequestBodySizeBytes = 16 * 1024;
    public const int MaxResponseBodySizeBytes = 4 * 1024 * 1024;
    public const int MaxCompletionResolveRequestBodySizeBytes = 16 * 1024;
    public const int MaxRoslynCompletionResolvePayloadUtf8Bytes = 16 * 1024;
    public const int MaxRoslynCompletionResolvePayloadAggregateUtf8Bytes = 1 * 1024 * 1024;
    public const int MaxCompletionResolveEditNewTextUtf8Bytes = 4 * 1024 * 1024;
    public const int MaxCompletionResolveResponseBodySizeBytes = 24 * 1024 * 1024;
    public const int MaxRetainedCompletionResolveBatches = 3;
    public const int MaxInspectedRoslynCompletionItems = 1024;
    public const int MaxPublishedCompletionItems = 256;
    public const int MaxCompletionPrefixUtf8Bytes = 2048;
    public const int MaxDisplayTextUtf8Bytes = 2048;
    public const int MaxInsertTextUtf8Bytes = 4096;
    public const int MaxFilterTextUtf8Bytes = 2048;
    public const int MaxSortTextUtf8Bytes = 2048;
    public const int MaxNormalizedCompletionTextUtf8Bytes = 1024 * 1024;
    public const int MaxCompletionLine = 1_000_000;
    public const int MaxCompletionCharacter = 1_000_000;

    public static bool IsCompletionPrefixWithinBounds(string? prefix)
    {
        if (prefix is null || prefix.Length > MaxCompletionPrefixUtf8Bytes)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(prefix) <= MaxCompletionPrefixUtf8Bytes;
    }
}

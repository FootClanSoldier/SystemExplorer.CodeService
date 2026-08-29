using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal static class ProbeSourceMarker
{
    public static LspPosition FindUnique(string source, string markerName)
    {
        string marker = $"/*{markerName}*/";
        int first = source.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException($"Marker {markerName} was not found.");
        if (source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Marker {markerName} occurred more than once.");
        return PositionAt(source, first + marker.Length);
    }

    public static LspPosition FindUniqueCompletionPosition(string source, string markerName)
    {
        string marker = $"/*{markerName}*/";
        int first = source.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException($"Marker {markerName} was not found.");
        if (source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Marker {markerName} occurred more than once.");
        if (first <= 0)
            throw new InvalidOperationException($"Completion marker {markerName} must follow a member-access dot.");
        if (source[first - 1] != '.')
            throw new InvalidOperationException($"Completion marker {markerName} was not immediately preceded by a member-access dot.");
        return PositionAt(source, first);
    }

    public static LspPosition FindUniquePositionWithin(string source, string uniqueAnchor, int offset)
    {
        if (string.IsNullOrEmpty(uniqueAnchor))
            throw new ArgumentException("Unique anchor must not be null or empty.", nameof(uniqueAnchor));
        if ((uint)offset > (uint)uniqueAnchor.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int first = source.IndexOf(uniqueAnchor, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException($"Anchor {uniqueAnchor} was not found.");
        if (source.IndexOf(uniqueAnchor, first + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Anchor {uniqueAnchor} occurred more than once.");
        return PositionAt(source, first + offset);
    }

    public static LspRange FindUniqueTokenRange(string source, string token)
    {
        int first = source.IndexOf(token, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidOperationException($"Token {token} was not found.");
        if (source.IndexOf(token, first + token.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Token {token} occurred more than once.");
        return new LspRange(PositionAt(source, first), PositionAt(source, first + token.Length));
    }

    public static LspPosition PositionAt(string source, int absoluteIndex)
    {
        if ((uint)absoluteIndex > (uint)source.Length)
            throw new ArgumentOutOfRangeException(nameof(absoluteIndex));

        int line = 0;
        int character = 0;
        for (int i = 0; i < absoluteIndex; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (source[i] != '\r')
            {
                character++;
            }
        }
        return new LspPosition(line, character);
    }
}

namespace SystemExplorer.CodeService;

internal static class CompletionNamespaceDisambiguator
{
    public static IReadOnlyList<DocumentCompletionItem> Apply(IReadOnlyList<DocumentCompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count < 2)
        {
            return items;
        }

        Dictionary<string, NamespaceGroupState> groups = new(StringComparer.Ordinal);
        foreach (DocumentCompletionItem item in items)
        {
            if (item.ContainingNamespace is not string containingNamespace)
            {
                continue;
            }

            groups.TryGetValue(item.DisplayText, out NamespaceGroupState state);
            state.AuthoritativeCount++;
            if (state.FirstNamespace is null)
            {
                state.FirstNamespace = containingNamespace;
            }
            else if (!string.Equals(state.FirstNamespace, containingNamespace, StringComparison.Ordinal))
            {
                state.HasDifferentNamespace = true;
            }

            groups[item.DisplayText] = state;
        }

        DocumentCompletionItem[]? output = null;
        for (int index = 0; index < items.Count; index++)
        {
            DocumentCompletionItem item = items[index];
            string? namespaceDisambiguation = item.ContainingNamespace is not null
                && groups.TryGetValue(item.DisplayText, out NamespaceGroupState state)
                && state.AuthoritativeCount > 1
                && state.HasDifferentNamespace
                ? item.ContainingNamespace
                : null;

            if (string.Equals(item.NamespaceDisambiguation, namespaceDisambiguation, StringComparison.Ordinal))
            {
                continue;
            }

            output ??= items.ToArray();
            output[index] = item with { NamespaceDisambiguation = namespaceDisambiguation };
        }

        return output ?? items;
    }

    private struct NamespaceGroupState
    {
        public int AuthoritativeCount;
        public string? FirstNamespace;
        public bool HasDifferentNamespace;
    }
}

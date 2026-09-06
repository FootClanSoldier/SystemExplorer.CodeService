namespace SystemExplorer.CodeService;

internal readonly record struct CompletionSemanticDistribution(
    int PreselectCount,
    int LocalCount,
    int CurrentTypeCount,
    int BaseTypeDepth1Count,
    int BaseTypeDepth2Count,
    int DeepBaseTypeCount,
    int OtherUserCodeCount,
    int FrameworkOrOtherCount,
    int UnknownCount);

internal readonly record struct CompletionCandidateSelectionStatistics(
    int CommitSafeInputCount,
    int PrefixMatchCount,
    bool TextualFilterApplied,
    bool TextualFallbackUsed,
    int TextualEligibleCount,
    int DroppedByPrefixCount,
    int DroppedByPublicationBudgetCount,
    int PublishedCount,
    CompletionSemanticDistribution InputDistribution,
    CompletionSemanticDistribution EligibleDistribution,
    CompletionSemanticDistribution PublishedDistribution);

internal readonly record struct CompletionCandidateSelectionResult(
    IReadOnlyList<DocumentCompletionItem> Items,
    bool WasReduced,
    CompletionCandidateSelectionStatistics Statistics);

internal static class CompletionCandidateSelector
{
    public static CompletionCandidateSelectionResult Select(
        IReadOnlyList<DocumentCompletionItem> items,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(prefix);

        int inputCount = items.Count;
        if (inputCount > DocumentCompletionLimits.MaxInspectedRoslynCompletionItems)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                $"Commit-safe completion input exceeds the {DocumentCompletionLimits.MaxInspectedRoslynCompletionItems}-item inspection bound.");
        }

        bool[] textualEligible = new bool[inputCount];
        int prefixMatchCount;
        bool textualFilterApplied;
        bool textualFallbackUsed;
        int textualEligibleCount;

        if (prefix.Length == 0)
        {
            Array.Fill(textualEligible, true);
            prefixMatchCount = inputCount;
            textualFilterApplied = false;
            textualFallbackUsed = false;
            textualEligibleCount = inputCount;
        }
        else
        {
            prefixMatchCount = 0;
            for (int index = 0; index < inputCount; index++)
            {
                if (items[index].FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    prefixMatchCount++;
                }
            }

            if (prefixMatchCount == 0)
            {
                Array.Fill(textualEligible, true);
                textualFilterApplied = false;
                textualFallbackUsed = true;
                textualEligibleCount = inputCount;
            }
            else
            {
                for (int index = 0; index < inputCount; index++)
                {
                    textualEligible[index] = items[index].FilterText.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase);
                }

                textualFilterApplied = true;
                textualFallbackUsed = false;
                textualEligibleCount = prefixMatchCount;
            }
        }

        int droppedByPrefixCount = inputCount - textualEligibleCount;
        bool[] selected = new bool[inputCount];
        int publishedCount;

        if (textualEligibleCount <= DocumentCompletionLimits.MaxPublishedCompletionItems)
        {
            Array.Copy(textualEligible, selected, inputCount);
            publishedCount = textualEligibleCount;
        }
        else
        {
            publishedCount = 0;
            AdmitProtectedCandidates(items, textualEligible, selected, ref publishedCount, CandidateAdmissionPass.Preselect);
            AdmitProtectedCandidates(items, textualEligible, selected, ref publishedCount, CandidateAdmissionPass.LocalOrCurrentType);
            AdmitProtectedCandidates(items, textualEligible, selected, ref publishedCount, CandidateAdmissionPass.ProjectOrNearBaseType);
            AdmitProtectedCandidates(items, textualEligible, selected, ref publishedCount, CandidateAdmissionPass.Fallback);
        }

        int droppedByPublicationBudgetCount = textualEligibleCount - publishedCount;
        DocumentCompletionItem[] output = new DocumentCompletionItem[publishedCount];
        int outputIndex = 0;
        for (int index = 0; index < inputCount; index++)
        {
            if (selected[index])
            {
                output[outputIndex++] = items[index];
            }
        }

        CompletionCandidateSelectionStatistics statistics = new(
            inputCount,
            prefixMatchCount,
            textualFilterApplied,
            textualFallbackUsed,
            textualEligibleCount,
            droppedByPrefixCount,
            droppedByPublicationBudgetCount,
            publishedCount,
            CountSemanticDistribution(items, include: null),
            CountSemanticDistribution(items, textualEligible),
            CountSemanticDistribution(items, selected));

        return new CompletionCandidateSelectionResult(
            output,
            droppedByPrefixCount > 0 || droppedByPublicationBudgetCount > 0,
            statistics);
    }

    private static void AdmitProtectedCandidates(
        IReadOnlyList<DocumentCompletionItem> items,
        bool[] textualEligible,
        bool[] selected,
        ref int selectedCount,
        CandidateAdmissionPass pass)
    {
        for (int index = 0;
             index < items.Count && selectedCount < DocumentCompletionLimits.MaxPublishedCompletionItems;
             index++)
        {
            if (!textualEligible[index]
                || selected[index]
                || !MatchesAdmissionPass(items[index], pass))
            {
                continue;
            }

            selected[index] = true;
            selectedCount++;
        }
    }

    private static bool MatchesAdmissionPass(DocumentCompletionItem item, CandidateAdmissionPass pass)
        => pass switch
        {
            CandidateAdmissionPass.Preselect => item.Preselect,
            CandidateAdmissionPass.LocalOrCurrentType => item.SemanticOrigin is
                CompletionSemanticOrigin.Local or CompletionSemanticOrigin.CurrentType,
            CandidateAdmissionPass.ProjectOrNearBaseType => item.SemanticOrigin == CompletionSemanticOrigin.OtherUserCode
                || (item.SemanticOrigin == CompletionSemanticOrigin.BaseType
                    && item.InheritanceDepth is 1 or 2),
            CandidateAdmissionPass.Fallback => true,
            _ => false,
        };

    private static CompletionSemanticDistribution CountSemanticDistribution(
        IReadOnlyList<DocumentCompletionItem> items,
        bool[]? include)
    {
        int preselectCount = 0;
        int localCount = 0;
        int currentTypeCount = 0;
        int baseTypeDepth1Count = 0;
        int baseTypeDepth2Count = 0;
        int deepBaseTypeCount = 0;
        int otherUserCodeCount = 0;
        int frameworkOrOtherCount = 0;
        int unknownCount = 0;

        for (int index = 0; index < items.Count; index++)
        {
            if (include is not null && !include[index])
            {
                continue;
            }

            DocumentCompletionItem item = items[index];
            if (item.Preselect)
            {
                preselectCount++;
            }

            switch (item.SemanticOrigin)
            {
                case CompletionSemanticOrigin.Local:
                    localCount++;
                    break;
                case CompletionSemanticOrigin.CurrentType:
                    currentTypeCount++;
                    break;
                case CompletionSemanticOrigin.BaseType when item.InheritanceDepth == 1:
                    baseTypeDepth1Count++;
                    break;
                case CompletionSemanticOrigin.BaseType when item.InheritanceDepth == 2:
                    baseTypeDepth2Count++;
                    break;
                case CompletionSemanticOrigin.BaseType when item.InheritanceDepth is >= 3:
                    deepBaseTypeCount++;
                    break;
                case CompletionSemanticOrigin.OtherUserCode:
                    otherUserCodeCount++;
                    break;
                case CompletionSemanticOrigin.FrameworkOrOther:
                    frameworkOrOtherCount++;
                    break;
                case CompletionSemanticOrigin.Unknown:
                    unknownCount++;
                    break;
            }
        }

        return new CompletionSemanticDistribution(
            preselectCount,
            localCount,
            currentTypeCount,
            baseTypeDepth1Count,
            baseTypeDepth2Count,
            deepBaseTypeCount,
            otherUserCodeCount,
            frameworkOrOtherCount,
            unknownCount);
    }

    private enum CandidateAdmissionPass
    {
        Preselect,
        LocalOrCurrentType,
        ProjectOrNearBaseType,
        Fallback,
    }
}

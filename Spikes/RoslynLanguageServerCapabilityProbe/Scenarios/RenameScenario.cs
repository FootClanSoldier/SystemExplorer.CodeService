using System.Text.Json;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class RenameScenario
{
    private const string NewName = "ProbeRenamedSymbol";

    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Rename", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            session.Client.RefreshDynamicCapabilities();
            RoslynServerCapabilities capabilities = session.Client.ServerCapabilities
                ?? throw new InvalidOperationException("Server capabilities unavailable.");
            context.FixtureServerCapabilities = capabilities;
            string consumer = context.Fixture.ReadConsumer();
            LspPosition position = ProbeSourceMarker.FindUnique(consumer, "PROBE_RENAME");

            if (capabilities.PrepareRenameProvider)
            {
                JsonElement prepare = await session.Client.PrepareRenameAsync(
                    context.Fixture.ConsumerPath, position, cancellationToken).ConfigureAwait(false);
                checks.Add(new ProbeCheckResult("PrepareRename", prepare.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined));
            }
            else
            {
                checks.Add(new ProbeCheckResult("PrepareRename", true, "not advertised; rename tested directly"));
            }

            JsonElement workspaceEdit = await session.Client.RenameAsync(
                context.Fixture.ConsumerPath, position, NewName, cancellationToken).ConfigureAwait(false);
            WorkspaceEditSummary summary = SummarizeWorkspaceEdit(workspaceEdit);
            string targetUri = LspJson.FileUri(context.Fixture.TargetPath);
            string consumerUri = LspJson.FileUri(context.Fixture.ConsumerPath);
            checks.Add(new ProbeCheckResult("RenameResponseContainsExpectedFiles",
                summary.Uris.Contains(targetUri, StringComparer.OrdinalIgnoreCase)
                    && summary.Uris.Contains(consumerUri, StringComparer.OrdinalIgnoreCase),
                $"files={summary.Uris.Count}"));
            checks.Add(new ProbeCheckResult("RenameResponseContainsExpectedEdits", summary.EditCount >= 2,
                $"edits={summary.EditCount}"));
            checks.Add(new ProbeCheckResult("RenameNewTextMatches", summary.AllNewTextMatches && summary.EditCount > 0));
            checks.Add(new ProbeCheckResult("RenameNotAppliedToDisk",
                context.Fixture.ReadTarget().Contains("ProbeRenameSymbol", StringComparison.Ordinal)
                    && context.Fixture.ReadConsumer().Contains("ProbeRenameSymbol", StringComparison.Ordinal)
                    && !context.Fixture.ReadTarget().Contains(NewName, StringComparison.Ordinal)
                    && !context.Fixture.ReadConsumer().Contains(NewName, StringComparison.Ordinal)));
            checks.Add(new ProbeCheckResult("ProcessSurvivedRename", !session.Process.HasExited));
        });

    private static WorkspaceEditSummary SummarizeWorkspaceEdit(JsonElement edit)
    {
        HashSet<string> uris = new(StringComparer.OrdinalIgnoreCase);
        int editCount = 0;
        bool allNewTextMatches = true;

        if (edit.ValueKind != JsonValueKind.Object)
            return new WorkspaceEditSummary(uris, 0, false);

        if (edit.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in changes.EnumerateObject())
            {
                uris.Add(property.Name);
                CountEdits(property.Value, ref editCount, ref allNewTextMatches);
            }
        }

        if (edit.TryGetProperty("documentChanges", out JsonElement documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement documentChange in documentChanges.EnumerateArray())
            {
                if (documentChange.ValueKind != JsonValueKind.Object)
                    continue;
                if (documentChange.TryGetProperty("textDocument", out JsonElement textDocument)
                    && textDocument.TryGetProperty("uri", out JsonElement uriElement))
                {
                    string? uri = uriElement.GetString();
                    if (uri is not null)
                        uris.Add(uri);
                }
                if (documentChange.TryGetProperty("edits", out JsonElement edits))
                    CountEdits(edits, ref editCount, ref allNewTextMatches);
            }
        }

        return new WorkspaceEditSummary(uris, editCount, allNewTextMatches);
    }

    private static void CountEdits(JsonElement edits, ref int editCount, ref bool allNewTextMatches)
    {
        if (edits.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement textEdit in edits.EnumerateArray())
        {
            if (!textEdit.TryGetProperty("newText", out JsonElement newText))
                continue;
            editCount++;
            allNewTextMatches &= string.Equals(newText.GetString(), NewName, StringComparison.Ordinal);
        }
    }

    private sealed record WorkspaceEditSummary(HashSet<string> Uris, int EditCount, bool AllNewTextMatches);
}

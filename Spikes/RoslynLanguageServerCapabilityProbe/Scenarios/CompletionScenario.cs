using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class CompletionScenario
{
    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Completion", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            string consumer = context.Fixture.ReadConsumer();
            string target = context.Fixture.ReadTarget();

            var instancePosition = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");
            var (instanceItems, firstMs) = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, instancePosition, cancellationToken).ConfigureAwait(false);
            var (_, warmMs) = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, instancePosition, cancellationToken).ConfigureAwait(false);

            CheckPresent(checks, instanceItems, "ProbeInstanceProperty", "InstanceIncludesProbeInstanceProperty");
            CheckPresent(checks, instanceItems, "ProbeExtension", "InstanceIncludesProbeExtension");
            CheckPresent(checks, instanceItems, "ProbeBasePublic", "InstanceIncludesInheritedPublic");
            CheckAbsent(checks, instanceItems, "ProbeStaticProperty", "InstanceExcludesStaticProperty");
            CheckAbsent(checks, instanceItems, "ProbePrivateField", "InstanceExcludesForeignPrivate");
            checks.Add(new ProbeCheckResult("FirstCompletionLatency", true, null, firstMs));
            checks.Add(new ProbeCheckResult("WarmCompletionLatency", true, null, warmMs));

            var staticPosition = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_STATIC_COMPLETION");
            var (staticItems, _) = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, staticPosition, cancellationToken).ConfigureAwait(false);
            CheckPresent(checks, staticItems, "ProbeStaticProperty", "StaticIncludesProbeStaticProperty");
            CheckAbsent(checks, staticItems, "ProbeInstanceProperty", "StaticExcludesInstanceProperty");

            var privatePosition = ProbeSourceMarker.FindUniqueCompletionPosition(target, "PROBE_PRIVATE_COMPLETION");
            var (privateItems, _) = await session.Client.CompletionAsync(
                context.Fixture.TargetPath, privatePosition, cancellationToken).ConfigureAwait(false);
            CheckPresent(checks, privateItems, "ProbePrivateField", "SameTypeIncludesPrivateField");

            var derivedPosition = ProbeSourceMarker.FindUniqueCompletionPosition(target, "PROBE_DERIVED_COMPLETION");
            var (derivedItems, _) = await session.Client.CompletionAsync(
                context.Fixture.TargetPath, derivedPosition, cancellationToken).ConfigureAwait(false);
            CheckPresent(checks, derivedItems, "ProbeBaseProtected", "DerivedIncludesProtectedMember");

            var genericPosition = ProbeSourceMarker.FindUniqueCompletionPosition(target, "PROBE_GENERIC_COMPLETION");
            var (genericItems, _) = await session.Client.CompletionAsync(
                context.Fixture.TargetPath, genericPosition, cancellationToken).ConfigureAwait(false);
            CheckPresent(checks, genericItems, "ProbeGenericMethod", "GenericMethodDiscoverable");
            checks.Add(new ProbeCheckResult("ProcessSurvivedCompletion", !session.Process.HasExited));
        });

    private static void CheckPresent(List<ProbeCheckResult> checks, IReadOnlyList<CompletionItemSummary> items, string label, string name) =>
        checks.Add(new ProbeCheckResult(name, ScenarioExecution.ContainsLabel(items, label)));

    private static void CheckAbsent(List<ProbeCheckResult> checks, IReadOnlyList<CompletionItemSummary> items, string label, string name) =>
        checks.Add(new ProbeCheckResult(name, !ScenarioExecution.ContainsLabel(items, label)));
}

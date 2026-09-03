using System.Security.Cryptography;
using System.Text;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class RoslynStateLineageTraceScenario
{
    private const string ScenarioName = "RoslynStateLineageTrace";
    private const string NaturalMemberAnchor = "return target.ProbeExtension();";
    private const string EditorMemberPrefix = "return target.";

    public static async Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Options.StateTraceSelected)
        {
            return ProbeScenarioResult.Skipped(
                ScenarioName,
                "No --state-trace-server/--state-trace-provenance supplied.");
        }

        try
        {
            return await ScenarioExecution.RunAsync(ScenarioName, cancellationToken, async checks =>
            {
            string stateTraceServer = context.Options.StateTraceServerPath!;
            string stateTraceProvenance = context.Options.StateTraceProvenancePath!;
            RoslynStateTraceProvenance provenance = await RoslynStateTraceProvenance.LoadAndVerifyAsync(
                stateTraceProvenance,
                stateTraceServer,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "StateTraceProvenanceVerified",
                true,
                $"repository={provenance.Repository}; baseCommit={provenance.BaseCommit}; instrumentationVersion={provenance.InstrumentationVersion}; targetFileName={provenance.TargetFileName}"));

            RoslynLanguageServerLaunchSpec launchSpec = RoslynLanguageServerLaunchSpec.CreateInstrumentation(stateTraceServer);
            string diskTarget = context.Fixture.ReadTarget();
            string diskConsumer = context.Fixture.ReadConsumer();
            EditorSnapshot consumerSnapshot = CreateEditorSnapshot(diskConsumer);
            string targetV2 = ReplaceExactlyOnce(diskTarget, "ProbeDiskMember", "ProbeUnsavedMember");
            string targetV3 = ReplaceExactlyOnce(targetV2, "ProbeUnsavedMember", "ProbeIncrementalMember");
            string hashV1 = ComputeContentHash(diskTarget);
            string hashV2 = ComputeContentHash(targetV2);
            string hashV3 = ComputeContentHash(targetV3);
            bool initialObserved = false;
            bool fullImmediateStale = false;
            bool fullPostDiagnosticStale = false;
            bool incrementalImmediateStale = false;
            bool incrementalPostDiagnosticStale = false;

            checks.Add(new ProbeCheckResult(
                "StateTraceTargetHashesPrepared",
                true,
                $"v1={hashV1}; v2={hashV2}; v3={hashV3}"));

            RoslynLanguageServerProcessResult? processResult = null;
            await using (ProbeSession session = await context.StartSessionAsync(
                launchSpec,
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false))
            {
                checks.Add(new ProbeCheckResult(
                    "StateTraceServerStart",
                    !session.Process.HasExited,
                    $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}; command={session.Process.Identity.ServerCommandPath}"));

                (bool readinessObserved, double readinessMs) = await session.InitializeWorkspaceAsync(
                    context.Fixture.RootPath,
                    context.Fixture.SolutionPath,
                    explicitOpen: true,
                    cancellationToken).ConfigureAwait(false);
                checks.Add(new ProbeCheckResult(
                    "StateTraceProjectInitializationNotificationObserved",
                    readinessObserved,
                    readinessObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                    readinessMs));

                await session.Client.DidOpenAsync(context.Fixture.TargetPath, diskTarget, 1, cancellationToken).ConfigureAwait(false);
                await session.Client.DidOpenAsync(context.Fixture.ConsumerPath, consumerSnapshot.Text, 1, cancellationToken).ConfigureAwait(false);

                // diagnostic #1 + completion #1
                SemanticReadinessAttempt initialReadiness = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                    session,
                    context.Fixture.ConsumerPath,
                    consumerSnapshot.Position,
                    cancellationToken).ConfigureAwait(false);
                CompletionRequestResult initialCompletion = initialReadiness.Completion
                    ?? throw new InvalidOperationException("State trace initial semantic readiness did not produce completion evidence.");
                initialObserved = IsCurrentCompletion(
                    initialCompletion,
                    required: ["ProbeInstanceProperty", "ProbeDiskMember"],
                    forbidden: ["ProbeUnsavedMember", "ProbeIncrementalMember"]);
                checks.Add(new ProbeCheckResult(
                    "StateTraceInitialReadinessObserved",
                    initialObserved,
                    DescribeBehavior(initialCompletion)));

                // full didChange v2 + completion #2
                await session.Client.DidChangeFullAsync(
                    context.Fixture.TargetPath,
                    targetV2,
                    2,
                    cancellationToken).ConfigureAwait(false);
                CompletionRequestResult fullImmediate = await session.Client.CompletionAsync(
                    context.Fixture.ConsumerPath,
                    consumerSnapshot.Position,
                    cancellationToken).ConfigureAwait(false);
                fullImmediateStale = IsStaleV2(fullImmediate);
                AddObservation(checks, "StateTraceFullImmediateStalenessReproduced", fullImmediateStale, fullImmediate);

                // diagnostic #2 + completion #3
                SemanticReadinessAttempt fullPostDiagnosticAttempt = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                    session,
                    context.Fixture.ConsumerPath,
                    consumerSnapshot.Position,
                    cancellationToken).ConfigureAwait(false);
                CompletionRequestResult fullPostDiagnostic = fullPostDiagnosticAttempt.Completion
                    ?? throw new InvalidOperationException("State trace post-v2 diagnostic readiness did not produce completion evidence.");
                fullPostDiagnosticStale = IsStaleV2(fullPostDiagnostic);
                AddObservation(checks, "StateTraceFullPostDiagnosticStalenessReproduced", fullPostDiagnosticStale, fullPostDiagnostic);

                // incremental didChange v3 + completion #4
                LspRange incrementalRange = ProbeSourceMarker.FindUniqueTokenRange(targetV2, "ProbeUnsavedMember");
                await session.Client.DidChangeIncrementalAsync(
                    context.Fixture.TargetPath,
                    "ProbeIncrementalMember",
                    3,
                    incrementalRange,
                    "ProbeUnsavedMember".Length,
                    cancellationToken).ConfigureAwait(false);
                CompletionRequestResult incrementalImmediate = await session.Client.CompletionAsync(
                    context.Fixture.ConsumerPath,
                    consumerSnapshot.Position,
                    cancellationToken).ConfigureAwait(false);
                incrementalImmediateStale = IsStaleV3(incrementalImmediate);
                AddObservation(checks, "StateTraceIncrementalImmediateStalenessReproduced", incrementalImmediateStale, incrementalImmediate);

                // diagnostic #3 + completion #5
                SemanticReadinessAttempt incrementalPostDiagnosticAttempt = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                    session,
                    context.Fixture.ConsumerPath,
                    consumerSnapshot.Position,
                    cancellationToken).ConfigureAwait(false);
                CompletionRequestResult incrementalPostDiagnostic = incrementalPostDiagnosticAttempt.Completion
                    ?? throw new InvalidOperationException("State trace post-v3 diagnostic readiness did not produce completion evidence.");
                incrementalPostDiagnosticStale = IsStaleV3(incrementalPostDiagnostic);
                AddObservation(checks, "StateTraceIncrementalPostDiagnosticStalenessReproduced", incrementalPostDiagnosticStale, incrementalPostDiagnostic);

                bool diskAuthority = string.Equals(context.Fixture.ReadTarget(), diskTarget, StringComparison.Ordinal)
                    && string.Equals(context.Fixture.ReadConsumer(), diskConsumer, StringComparison.Ordinal);
                checks.Add(new ProbeCheckResult(
                    "StateTraceDiskAuthorityPreserved",
                    diskAuthority,
                    $"targetUnchanged={string.Equals(context.Fixture.ReadTarget(), diskTarget, StringComparison.Ordinal).ToString().ToLowerInvariant()}; consumerUnchanged={string.Equals(context.Fixture.ReadConsumer(), diskConsumer, StringComparison.Ordinal).ToString().ToLowerInvariant()}"));

                if (!session.Process.HasExited)
                {
                    await session.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                    await session.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
                }

                processResult = await session.GracefulRetireAsync().ConfigureAwait(false);
                checks.Add(new ProbeCheckResult(
                    "StateTraceProcessSurvived",
                    !processResult.ForcedKill && processResult.ExitCode is 0 or null,
                    $"hasExited={processResult.HasExited.ToString().ToLowerInvariant()}; exitCode={processResult.ExitCode?.ToString() ?? "<null>"}; forcedKill={processResult.ForcedKill.ToString().ToLowerInvariant()}"));
            }

            if (processResult is null)
                throw new InvalidOperationException("State trace process result was unavailable after retirement.");

            IReadOnlyList<RoslynStateTraceEvent> trace = RoslynStateTraceParser.Parse(processResult.CapturedStderr);
            bool baselineBehaviorReproduced = initialObserved
                && fullImmediateStale
                && fullPostDiagnosticStale
                && incrementalImmediateStale
                && incrementalPostDiagnosticStale;
            AddTraceEvidence(
                checks,
                trace,
                processResult.StderrTruncated,
                hashV1,
                hashV2,
                hashV3,
                baselineBehaviorReproduced);
            }).ConfigureAwait(false);
        }
        catch (ProbeServerSetupException exception)
        {
            return new ProbeScenarioResult(
                ScenarioName,
                ProbeScenarioStatus.Fail,
                0,
                [new ProbeCheckResult("StateTraceServerSetup", false, exception.Message)],
                "StateTraceServerSetup",
                exception.Message);
        }
    }

    private static void AddTraceEvidence(
        List<ProbeCheckResult> checks,
        IReadOnlyList<RoslynStateTraceEvent> trace,
        bool stderrTruncated,
        string hashV1,
        string hashV2,
        string hashV3,
        bool baselineBehaviorReproduced)
    {
        RoslynStateTraceEvent[] completionPre = trace.Where(e => e.Event == "completion.pre_freeze").ToArray();
        RoslynStateTraceEvent[] completionPost = trace.Where(e => e.Event == "completion.post_freeze").ToArray();
        RoslynStateTraceEvent[] diagnosticAfter = trace.Where(e => e.Event == "diagnostic.after").ToArray();
        bool captureComplete = !stderrTruncated
            && trace.Count > 0
            && completionPre.Length == 5
            && completionPost.Length == 5
            && diagnosticAfter.Length >= 3;
        checks.Add(new ProbeCheckResult(
            "StateTraceCaptureComplete",
            captureComplete,
            $"stderrTruncated={stderrTruncated.ToString().ToLowerInvariant()}; events={trace.Count}; completionPre={completionPre.Length}; completionPost={completionPost.Length}; diagnosticAfter={diagnosticAfter.Length}; max={ProbeConstants.MaxRoslynStateTraceEvents}"));

        bool deterministicPreFreezeEvidencePresent = completionPre.Length > 0
            && completionPre.All(static e => !string.IsNullOrWhiteSpace(e.SolutionChecksum)
                && !string.IsNullOrWhiteSpace(e.TargetFilePath)
                && !string.IsNullOrWhiteSpace(e.TargetTextHash)
                && e.TargetTextLength is >= 0);
        checks.Add(new ProbeCheckResult(
            "StateTracePreFreezeDeterministicEvidencePresent",
            deterministicPreFreezeEvidencePresent,
            deterministicPreFreezeEvidencePresent
                ? $"events={completionPre.Length}; solutionChecksum/targetFilePath/targetTextHash/targetTextLength present"
                : $"events={completionPre.Length}; one or more deterministic exact pre-freeze fields missing"));

        bool trackedV2 = trace.Any(e => IsTrackedUpdate(e) && e.Version == 2 && e.TargetHash == hashV2);
        bool trackedV3 = trace.Any(e => IsTrackedUpdate(e) && e.Version == 3 && e.TargetHash == hashV3);
        checks.Add(new ProbeCheckResult("StateTraceV2TrackedCurrentTextObserved", trackedV2, $"expectedHash={hashV2}"));
        checks.Add(new ProbeCheckResult("StateTraceV3TrackedCurrentTextObserved", trackedV3, $"expectedHash={hashV3}"));

        RoslynStateTraceEvent? v2ImmediatePre = ElementAtOrNull(completionPre, 1);
        RoslynStateTraceEvent? v2ImmediatePost = ElementAtOrNull(completionPost, 1);
        RoslynStateTraceEvent? v2PostDiagnosticPre = ElementAtOrNull(completionPre, 2);
        RoslynStateTraceEvent? v3ImmediatePre = ElementAtOrNull(completionPre, 3);
        RoslynStateTraceEvent? v3ImmediatePost = ElementAtOrNull(completionPost, 3);
        RoslynStateTraceEvent? v3PostDiagnosticPre = ElementAtOrNull(completionPre, 4);
        RoslynStateTraceEvent? v3PostDiagnosticPost = ElementAtOrNull(completionPost, 4);

        AddStateCheck(checks, "StateTraceV2CompletionPreFreezeTargetState", v2ImmediatePre, hashV1, hashV2, hashV3);
        AddStateCheck(checks, "StateTraceV2CompletionPostFreezeTargetState", v2ImmediatePost, hashV1, hashV2, hashV3);
        AddStateCheck(checks, "StateTraceV3CompletionPreFreezeTargetState", v3ImmediatePre, hashV1, hashV2, hashV3);
        AddStateCheck(checks, "StateTraceV3CompletionPostFreezeTargetState", v3ImmediatePost, hashV1, hashV2, hashV3);

        RoslynStateTraceEvent? diagnosticV2 = LastBefore(diagnosticAfter, v2PostDiagnosticPre?.Seq);
        RoslynStateTraceEvent? diagnosticV3 = LastBefore(diagnosticAfter, v3PostDiagnosticPre?.Seq);
        AddLineageCheck(checks, "StateTraceV2DiagnosticToCompletionLineage", diagnosticV2, v2PostDiagnosticPre);
        AddLineageCheck(checks, "StateTraceV3DiagnosticToCompletionLineage", diagnosticV3, v3PostDiagnosticPre);

        RoslynStateTraceEvent? pending = trace.FirstOrDefault(e => e.Event == "tracker.freeze_pending");
        checks.Add(new ProbeCheckResult(
            "StateTracePendingTranslationObserved",
            true,
            pending is null
                ? "observed=false"
                : $"observed=true; tracker={Format(pending.Tracker)}; pendingCount={Format(pending.PendingCount)}; firstActionKind={pending.FirstActionKind ?? "<unavailable>"}; oldTargetHash={pending.OldTargetHash ?? "<unavailable>"}; newTargetHash={pending.NewTargetHash ?? "<unavailable>"}"));

        RoslynStateTraceEvent? merge = trace.FirstOrDefault(e => e.Event == "translation.touch_merge");
        checks.Add(new ProbeCheckResult(
            "StateTraceTouchMergeObserved",
            true,
            merge is null
                ? "observed=false"
                : $"observed=true; oldTargetHash={merge.OldTargetHash ?? "<unavailable>"}; previousNewTargetHash={merge.PreviousNewTargetHash ?? "<unavailable>"}; finalNewTargetHash={merge.NewTargetHash ?? "<unavailable>"}"));

        checks.Add(new ProbeCheckResult(
            "StateTraceBaselineBehaviorReproduced",
            baselineBehaviorReproduced,
            $"reproduced={baselineBehaviorReproduced.ToString().ToLowerInvariant()}"));

        string interpretation = ClassifyTrace(
            hashV1,
            hashV3,
            v3PostDiagnosticPre,
            v3PostDiagnosticPost,
            diagnosticV3,
            baselineBehaviorReproduced);
        checks.Add(new ProbeCheckResult("StateTraceInterpretation", true, interpretation));
    }

    private static string ClassifyTrace(
        string hashV1,
        string hashV3,
        RoslynStateTraceEvent? completionPre,
        RoslynStateTraceEvent? completionPost,
        RoslynStateTraceEvent? diagnosticAfter,
        bool baselineBehaviorReproduced)
    {
        if (!baselineBehaviorReproduced)
            return "case=L6; instrumented build did not fully reproduce the verified 1.3.3 stale behavior; do not add delays or retries";

        if (completionPre is null || completionPost is null)
            return "case=<incomplete>; essential completion trace pair unavailable";

        if (completionPre.TargetHash == hashV3 && completionPost.TargetHash == hashV1)
        {
            bool lineageRecreated = diagnosticAfter is not null
                && diagnosticAfter.TrackerState == "Final"
                && diagnosticAfter.TargetHash == hashV3
                && completionPre.Tracker != diagnosticAfter.Tracker
                && completionPre.TrackerState == "InProgress";
            return lineageRecreated
                ? "case=L1+L4; frozen-partial rollback and cross-request lineage recreation directly observed"
                : "case=L1; frozen-partial rollback directly observed";
        }

        if (completionPre.TargetHash == hashV1)
            return "case=L2; stale Target is already present before frozen-partial completion";

        if (diagnosticAfter is not null
            && diagnosticAfter.Tracker == completionPre.Tracker
            && diagnosticAfter.TrackerState == "InProgress"
            && completionPre.TrackerState == "InProgress")
        {
            return "case=L3; diagnostic and next completion share an InProgress tracker";
        }

        if (diagnosticAfter is not null
            && diagnosticAfter.TrackerState == "Final"
            && diagnosticAfter.TargetHash == hashV3
            && diagnosticAfter.Tracker != completionPre.Tracker
            && completionPre.TrackerState == "InProgress")
        {
            return "case=L4; diagnostic Final/current lineage is replaced by a different InProgress completion lineage";
        }

        if (completionPre.TargetHash == hashV3 && completionPost.TargetHash == hashV3)
            return "case=L5-or-current-semantics; pre/post freeze retain v3; behavioral completion evidence must decide provider/result-cache hypothesis";

        return "case=L6-or-other; trace did not match L1-L5 state signatures; instrumentation perturbation or another lineage requires review";
    }

    private static void AddStateCheck(
        List<ProbeCheckResult> checks,
        string name,
        RoslynStateTraceEvent? traceEvent,
        string hashV1,
        string hashV2,
        string hashV3)
    {
        if (traceEvent is null)
        {
            checks.Add(new ProbeCheckResult(name, false, "traceEvent=<missing>"));
            return;
        }

        checks.Add(new ProbeCheckResult(
            name,
            true,
            $"targetHash={traceEvent.TargetHash ?? "<unavailable>"}; state={DescribeKnownHash(traceEvent.TargetHash, hashV1, hashV2, hashV3)}; solution={Format(traceEvent.Solution)}; tracker={Format(traceEvent.Tracker)}; trackerState={traceEvent.TrackerState ?? "<unavailable>"}; tryGetCompilation={Format(traceEvent.TryGetCompilation)}; compilation={Format(traceEvent.Compilation)}"));
    }

    private static void AddLineageCheck(
        List<ProbeCheckResult> checks,
        string name,
        RoslynStateTraceEvent? diagnostic,
        RoslynStateTraceEvent? completion)
    {
        bool available = diagnostic is not null && completion is not null;
        checks.Add(new ProbeCheckResult(
            name,
            available,
            available
                ? $"diagnosticSolution={Format(diagnostic!.Solution)}; diagnosticTracker={Format(diagnostic.Tracker)}; diagnosticTrackerState={diagnostic.TrackerState ?? "<unavailable>"}; diagnosticTargetHash={diagnostic.TargetHash ?? "<unavailable>"}; completionSolution={Format(completion!.Solution)}; completionTracker={Format(completion.Tracker)}; completionTrackerState={completion.TrackerState ?? "<unavailable>"}; completionTargetHash={completion.TargetHash ?? "<unavailable>"}; sameSolution={(diagnostic.Solution == completion.Solution).ToString().ToLowerInvariant()}; sameTracker={(diagnostic.Tracker == completion.Tracker).ToString().ToLowerInvariant()}"
                : "diagnostic/completion trace boundary unavailable"));
    }

    private static RoslynStateTraceEvent? LastBefore(IEnumerable<RoslynStateTraceEvent> events, long? seq) =>
        seq is null ? null : events.Where(e => e.Seq < seq.Value).LastOrDefault();

    private static RoslynStateTraceEvent? ElementAtOrNull(RoslynStateTraceEvent[] events, int index) =>
        index >= 0 && index < events.Length ? events[index] : null;

    private static bool IsTrackedUpdate(RoslynStateTraceEvent traceEvent) =>
        traceEvent.Event is "didchange.applied" or "lsp.tracked_updated";

    private static string DescribeKnownHash(string? value, string v1, string v2, string v3) => value switch
    {
        null => "<unavailable>",
        _ when value == v1 => "v1",
        _ when value == v2 => "v2",
        _ when value == v3 => "v3",
        _ => "other",
    };

    private static void AddObservation(
        List<ProbeCheckResult> checks,
        string name,
        bool reproduced,
        CompletionRequestResult completion)
    {
        checks.Add(new ProbeCheckResult(
            name,
            reproduced,
            $"reproduced={reproduced.ToString().ToLowerInvariant()}; {DescribeBehavior(completion)}",
            completion.DurationMs));
    }

    private static bool IsStaleV2(CompletionRequestResult completion) =>
        !ScenarioExecution.ContainsLabel(completion.Items, "ProbeUnsavedMember")
        && ScenarioExecution.ContainsLabel(completion.Items, "ProbeDiskMember");

    private static bool IsStaleV3(CompletionRequestResult completion) =>
        !ScenarioExecution.ContainsLabel(completion.Items, "ProbeIncrementalMember")
        && !ScenarioExecution.ContainsLabel(completion.Items, "ProbeUnsavedMember")
        && ScenarioExecution.ContainsLabel(completion.Items, "ProbeDiskMember");

    private static bool IsCurrentCompletion(
        CompletionRequestResult completion,
        IReadOnlyList<string> required,
        IReadOnlyList<string> forbidden) =>
        completion.Evidence.ResultKind is CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList
        && required.All(label => ScenarioExecution.ContainsLabel(completion.Items, label))
        && forbidden.All(label => !ScenarioExecution.ContainsLabel(completion.Items, label));

    private static string DescribeBehavior(CompletionRequestResult completion)
    {
        string[] labels = ["ProbeInstanceProperty", "ProbeDiskMember", "ProbeUnsavedMember", "ProbeIncrementalMember"];
        return ScenarioExecution.DescribeCompletionEvidence(completion)
            + "; "
            + string.Join("; ", labels.Select(label => $"{label}={ScenarioExecution.ContainsLabel(completion.Items, label).ToString().ToLowerInvariant()}"));
    }

    private static EditorSnapshot CreateEditorSnapshot(string diskConsumer)
    {
        int statementStart = diskConsumer.IndexOf(NaturalMemberAnchor, StringComparison.Ordinal);
        if (statementStart < 0 || diskConsumer.IndexOf(NaturalMemberAnchor, statementStart + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Natural Consumer member statement was not unique.");

        string editorConsumer = diskConsumer[..statementStart]
            + EditorMemberPrefix
            + diskConsumer[(statementStart + NaturalMemberAnchor.Length)..];
        int caretAbsoluteIndex = statementStart + EditorMemberPrefix.Length;
        if (caretAbsoluteIndex <= 0 || editorConsumer[caretAbsoluteIndex - 1] != '.')
            throw new InvalidOperationException("State trace Consumer caret was not immediately after the member-access dot.");
        if (caretAbsoluteIndex < editorConsumer.Length && editorConsumer[caretAbsoluteIndex] is not '\r' and not '\n')
            throw new InvalidOperationException("State trace Consumer caret had a right-hand identifier.");

        return new EditorSnapshot(editorConsumer, ProbeSourceMarker.PositionAt(editorConsumer, caretAbsoluteIndex));
    }

    private static string ReplaceExactlyOnce(string source, string oldValue, string newValue)
    {
        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected exactly one occurrence of {oldValue}.");
        return source[..index] + newValue + source[(index + oldValue.Length)..];
    }

    private static string ComputeContentHash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static string Format(object? value) => value?.ToString() ?? "<unavailable>";

    private sealed record EditorSnapshot(string Text, LspPosition Position);
}

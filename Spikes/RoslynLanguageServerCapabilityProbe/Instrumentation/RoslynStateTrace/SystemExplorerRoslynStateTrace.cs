// TEMPORARY DIAGNOSTIC-ONLY SOURCE FILE.
// Prepare-RoslynStateTrace.ps1 copies this file into an exact throwaway Roslyn checkout.
// It is intentionally excluded from the SystemExplorer probe compilation.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis;

/// <summary>
/// System Explorer diagnostic-only state-lineage tracer. The pinned Workspaces project already exposes internals
/// to the Features and LanguageServer assemblies used by the instrumentation points, so no IVT contract change or
/// public API promotion is required. It is never shipped as production SystemExplorer or Roslyn source.
/// </summary>
internal static class SystemExplorerRoslynStateTrace
{
    private const string Prefix = "SETRACE|";
    private const int MaxEvents = 256;
    private static readonly object s_writeGate = new();
    private static readonly bool s_enabled = string.Equals(
        Environment.GetEnvironmentVariable("SYSTEMEXPLORER_ROSLYN_STATE_TRACE"),
        "1",
        StringComparison.Ordinal);
    private static readonly string s_targetFileName =
        Environment.GetEnvironmentVariable("SYSTEMEXPLORER_ROSLYN_TRACE_TARGET") ?? string.Empty;
    private static long s_sequence;

    public static bool IsTargetIdentifier(string? identifier)
    {
        if (!s_enabled || string.IsNullOrWhiteSpace(s_targetFileName) || string.IsNullOrWhiteSpace(identifier))
            return false;

        return string.Equals(identifier, s_targetFileName, StringComparison.OrdinalIgnoreCase)
            || identifier.EndsWith("/" + s_targetFileName, StringComparison.OrdinalIgnoreCase)
            || identifier.EndsWith("\\" + s_targetFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static void TraceTrackedText(string eventName, string? identifier, int version, SourceText text)
    {
        try
        {
            if (!IsTargetIdentifier(identifier))
                return;

            Write(eventName,
                ("version", version.ToString()),
                ("targetHash", Hash(text)));
        }
        catch
        {
        }
    }

    public static void TraceSolution(
        string eventName,
        Solution solution,
        DocumentId? documentId = null,
        SourceText? trackedTargetText = null,
        Solution? workspaceSolution = null,
        string? forkKind = null,
        string? returnPath = null)
    {
        try
        {
            if (!s_enabled)
                return;

            var target = FindTarget(solution);
            SystemExplorerRoslynTrackerSnapshot tracker = target.ProjectId is null
                ? default
                : solution.CompilationState.GetSystemExplorerRoslynStateTraceSnapshot(target.ProjectId);

            Write(eventName,
                ("document", Id(documentId)),
                ("solution", Id(solution)),
                ("workspaceSolution", Id(workspaceSolution)),
                ("selectedSolution", Id(solution)),
                ("solutionStateContentVersion", Scalar(solution.SolutionStateContentVersion)),
                ("project", Id(target.ProjectId)),
                ("tracker", Scalar(tracker.TrackerIdentity)),
                ("trackerState", tracker.TrackerState),
                ("pendingCount", Scalar(tracker.PendingCount)),
                ("tryGetCompilation", Scalar(tracker.TryGetCompilation)),
                ("compilation", Scalar(tracker.CompilationIdentity)),
                ("targetHash", target.Hash),
                ("trackedTargetHash", trackedTargetText is null ? null : Hash(trackedTargetText)),
                ("forkKind", forkKind),
                ("returnPath", returnPath));
        }
        catch
        {
        }
    }

    public static void TraceSolutionSelection(
        string eventName,
        Solution workspaceSolution,
        Solution selectedSolution,
        SourceText? trackedTargetText,
        string forkKind,
        string returnPath)
    {
        try
        {
            if (!s_enabled)
                return;

            var target = FindTarget(selectedSolution);
            SystemExplorerRoslynTrackerSnapshot tracker = target.ProjectId is null
                ? default
                : selectedSolution.CompilationState.GetSystemExplorerRoslynStateTraceSnapshot(target.ProjectId);

            Write(eventName,
                ("solution", Id(selectedSolution)),
                ("workspaceSolution", Id(workspaceSolution)),
                ("selectedSolution", Id(selectedSolution)),
                ("solutionStateContentVersion", Scalar(selectedSolution.SolutionStateContentVersion)),
                ("project", Id(target.ProjectId)),
                ("tracker", Scalar(tracker.TrackerIdentity)),
                ("trackerState", tracker.TrackerState),
                ("pendingCount", Scalar(tracker.PendingCount)),
                ("tryGetCompilation", Scalar(tracker.TryGetCompilation)),
                ("compilation", Scalar(tracker.CompilationIdentity)),
                ("targetHash", target.Hash),
                ("trackedTargetHash", trackedTargetText is null ? null : Hash(trackedTargetText)),
                ("forkKind", forkKind),
                ("returnPath", returnPath));
        }
        catch
        {
        }
    }

    internal static void TraceTouchMerge(ProjectState oldProjectState, ProjectState previousNewProjectState, ProjectState finalNewProjectState)
    {
        try
        {
            if (!s_enabled)
                return;

            string? oldHash = HashTarget(oldProjectState);
            string? previousNewHash = HashTarget(previousNewProjectState);
            string? finalNewHash = HashTarget(finalNewProjectState);
            if (oldHash is null && previousNewHash is null && finalNewHash is null)
                return;

            Write("translation.touch_merge",
                ("oldTargetHash", oldHash),
                ("previousNewTargetHash", previousNewHash),
                ("newTargetHash", finalNewHash));
        }
        catch
        {
        }
    }

    internal static void TraceFreezePending(
        object tracker,
        int pendingCount,
        string firstActionKind,
        ProjectState oldProjectState,
        ProjectState latestProjectState)
    {
        try
        {
            if (!s_enabled || pendingCount <= 0)
                return;

            string? oldHash = HashTarget(oldProjectState);
            string? latestHash = HashTarget(latestProjectState);
            if (oldHash is null && latestHash is null)
                return;

            Write("tracker.freeze_pending",
                ("tracker", Id(tracker)),
                ("trackerState", "InProgress"),
                ("pendingCount", pendingCount.ToString()),
                ("firstActionKind", firstActionKind),
                ("oldTargetHash", oldHash),
                ("newTargetHash", latestHash));
        }
        catch
        {
        }
    }

    internal static string? HashTarget(ProjectState projectState)
    {
        try
        {
            foreach (var state in projectState.DocumentStates.States.Values)
            {
                if (!IsTargetIdentifier(state.FilePath) && !IsTargetIdentifier(state.Name))
                    continue;

                return state.TryGetText(out SourceText? text) ? Hash(text) : null;
            }
        }
        catch
        {
        }

        return null;
    }

    private static (ProjectId? ProjectId, string? Hash) FindTarget(Solution solution)
    {
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (!IsTargetIdentifier(document.FilePath) && !IsTargetIdentifier(document.Name))
                    continue;

                return (project.Id, document.TryGetText(out SourceText? text) ? Hash(text) : null);
            }
        }

        return default;
    }

    private static string Hash(SourceText text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

    private static void Write(string eventName, params (string Key, string? Value)[] fields)
    {
        if (!s_enabled)
            return;

        try
        {
            lock (s_writeGate)
            {
                long seq = Interlocked.Increment(ref s_sequence);
                if (seq > MaxEvents)
                    return;

                StringBuilder builder = new(Prefix)
                    .Append("seq=").Append(seq)
                    .Append("|event=").Append(Sanitize(eventName))
                    .Append("|pid=").Append(Environment.ProcessId)
                    .Append("|managedThreadId=").Append(Environment.CurrentManagedThreadId);

                foreach (var (key, value) in fields)
                {
                    builder.Append('|').Append(Sanitize(key)).Append('=').Append(Sanitize(value ?? "<unavailable>"));
                }

                Console.Error.WriteLine(builder.ToString());
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string value)
        => value.Replace("|", "_", StringComparison.Ordinal)
            .Replace("=", "_", StringComparison.Ordinal)
            .Replace("\r", "_", StringComparison.Ordinal)
            .Replace("\n", "_", StringComparison.Ordinal);

    private static string? Id(object? value) => value is null ? null : RuntimeHelpers.GetHashCode(value).ToString();
    private static string? Scalar<T>(T? value) where T : struct => value?.ToString();
    private static string Scalar(bool value) => value.ToString().ToLowerInvariant();
}

internal readonly record struct SystemExplorerRoslynTrackerSnapshot(
    int? TrackerIdentity,
    string? TrackerState,
    int? PendingCount,
    bool TryGetCompilation,
    int? CompilationIdentity);

internal sealed partial class SolutionCompilationState
{
    internal SystemExplorerRoslynTrackerSnapshot GetSystemExplorerRoslynStateTraceSnapshot(ProjectId projectId)
    {
        if (!TryGetCompilationTracker(projectId, out ICompilationTracker? tracker))
            return new(null, null, null, false, null);

        bool hasCompilation = tracker.TryGetCompilation(out Compilation? compilation);
        int? compilationIdentity = compilation is null ? null : RuntimeHelpers.GetHashCode(compilation);
        if (tracker is RegularCompilationTracker regular)
            return regular.GetSystemExplorerRoslynStateTraceSnapshot(hasCompilation, compilationIdentity);

        return new(RuntimeHelpers.GetHashCode(tracker), tracker.GetType().Name, null, hasCompilation, compilationIdentity);
    }

    private sealed partial class RegularCompilationTracker
    {
        internal SystemExplorerRoslynTrackerSnapshot GetSystemExplorerRoslynStateTraceSnapshot(
            bool hasCompilation,
            int? compilationIdentity)
        {
            CompilationTrackerState? state = ReadState();
            string? trackerState = state switch
            {
                null => null,
                InProgressState => "InProgress",
                FinalCompilationTrackerState => "Final",
                _ => state.GetType().Name,
            };
            int? pendingCount = state is InProgressState inProgress ? inProgress.PendingTranslationActions.Count : 0;
            return new(RuntimeHelpers.GetHashCode(this), trackerState, pendingCount, hasCompilation, compilationIdentity);
        }

        private void TraceSystemExplorerRoslynFreezePending(InProgressState state)
        {
            try
            {
                if (state.PendingTranslationActions.IsEmpty)
                    return;

                TranslationAction first = state.PendingTranslationActions.First();
                SystemExplorerRoslynStateTrace.TraceFreezePending(
                    this,
                    state.PendingTranslationActions.Count,
                    first.GetType().Name,
                    first.OldProjectState,
                    ProjectState);
            }
            catch
            {
            }
        }
    }
}

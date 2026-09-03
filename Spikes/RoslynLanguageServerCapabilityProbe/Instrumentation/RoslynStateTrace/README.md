# RoslynStateLineageTrace temporary upstream instrumentation

This directory contains **diagnostic-only tooling** for `RoslynStateLineageTrace`. It is not a Roslyn fork and it does not vendor any Roslyn source or build output into SystemExplorer.CodeService.

The only supported upstream base is:

- repository: `dotnet/roslyn`
- commit: `3aeb96c9ecc56a5ee483558f9e648e33e7bfe756`
- instrumentation schema/version: `1`
- target filename: `ProbeTarget.cs`

`Prepare-RoslynStateTrace.ps1` requires a **throwaway clean checkout** at exactly that commit and the external canonical SystemExplorer semantic-reuse patch (SHA-256 `11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be`). It verifies `git rev-parse HEAD` and an empty `git status --porcelain`, verifies the patch hash and `git apply --check` while pristine, validates every retained StateTrace source anchor in memory, runs the pinned repository-native restore path (`Restore.cmd` on Windows, `./build.sh --restore` on Unix) while the checkout is still clean, re-verifies commit/worktree state, applies the canonical SystemExplorer patch, confirms that patch did not alter the StateTrace anchor files, and only then writes `SystemExplorerRoslynStateTrace.cs` plus observational trace calls. It finally performs a targeted `Release --no-restore` build of the repository's existing `Microsoft.CodeAnalysis.LanguageServer.csproj`. The output wrapper/provenance directory must be outside the throwaway checkout. The script does not create a commit and does not change Roslyn package versions or target frameworks.

The generated output directory contains a small wrapper plus `provenance.json`. These are runtime artifacts and are deliberately excluded from the SystemExplorer release archive.

The wrapper only sets:

- `SYSTEMEXPLORER_ROSLYN_STATE_TRACE=1`
- `SYSTEMEXPLORER_ROSLYN_TRACE_TARGET=ProbeTarget.cs`

and forwards all arguments to the built server. The probe remains responsible for the normal `--stdio --logLevel Warning --telemetryLevel off` server arguments.

Trace lines are written only to stderr and start with `SETRACE|`. The helper caps output at 256 events, serializes whole-line writes under a lock, uses a monotonic `Interlocked` sequence, hashes exact UTF-8 source content with SHA-256, and never persists source text, diagnostics, completion payloads, syntax trees, or object dumps. All trace entry points swallow tracing failures so instrumentation cannot throw into Roslyn request paths.

The helper is intentionally observational: it uses already-present `Solution`, `ProjectState`, `SourceText`, `TryGetText`, pending tracker state and non-creating `TryGetCompilation`. It does not add semantic-materializing calls such as `GetCompilationAsync`, `GetSemanticModelAsync`, `GetSyntaxTreeAsync`, `WithFrozenPartialSemantics`, diagnostics, completion, workspace mutation, retries, sleeps, or delays.

Windows normal entrypoint is `Prepare-RoslynStateTrace.cmd`; `Prepare-RoslynStateTrace.ps1` is the implementation script. The `.cmd` child uses `-NoLogo -NoProfile -ExecutionPolicy Bypass` process-locally only and does not modify persistent execution policy.

Example preparation:

```bat
Prepare-RoslynStateTrace.cmd ^
  -RoslynRoot "C:\Temp\roslyn-state-trace-throwaway" ^
  -CanonicalSystemExplorerPatchPath "C:\Path To\Service.ThirdParty\ThirdParty\RoslynLanguageServer\patches\0001-Fix-semantic-model-reuse-after-cross-document-semant.patch" ^
  -OutputRoot "C:\Temp\roslyn-state-trace-output"
```

Then pass both generated absolute paths to probe 1.3.5 using `--state-trace-server` and `--state-trace-provenance`. Never point the trace scenario at a real Godot workspace; the scenario itself only runs against the controlled fixture.


`completion.pre_freeze` once again records deterministic authority from the **exact Solution used by
completion before `GetDocumentWithFrozenPartialSemantics(...)`**. Each event carries the exact solution
checksum plus target document file path, SHA-256 text hash, and text length from that same snapshot.
Process-local solution/tracker/compilation identities remain supplementary lineage diagnostics; they are
not substitutes for deterministic pre-freeze authority. The probe emits
`StateTracePreFreezeDeterministicEvidencePresent` to check these fields without changing the retained
stale-behavior request ordering or ordinary suitability classification.

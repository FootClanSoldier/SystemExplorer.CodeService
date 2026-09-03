# Roslyn Language Server capability probe

`RoslynLanguageServerCapabilityProbe` is a **non-production** executable used only to evaluate whether Microsoft's external `roslyn-language-server` is a suitable semantic-engine candidate for a later SystemExplorer.CodeService Roslyn phase.

It is deliberately isolated from the production CodeService:

- the production `SystemExplorer.CodeService.csproj` does not reference Roslyn or StreamJsonRpc;
- the production `SystemExplorer.CodeService.slnx` does not include this project;
- the probe does not reference the production CodeService project;
- no CodeService protocol, workspace, index, cache, workload lane, or Godot plugin behavior is changed by this spike.

## Pinned dependencies

The probe is designed for exactly:

- `roslyn-language-server` **5.12.0-1.26426.8**
- `StreamJsonRpc` **2.25.29**
- target framework `net10.0`
- probe version **1.3.6** / ordinary report schema **3** / semantic-origin-only report schema **1**
- optional Roslyn state-lineage instrumentation base: `dotnet/roslyn` commit `3aeb96c9ecc56a5ee483558f9e648e33e7bfe756`

Do not replace the Roslyn tool version with `latest`, a wildcard, or another prerelease version when collecting evidence for this spike. If the exact tool package is unavailable, stop the runtime verification rather than silently substituting a different Roslyn Language Server build.

## Private tool installation only

Do not install, uninstall, or reuse a global `roslyn-language-server`. Install the exact version into a private tool directory and pass the installed tool **command path** to the probe explicitly through `--server`.

Windows example (paths with spaces are intentional and useful for validation):

```powershell
$ProbeRoot = "C:\Temp\System Explorer Roslyn Probe"

dotnet tool install roslyn-language-server `
  --version 5.12.0-1.26426.8 `
  --tool-path "$ProbeRoot\tools"
```

Verify the private package inventory:

```powershell
dotnet tool list --tool-path "$ProbeRoot\tools" roslyn-language-server
```

The required matching row is conceptually:

```text
roslyn-language-server   5.12.0-1.26426.8   roslyn-language-server
```

The currently observed Windows .NET Tool installation publishes the top-level command as:

```text
C:\Temp\System Explorer Roslyn Probe\tools\roslyn-language-server.cmd
```

Use that command path with the probe:

```text
--server "C:\Temp\System Explorer Roslyn Probe\tools\roslyn-language-server.cmd"
```

A future SDK/tool installation that publishes `roslyn-language-server.exe` at the same private tool root is also accepted on Windows, but the probe does not depend on internal `.store` layout to find or launch such a payload.

macOS/Linux example:

```bash
dotnet tool install roslyn-language-server \
  --version 5.12.0-1.26426.8 \
  --tool-path "/tmp/System Explorer Roslyn Probe/tools"

dotnet tool list --tool-path "/tmp/System Explorer Roslyn Probe/tools" roslyn-language-server
```

Then use:

```text
--server "/tmp/System Explorer Roslyn Probe/tools/roslyn-language-server"
```

The private tool payload is test infrastructure and must not be committed or included in the delivery zip.

## Exact private-tool provenance preflight

`--server` means the absolute path to the installed private `roslyn-language-server` command/shim. It does **not** mean an internal Roslyn payload executable.

Before any fixture is created or any LSP capability generation is started, the probe:

```text
--server command path
  -> validates absolute existing expected command filename
  -> derives the containing private tool path
  -> runs bounded:
       dotnet tool list --tool-path <tool-path> roslyn-language-server
  -> requires exactly one matching package row
  -> requires package id roslyn-language-server
  -> requires version 5.12.0-1.26426.8 exactly
  -> requires published command roslyn-language-server
  -> creates a platform-aware launch specification for that same command
```

The inventory process uses redirected stdout/stderr, concurrent bounded drains, a 10 second deadline, and forced process-tree retirement if necessary. Exit code 0 alone is not evidence: the parser must find one actual matching package row. The parser does not depend on English table headers.

The package inventory is the version/provenance authority. The command filename alone is not version authority. The probe does not infer the version from directory names, internal `.store` paths, NuGet cache layout, web lookup, a global tool, or a hard-coded `ActualVersion`.

The installed Roslyn tool does not expose `roslyn-language-server --version` as a supported version contract for this build; the probe therefore does not invoke or parse `--version` at runtime.

`DOTNET_HOST_PATH` is preferred for the inventory command only when it names an existing fully-qualified file. Otherwise the inventory uses the `dotnet` CLI command. This is not semantic-tool PATH resolution: the semantic tool itself is always the exact private command supplied through `--server`.

## Controlled fixture restore precondition

After exact private-tool provenance has been verified, the controlled synthetic fixture is prepared before any Roslyn Language Server generation can start:

```text
create temporary net10.0 solution/project/source
  -> bounded owned dotnet restore of ProbeFixture/ProbeFixture.csproj
       --disable-build-servers --nologo --verbosity minimal
  -> require restore exit code 0
  -> require non-empty ProbeFixture/obj/project.assets.json
  -> retire restore stdout/stderr drains and the owned restore process
  -> create ProbeScenarioContext
  -> only then start Roslyn generation 1
```

The restore target is exactly the generated `fixture.ProjectPath`. It is fixture preparation only; it is not production restore architecture and it is never applied to a user-supplied `--solution`, `--project`, or real workspace. Real-workspace mode remains read-only from the probe's preparation perspective.

The restore process is shell-free: `UseShellExecute = false`, stdout/stderr are redirected, and the command is built with `ProcessStartInfo.ArgumentList`. `DOTNET_HOST_PATH` is used only when it is non-empty, fully qualified, and names an existing file; otherwise the host command is `dotnet`, matching the private-tool inventory policy without refactoring the already verified verifier. Restore has its own 120 second timeout and a 256 KiB per-stream capture bound. It does not reuse the shorter tool-inventory timeout.

Cancellation or timeout does not release ownership merely because cancellation was requested. The probe first retires the owned restore process tree (using `Kill(entireProcessTree: true)` with the existing platform fallback when forced retirement is needed), waits boundedly for actual root-process terminality, retires both output drains, and only then propagates cancellation or reports setup failure. Output truncation, non-zero exit, missing assets, or an empty assets file fail closed as **probe infrastructure / fixture setup failure**, not `UnsuitableCandidate` evidence about Roslyn.

Restore happens exactly once per temporary fixture run. Explicit solution open, recovery, stale-version observation, and the auto-load comparison all reuse that same prepared fixture. `--keep-artifacts` therefore also retains the generated `obj/project.assets.json`; normal fixture disposal removes it with the temporary root.

Restore latency is setup/preparation latency and is not included in `SemanticReadyMs`. `SemanticReadyMs` now starts immediately before primary workspace initialization and ends only when fixture semantic readiness is actually established. It therefore includes workspace initialization, Target/Consumer `didOpen`, the cold completion observation, any same-generation `SemanticGateDisambiguation` controls, and—when required—the awaited diagnostic readiness operation plus the immediately following successful true-editor completion.

No Roslyn workspace/configuration value is changed by this preparation step. In particular, the existing configuration callback behavior (including JSON null for `projects.dotnet_enable_automatic_restore`) and the existing completion request shape remain unchanged.

## Thin-client launch boundary

The installed `roslyn-language-server` package is a lightweight editor-facing thin client. It bundles `Microsoft.CodeAnalysis.LanguageServer` and launches that language server on demand.

This probe deliberately uses the official top-level `roslyn-language-server` command as its runtime launch boundary:

```text
probe
  -> installed roslyn-language-server command
      -> Roslyn thin client
          -> dedicated Microsoft.CodeAnalysis.LanguageServer child
              -> possible BuildHost descendants
```

The probe does **not** scan `.store` for a runtime executable, does **not** load package assemblies into the probe process, and does **not** directly launch `Microsoft.CodeAnalysis.LanguageServer`.

Daemon mode is intentionally not used. In non-daemon mode the thin client starts a dedicated language-server child for the client. With `--stdio`, the thin client relays LSP traffic between its own stdio and the underlying language server. This dedicated descendant model is why one probe server generation owns the complete launched process tree.

The Roslyn arguments remain:

```text
--stdio --logLevel Warning --telemetryLevel off
```

The separate auto-load comparison generation additionally uses:

```text
--autoLoadProjects
```

No `--daemon-mode` argument is added.

## Platform-aware launch

For a directly startable command (Windows `.exe` or the Unix `roslyn-language-server` command), the probe uses:

```text
FileName = ServerCommandPath
UseShellExecute = false
redirect stdin/stdout/stderr
ArgumentList = Roslyn arguments
```

For a Windows `.cmd` command, redirected stdio with `UseShellExecute = false` requires an explicit Windows command interpreter. The launch specification resolves an existing absolute `COMSPEC` first, then a verified system `cmd.exe` location. `ProcessStartInfo.FileName` is that actual `cmd.exe`/`COMSPEC` path, while the complete nested command-interpreter invocation is supplied as one raw `ProcessStartInfo.Arguments` string:

```text
/d /s /c ""C:\Temp\System Explorer Roslyn Probe\tools\roslyn-language-server.cmd" --stdio --logLevel Warning --telemetryLevel off"
```

This is logically the same as:

```text
cmd.exe /d /s /c ""C:\Temp\System Explorer Roslyn Probe\tools\roslyn-language-server.cmd" --stdio --logLevel Warning --telemetryLevel off"
```

The Windows shim does **not** place `/d`, `/s`, `/c`, or the already cmd-quoted nested command into `ProcessStartInfo.ArgumentList`. `cmd.exe` owns a second command-line parsing layer, so the `/c` nested quote structure must reach the command interpreter intact rather than first being encoded as a separate argv element by .NET. One central fail-closed helper owns the complete raw `/d /s /c ...` argument string. Paths with spaces are supported. Shell-sensitive command-path sequences that the helper does not intentionally support are rejected during setup rather than interpolated unsafely. Workspace/source paths never enter this shell command; workspace paths continue to travel later through LSP. Direct semantic command launches and private `dotnet tool list` inventory continue to use `ArgumentList` because they do not cross this nested `cmd.exe /c` parsing boundary.

On Windows `.cmd` runs, `cmd.exe`/`COMSPEC` is the **owned OS launch root**, not the semantic engine. Report identity therefore keeps these concepts separate:

```text
ServerCommandPath        = ...\roslyn-language-server.cmd
LauncherExecutablePath   = actual cmd.exe / COMSPEC process path
LaunchKind               = WindowsCommandShim
```

For direct launch:

```text
ServerCommandPath        = .../roslyn-language-server
LauncherExecutablePath   = actual directly launched command path
LaunchKind               = DirectCommand
```

## Transport and callbacks

StreamJsonRpc owns JSON-RPC request IDs, concurrency bookkeeping, JSON serialization, and LSP `Content-Length` framing through `HeaderDelimitedMessageHandler` over:

- owned launch-root stdin for outgoing LSP traffic;
- owned launch-root stdout for incoming LSP traffic.

With a Windows `.cmd` launch these handles flow through `cmd.exe` to the tool command and thin client; the thin client then relays stdio to its dedicated language-server child.

The probe never reads stdout in parallel as a text log. stderr is drained independently into a bounded 256 KiB tail capture and continues to be drained after the capture bound is reached.

The client implements the callbacks needed by the exercised Roslyn flows, including:

- `workspace/configuration` — returns JSON `null` for every setting that the probe has not explicitly defined;
- `client/registerCapability` / `client/unregisterCapability` — captures dynamic registrations;
- `window/logMessage` / `window/showMessage` / `window/showMessageRequest`;
- `window/workDoneProgress/create` / `$/progress`;
- `workspace/diagnostic/refresh`;
- `workspace/projectInitializationComplete`;
- `textDocument/publishDiagnostics`.

The initialize request intentionally advertises only a minimal capability set that the probe can handle. It does not imitate the VS Code C# client configuration. StreamJsonRpc warnings for server requests with no matching local target are captured into bounded `UnsupportedServerRequests` observations so missing callback coverage is visible rather than silently ignored. The already-bounded callback message stream (`window/logMessage`, `window/showMessage`, progress, and handled message requests) is also exposed read-only so a failed controlled semantic gate can include a bounded recent-message summary without promoting the full stream into the top-level report schema. Message presence is observational and is not itself a failure condition.

The project-load contracts exercised by the spike remain:

```text
solution/open
    params: { "solution": "file:///.../Project.sln[x]" }

project/open
    params: { "projects": ["file:///.../Project.csproj"] }

workspace/projectInitializationComplete
    server notification used as explicit project-initialization evidence
```

The primary fixture and real-workspace paths use explicit `solution/open` or `project/open`. `--autoLoadProjects` is a separate comparison scenario and is not allowed to hide explicit-load failures.

## Fixture scenarios

With only `--server`, the probe builds a temporary standalone `net10.0` C# solution under the system temporary directory, restores that generated project exactly once using the bounded preparation step above, verifies non-empty `obj/project.assets.json`, and only then runs deterministic scenarios sequentially:

1. explicit server start, LSP initialize/initialized, explicit solution open, and `workspace/projectInitializationComplete` readiness observation;
2. `ProbeTarget.cs` version 1 from exact disk source followed by `ProbeConsumer.cs` version 1 from the primary in-memory true-editor snapshot `return target.|`, while the Consumer disk source remains `return target.ProbeExtension();`;
3. the required diagnostic-first `SemanticReadiness` gate at the stored true-editor caret; on PASS, `SemanticGateDisambiguation` is skipped and the baseline/warm completion plus instance/static/private/protected/generic/extension checks run; on FAIL, same-generation `SemanticGateDisambiguation` may run afterward as failure-only evidence;
4. full-document `didChange` of Target to version 2 changing `ProbeDiskMember` to `ProbeUnsavedMember` without a disk write, followed first by the required immediate Consumer completion and, only when that immediate update is not observed while the process remains live, a same-generation diagnostic semantic re-readiness operation plus an identical Consumer completion;
5. incremental range `didChange` of Target to version 3 changing it to `ProbeIncrementalMember`, again followed first by the required immediate Consumer completion and then, only on immediate failure with a live process, the same controlled diagnostic semantic re-readiness experiment;
6. definition and references;
7. protocol-aware pull or publish diagnostics using its own separately opened diagnostics document and an unsaved semantic error, followed by clearing and closing it;
8. `prepareRename` when supported and `rename`, verifying the returned `WorkspaceEdit` without applying it;
9. forced server-generation process-tree death while the semantic Target/Consumer documents and JSON-RPC/stdin/stdout are still live, exact old-generation root retirement, fresh generation initialization/project load, Target unsaved text/version replay plus replay of the same Consumer version-1 true-editor snapshot, explicit diagnostic semantic-readiness establishment, and immediate post-restart true-editor completion verification against the replayed unsaved member;
10. an optional isolated stale-version observation (`--stale-version-experiment`).

Fixture `textDocument/*` semantic requests are issued only after the request document has been opened in that active server generation. Open-document state is generation-local: after recovery retires the old generation, Target is reopened with the current unsaved text/version while Consumer is replayed from the exact stored version-1 true-editor snapshot before the post-restart completion request. The Consumer snapshot remains off disk. The auto-load comparison intentionally retains its legacy disk-Consumer marker behavior for comparison only, and closes Consumer then Target before normal retirement.

Controlled fixture marker comments have two intentionally different position semantics. Completion markers identify an editor-caret location and resolve to the **start of the marker comment**, which is fail-closed verified to be immediately after a member-access dot. For example:

```text
target.|/*PROBE_INSTANCE_COMPLETION*/ProbeInstanceProperty
```

Definition/reference/rename markers retain the existing symbol-target semantics and resolve to the **end of the marker comment**, immediately before the existing identifier:

```text
target./*PROBE_DEFINITION*/|ProbeDefinitionSymbol
```

The disk source fixture is not rewritten. The required primary cross-document instance-completion authority is now the in-memory version-1 Consumer snapshot whose unique disk statement `return target.ProbeExtension();` is replaced only in memory by `return target.` with the caret immediately after the dot. `PROBE_INSTANCE_COMPLETION` is retained in the fixture for legacy/diagnostic comparability but is no longer the required cross-document instance-completion authority. Static/private/derived/generic completion markers and definition/reference/rename markers remain where their semantics are still appropriate. Completion still sends `TextDocumentPositionParams` without an explicit LSP completion context. This probe version does not add `contextSupport`, `CompletionContext`, `triggerKind`, or `triggerCharacter`, and it does not change completion normalization, client capabilities, `workspace/configuration`, or timing. Failure-branch diagnostic generations remain available and leave the disk fixture unchanged.

Each completion request records bounded response-shape evidence before item normalization. The evidence distinguishes JSON `null`, an explicit completion-item array, a `CompletionList` object with an `items` array, undefined/default response state, and unexpected object/value shapes. It records the raw response array length and optional boolean `isIncomplete`, while normalized persisted items remain capped by `MaxCompletionItems`. The full completion JSON payload and arbitrary completion-label sets are not persisted; semantic-gate details continue to keep only a deterministic, bounded set of at most 32 ordinal-distinct/sorted `Probe*` labels.

The explicit semantic gate also records `FixtureRestoreVerified` and a required `NoUnresolvedDependencyWarning` check against the existing bounded server-message capture. The auto-load comparison records `AutoLoadFixtureRestoreReused` and `AutoLoadNoUnresolvedDependencyWarning`. The known Roslyn message fragment `has unresolved dependencies` is matched case-insensitively. Existing `ServerMessagesObserved` / `AutoLoadServerMessagesObserved` failure evidence is retained so other project-load or build-host messages remain visible if completion still fails.

`ProjectInitializationComplete` and fixture `SemanticReadiness` are intentionally separate states. Probe 1.3.0 established an important negative/positive differential: the primary diagnostic pull completed but completion at the synthetic mid-token marker remained `Null`, while the fresh true-editor diagnostic control completed the same diagnostic operation and immediately returned `CompletionList` at `return target.|` with exact `ProbeInstanceProperty`. Therefore the diagnostic semantic operation remains causal for the true-editor completion shape, while the failed 1.3.0 promotion showed that the synthetic mid-token completion marker is not a valid semantic-readiness authority.

The controlled primary generation now models a real editor buffer from first open:

```text
disk Consumer:
    return target.ProbeExtension();

open Consumer v1:
    return target.|

disk remains unchanged
```

`InitializationScenario` constructs that snapshot only in memory by requiring exactly one ordinal occurrence of the complete disk statement and replacing only that occurrence with `return target.`. The snapshot fails closed unless it differs from disk, the caret is after `.`, newline/EOF is immediately to the right, no right-hand identifier or semicolon remains at the caret, and the disk source is still byte-for-byte the original Consumer. The actual open-buffer state is retained only at runtime as `CurrentConsumerText`, `CurrentConsumerVersion`, and `CurrentConsumerCompletionPosition` so downstream scenarios do not reconstruct the primary caret from disk.

`ExplicitSolutionOpen` now owns only lifecycle/setup and the true-editor buffer boundary:

```text
initialize
solution/open
workspace/projectInitializationComplete
Target didOpen v1 from disk
Consumer didOpen v1 from in-memory true-editor snapshot
    return target.|
verify Consumer disk authority
NO semantic request
```

`ProjectInitializationComplete` is therefore explicitly **not** `SemanticReadiness`.
`PrimarySemanticReadinessStartTimestamp` still starts before workspace initialization, so
`FixtureSemanticReadyMs` includes workspace initialization, project load, `didOpen`, the
diagnostic readiness operation, and the first successful completion, but not fixture restore.

The verified 1.3.1 runtime evidence motivating this change was order-sensitive:

```text
primary history:
didOpen
-> cold completion Null
-> completion/definition/completion
-> diagnostic completed
-> completion Null

fresh history:
didOpen
-> diagnostic completed
-> completion CompletionList
-> exact ProbeInstanceProperty present
```

The semantic operation is capable of establishing working cross-document completion, but
request ordering/history materially affects whether it does so. This does **not** establish
that an earlier completion permanently "poisons" Roslyn.

The primary order promoted in 1.3.2 remains:

```text
initialize
solution/open
workspace/projectInitializationComplete

Target didOpen v1
Consumer didOpen v1:
    return target.|

SemanticReadiness
    -> diagnostic capability observation
    -> await existing no-identifier Consumer PullDiagnosticsAsync
    -> immediately CompletionAsync at the same stored return target.| position
    -> require Array/CompletionList
    -> require exact ProbeInstanceProperty

PASS:
    -> SemanticGateDisambiguation SKIPPED
    -> Completion
    -> DocumentSynchronization
    -> Navigation
    -> Diagnostics
    -> Rename
    -> Recovery

FAIL, if the primary generation is still alive:
    -> SemanticGateDisambiguation
       completion -> definition -> completion
       as failure evidence after failed readiness
```

No Consumer completion or definition request occurs before the diagnostic pull that attempts
to establish primary semantic readiness. `PrimaryCompletionEvidence` is now the diagnostic-first
readiness completion evidence, never a cold initialization completion.

### Verified probe 1.3.2 semantic-readiness result

The controlled 1.3.2 fixture run verified the promoted primary boundary:

```text
ExplicitSolutionOpen PASS

SemanticReadiness PASS
    diagnostic pull
    -> completion at return target.|
    -> CompletionList
    -> exact ProbeInstanceProperty present

SemanticGateDisambiguation SKIPPED
```

The same run also verified ordinary cross-document completion of `ProbeInstanceProperty`,
`ProbeExtension`, and `ProbeBasePublic` after readiness. This establishes that initial
project-wide cross-document completion works under the diagnostic-first primary semantic-readiness
ordering. The separate exact `GenericMethodDiscoverable` and rename `newText` failures are not
changed by probe 1.3.3.

The 1.3.2 document-synchronization differential was:

```text
initial semantic-ready state:
    ProbeDiskMember visible

Target full didChange v2:
    ProbeDiskMember -> ProbeUnsavedMember
immediate Consumer completion:
    ProbeUnsavedMember NOT observed

Target incremental didChange v3:
    ProbeUnsavedMember -> ProbeIncrementalMember
immediate Consumer completion:
    ProbeIncrementalMember NOT observed
```

Recovery then supplied an important control: a fresh Roslyn generation, replay of Target v3 and
Consumer v1, and diagnostic semantic readiness produced completion containing
`ProbeIncrementalMember` while excluding `ProbeDiskMember`. The unsaved Target v3 source therefore
exists correctly in probe-owned in-memory document state and is semantically understandable by
Roslyn once readiness is re-established.

Primary `SemanticReadiness` admission requires the observed project-initialization boundary,
Target/Consumer version 1, the verified stored Consumer true-editor snapshot, unchanged Consumer
disk authority, and a live primary process. It does not depend on `SemanticGateDisambiguation`.
The operation then refreshes dynamic capability observation, requires a static diagnostic provider
or active dynamic `textDocument/diagnostic` registration, awaits the existing no-identifier
Consumer diagnostic pull, and immediately sends the completion request with no intervening Roslyn
operation.

Readiness promotion requires all of the following: diagnostic capability is available, the pull
completes, completion returns `Array`/`CompletionList`, exact `ProbeInstanceProperty` is present,
the process remains alive, the true-editor snapshot remains valid, Consumer disk authority remains
intact, and the readiness timestamp is valid. Only then are `FixtureSemanticRequestSucceeded` and
`FixtureSemanticReadyMs` promoted, with `SemanticReadinessEstablished` reporting
`source=precompletion-diagnostic-pull`.

The diagnostic operation uses the existing `PullDiagnosticsAsync` unchanged: `identifier` and
`previousResultId` remain omitted. Zero returned diagnostics is still a successful completed
operation. Persisted readiness evidence is bounded to diagnostic count, at most 32
ordinal-distinct/sorted codes, request duration, completion response shape, and bounded `Probe*`
labels. Diagnostic messages, raw diagnostic payloads, source text, and JSON are not persisted.
There is no delay, callback wait, `didChange`, reopen, duplicate `didOpen`, configuration change,
completion context, capability-advertisement change, or dynamic-registration implementation in
this policy. Pre-completion diagnostic pull is a **probe candidate semantic-readiness policy**
derived from experimentally observed Roslyn behavior; it is not an LSP requirement.

The permanent detailed Microsoft-client / Roslyn Language Server parity research, source
provenance, frozen-partial-semantics analysis, diagnostic lifecycle findings, project-context
analysis, and external Source Ledger are maintained in:

`docs/RoslynLanguageServer_ClientIntegration.md`

That document is research/reference context and does not assert what the current CodeService source
implements.

On readiness success the same live generation continues into `Completion`, `DocumentSynchronization`, `Navigation`, `Diagnostics`, `Rename`, and `Recovery`. `Completion` uses the stored true-editor position for its required cross-document instance check and warm repeat; static, same-type/private, derived/protected, and generic controls retain their existing marker positions. `DocumentSynchronization` uses the same stored Consumer caret for baseline, immediate post-full-Target-`didChange`, and immediate post-incremental-Target-`didChange` completion. Probe 1.3.3 preserves those immediate completions as the required authority and, only when an immediate update is not observed while the primary process is still live, invokes the existing diagnostic semantic-readiness operation in that same generation and immediately repeats the identical Consumer completion. Consumer remains version 1 and receives no `didChange` before Recovery.

Navigation and Rename calculate Consumer marker positions from `CurrentConsumerText`, the actual open editor buffer, rather than re-reading Consumer from disk. Their request order and assertions are otherwise unchanged.

Recovery first proves the primary generation still answers at the same true-editor caret with exact `ProbeIncrementalMember`, then kills/retires that generation. The fresh generation replays the current unsaved Target text/version and the exact stored Consumer version-1 true-editor snapshot, refreshes diagnostic capability state, awaits Consumer diagnostic pull, and immediately completes at the same stored caret. Recovery still requires exact `ProbeIncrementalMember` present and `ProbeDiskMember` absent, and separately verifies that both the unsaved Target state and the Consumer editor snapshot remained off disk.

This is a **probe candidate readiness policy**, not a permanent production design and not evidence that diagnostics are an LSP prerequisite for completion. Dynamic diagnostic registration/provider lifecycle remains a separate deferred contract-hardening problem.

The fresh `TrueEditorBufferCompletionDisambiguation` generation is admitted only when primary evidence is exactly:

```text
primary readiness completion = Null
post-readiness pre-definition true-editor completion = Null
definition locations > 0
definition expectedTargetMatched = true
post-definition true-editor completion = Null
```

If fixture semantic readiness was already established, primary disambiguation evidence is missing, the definition control did not establish the expected Target symbol, or post-definition completion changed shape, the scenario is explicitly skipped with a bounded reason instead of guessing from scenario status. The generation runs only after the primary generation is terminal and the AutoLoad comparison has fully retired, and it retires before fixture process/scenario snapshotting and before `RealGodotWorkspace`. No Roslyn generations overlap.

The restored disk fixture remains exactly:

```text
return target.ProbeExtension();
```

The primary logical caret is already:

```text
return target.|
```

The retained fresh diagnostic generation independently creates the same fail-closed in-memory Consumer snapshot by replacing the unique complete statement with:

```text
return target.|
```

The caret must be immediately after `.` and the next source character must be a line break or end-of-source. There is no marker comment, identifier token, or semicolon to the right of the caret. `ProbeTarget.cs` is still read from disk unchanged. Target is opened once at version 1 from disk; Consumer is opened once at version 1 from the in-memory editor snapshot. There is no `didChange`, reopen, reload, additional restore, source write, delay, completion-context change, capability change, or configuration change. The probe verifies the disk Consumer before opening the snapshot and again after semantic requests.

The true-editor generation uses the existing `RoslynLspClient.CompletionAsync` unchanged, then compares its bounded response shape observationally with the primary generation's same-caret `PrimarySemanticGateDisambiguationEvidence.PreDefinitionNaturalCompletionEvidence`. It separately requires a non-null `Array`/`CompletionList` shape and exact `ProbeInstanceProperty`. After completion it runs the same exact-target definition control from the modified open Consumer against the unchanged Target and requires at least one location plus the expected Target line. It then verifies disk authority, process survival, protocol coverage, closes Consumer then Target when the process is live, and gracefully retires the generation. The comparison check always passes because it is evidence; the completion/member/definition/disk/process checks remain truthful assertions.

After snapshot verification, completion, exact-target definition, and disk-authority verification have all produced observations, the true-editor scenario publishes only bounded scalar `TrueEditorBufferCompletionEvidence` into `ProbeScenarioContext`: completion response shape, exact `ProbeInstanceProperty` presence, definition count, expected-target match, snapshot verification, and disk unchanged state. It never persists completion items, source text, locations, or another unbounded payload. If the scenario faults before those observations are available, the evidence remains absent.

If that bounded evidence establishes the retained fail-closed branch -- primary diagnostic-first readiness completion `Null`, post-readiness SemanticGate pre/post completion both `Null`, primary exact-target definition PASS, verified fresh cross-document true-editor snapshot, fresh cross-document true-editor completion `Null`, exact-target true-editor definition PASS, and unchanged disk -- the runner continues through the existing failure-only fresh controls only after each previous generation is terminal:

```text
primary diagnostic-first readiness completion = Null
    ->
same-generation post-readiness failure evidence:
    true-editor completion
    definition
    same true-editor completion
    ->
AutoLoad comparison
    ->
fresh true-editor cross-document generation:
    return target.|
    ->
if still Null + exact definition PASS:
    ->
fresh same-document generation:
    Target:
        _ = this.|
    expected:
        ProbePrivateField
```

`SameDocumentCompletionDisambiguation` reuses the already-restored fixture and makes no disk source change. Its unique source anchor is the existing Target statement `_ = this./*PROBE_PRIVATE_COMPLETION*/ProbePrivateField;`. A scenario-local exact ordinal replacement removes that complete statement body only from the version-1 Target `didOpen` snapshot, yielding `_ = this.|` with newline/EOF immediately after the dot: no marker, right-hand identifier, or semicolon is present at the caret. Consumer is opened once at version 1 from exact disk text. There is no `didChange`, reopen, additional restore, source write, delay, `CompletionContext`, `contextSupport`, capability change, configuration change, or completion-normalization change.

The same-document generation deliberately runs ordinary `CompletionAsync(TargetPath, position, ...)` **before** its definition control. A non-null `Array`/`CompletionList` response is necessary but not sufficient: the capability assertion requires the exact label `ProbePrivateField`. This is a strong same-document/same-type proof because the receiver is `this`, its compile-time type is the current `ProbeTarget`, the expected field is declared in the same class and source document, and private accessibility is valid from that type; cross-document member lookup is not required to discover the declaration. The scenario compares only bounded response shape against `TrueEditorBufferEvidence.CompletionEvidence`, then runs the Consumer `PROBE_DEFINITION` exact-target control against unchanged `ProbeDefinitionSymbol`, verifies Target and Consumer disk authority, checks process survival/protocol coverage, closes Consumer then Target when live, and gracefully retires the generation. The cross-vs-same response-shape comparison is observational and always PASS; response shape, exact private member, definition, disk, and process checks remain truthful assertions.

This request ordering is motivated by matching Roslyn Language Server package source at Roslyn commit `3aeb96c9ecc56a5ee483558f9e648e33e7bfe756`: completion uses frozen partial semantics (`CompletionService_GetCompletions.cs` / `Document.WithFrozenPartialSemantics`), and navigation/definition can retry from frozen partial semantics against the original/full document when symbol resolution is insufficient (`AbstractNavigableItemsService.cs`). A cold frozen solution can therefore contain materially less compilation state than a later full-semantic path. Definition success does **not** prove that an earlier completion request observed equivalent semantic state. The probe does not claim to observe Roslyn's internal compilation object directly; it only records order-sensitive protocol evidence consistent with or contrary to that mechanism.

Interpret the same-generation failure evidence first:

- **Post-readiness semantic-order evidence:** primary diagnostic-first readiness completion is `Null`, the post-readiness pre-definition completion is `Null`, definition matches the expected Target symbol, and post-definition completion returns `Array`/`CompletionList` containing exact `ProbeInstanceProperty`. This remains diagnostic-only evidence that a later semantic operation changed completion state; it does not retroactively promote failed `SemanticReadiness`.
- **Definition-warming hypothesis materially weakened:** primary diagnostic-first readiness completion is `Null`, the post-readiness pre-definition completion is `Null`, definition matches the expected Target symbol, and the identical post-definition completion remains `Null`. This exact branch admits `TrueEditorBufferCompletionDisambiguation`.
- **Content mismatch:** post-definition completion becomes non-null but exact `ProbeInstanceProperty` is absent. Inspect the bounded `Probe*` labels and keep the expected direct-member assertion strong.
- **Definition failure:** if the exact Target symbol does not match, do not attribute completion behavior to a comparable semantic state; the true-editor branch is skipped.

Interpret the retained fresh true-editor control as a generation/history comparison, not as primary source-shape authority:

- **Fresh true-editor succeeds while primary same-caret completion failed.** The request-buffer shape is now the same, so investigate generation/session history rather than attributing the result to the old mid-token marker shape.
- **Fresh true-editor remains `Null` with exact definition PASS.** This preserves the existing fail-closed path into the same-document control and then, when that control succeeds, into the diagnostic-pull generation.
- **Fresh true-editor is non-null but exact member is absent.** Inspect only bounded `Probe*` labels and receiver/member content; do not weaken the exact direct-member assertion.
- **Fresh definition control fails.** If `TrueEditorBufferDefinitionSemanticProbeMatchedExpectedFixtureSymbol` is false, do not interpret any changed completion result as a clean generation/history comparison; semantic comparability must be investigated first.

When the retained fresh true-editor Null/definition-PASS branch admits the same-document generation, interpret that result separately:

- **Case F1 — same-document succeeds.** If cross-document true-editor completion is `Null`, same-document completion returns `Array`/`CompletionList` containing exact `ProbePrivateField`, and the exact-target definition still matches, this is strong evidence that the core C# completion path is operational with the current transport/request/capabilities while the failing dimension is cross-document receiver/member semantic visibility. This strengthens a cross-document semantic visibility / frozen-partial semantics explanation. The next patch should focus on how cross-document compilation/semantic state is established for completion rather than generic client-capability imitation; primary authority is still not promoted.
- **Case F2 — same-document remains `Null`.** If both cross-document `return target.|` and same-document `this.|` completion are `Null` while exact-target definition still passes, cross-document receiver visibility is not sufficient to explain the failure. The next isolated experiment should be simpler syntactic/keyword completion that does not require member lookup, distinguishing “all completion/provider paths fail” from “member-access completion specifically fails.” Do not return directly to restore/timing or change capabilities/configuration/context at the same time.
- **Case F3 — non-null response but private member absent.** If same-document completion returns items but exact `ProbePrivateField` is absent, the provider pipeline is at least partly active. Inspect only bounded `Probe*` labels and same-type accessibility/member recommendations; do not weaken the exact private-member assertion.
- **Case F4 — definition comparability fails.** If `SameDocumentDefinitionSemanticProbeMatchedExpectedFixtureSymbol` is false, do not attribute a changed completion response purely to same-document versus cross-document semantics. Investigate whether the incomplete Target editor buffer made that generation's project semantics non-comparable.

The verified Case F1 differential is now explicit: cold cross-document true-editor `return target.|` returns protocol `Null`, while same-document `_ = this.|` returns an `Array`/`CompletionList` containing exact `ProbePrivateField`, with exact-target definition controls passing in both generations. This establishes that generic core C# completion, current request transport, provider activation, response discrimination/normalization, and same-document private-member accessibility are operational. The remaining failure is associated specifically with cross-document semantic/member visibility.

An independent parity review against Microsoft's `vscode-languageclient` found one concrete lifecycle difference with a source-supported route to project compilation state: the Microsoft client honors Roslyn's dynamic diagnostic registrations and automatically starts document diagnostic pulls after `didOpen`; matching Roslyn compiler-semantic diagnostic paths can request a required project compilation. The probe currently advertises `diagnostic.dynamicRegistration = true`, acknowledges registrations, but retains only registration ID/method and does not retain/install `registerOptions` or provider identifiers. This is a known client-contract gap. It is intentionally **not** corrected in this patch because doing so would mix registration-lifecycle behavior with the isolated diagnostic-warming experiment. The parity finding is a causal hypothesis, not an LSP completion requirement and not yet a proven runtime root cause.

Exact Case F1 evidence admits one additional diagnostic-only generation, `DiagnosticPullCompletionDisambiguation`, after the same-document generation has fully retired. Admission is fail-closed from bounded scalar evidence, not scenario PASS/FAIL: primary readiness completion must be `Null` and the post-readiness failure disambiguation must establish completion/definition/completion evidence; primary exact-target definition must pass; the cross-document true-editor snapshot must be verified, remain `Null`, pass exact-target definition, and leave disk unchanged; and the same-document snapshot must be verified, return `Array`/`CompletionList` containing exact `ProbePrivateField`, pass exact-target definition, and leave both fixture files unchanged. `SameDocumentCompletionDisambiguation` now publishes only those bounded scalar observations to `ProbeScenarioContext`; it still preserves its existing request order and PASS/FAIL semantics.

The fresh diagnostic-pull generation is exactly:

```text
fresh explicit generation
    -> initialize + solution/open
    -> wait workspace/projectInitializationComplete
    -> Target didOpen v1 from exact disk source
    -> Consumer didOpen v1 from in-memory true-editor snapshot:
       return target.|
    -> observe static or dynamic textDocument/diagnostic capability
    -> await Consumer textDocument/diagnostic
       identifier omitted
       previousResultId omitted
    -> immediately ordinary CompletionAsync at return target.|
    -> require Array/CompletionList
    -> require exact ProbeInstanceProperty
    -> observational cold-true-editor vs post-diagnostic response-shape comparison
    -> exact-target Consumer definition control
    -> Target/Consumer disk-authority verification
    -> process survival / protocol observation
    -> Consumer didClose, Target didClose when live
    -> graceful retirement
```

The Consumer snapshot is constructed with the same scenario-local exact ordinal replacement used by the true-editor baseline: the unique disk statement `return target.ProbeExtension();` becomes only `return target.` in the version-1 `didOpen` buffer. The caret is immediately after `.`, and newline/EOF must be immediately to the right; no marker, identifier, or semicolon is present at the caret. The disk fixture remains unchanged. The experiment uses the existing no-identifier `PullDiagnosticsAsync` unchanged. A diagnostic response with zero diagnostics is still a completed pull and does not fail the request check; persisted diagnostic evidence is bounded to count plus at most 32 ordinal-distinct/sorted codes, never messages or raw payloads. There is no `Task.Delay`, `Thread.Sleep`, callback polling, `didChange`, reopen, source write, `CompletionContext`, capability change, configuration change, registration implementation, or completion-normalization change. No other Roslyn request occurs between the awaited diagnostic response and the completion request.

The verified G1 runtime is now established evidence for this patch:

```text
cold true-editor:
    Null

same-document:
    CompletionList
    exact ProbePrivateField

diagnostic pull:
    completed

immediate cross-document completion:
    CompletionList
    exact ProbeInstanceProperty

definition:
    exact target PASS
```

The diagnostic pull is now experimentally established as a causal operation that can change cold cross-document completion into correct direct-member completion. This does **not** establish that diagnostics are required for completion, nor does it make automatic diagnostic-provider lifecycle emulation the production design.

Interpret the retained diagnostic-only scenario branches separately:

- **Case G1 — diagnostic pull makes cross-document completion work.** This is the verified current differential above and is the basis for the new primary-generation `SemanticReadiness` candidate policy.
- **Case G2 — diagnostic pull completes but completion remains `Null`.** With exact-target definition still passing, the simple diagnostic-warming hypothesis is materially weakened. The next isolated work should focus on diagnostic source/identifier observability, proof that compiler-semantic diagnostics actually executed, LSP `Solution` identity/caching, `Project.TryGetCompilation`, receiver binding/error type, and frozen syntax-tree membership. Do not return to generic completion-capability imitation.
- **Case G3 — response becomes non-null but exact member is absent.** Semantic/provider state changed, but expected cross-document direct-member visibility remains incomplete. Inspect only bounded `Probe*` labels and keep exact `ProbeInstanceProperty` as the causal assertion.
- **Case G4 — diagnostic capability is unavailable or the pull faults.** Draw no conclusion about frozen semantics or compilation warming. The next patch should isolate dynamic diagnostic registration/identifier observability and then either implement the registration lifecycle correctly or stop advertising unsupported dynamic registration.
- **Case G5 — completion changes but exact-target definition fails.** Do not attribute the result solely to diagnostic warming; first investigate semantic comparability for that generation.

The auto-load comparison reuses the same shared helper when its legacy marker completion fails and that generation remains alive. It therefore performs the same pre-definition natural completion -> exact-target definition -> identical post-definition natural completion sequence inline before `didClose`, using the same already-restored fixture and the same open-document versions. No separate AutoLoad implementation duplicates these requests.

The diagnostic-only scenarios remain authority-separated from the classifier. `SemanticGateDisambiguation`, `TrueEditorBufferCompletionDisambiguation`, `SameDocumentCompletionDisambiguation`, and `DiagnosticPullCompletionDisambiguation` cannot directly promote the fixture or make the candidate suitable. The required classifier gates are now exactly `ExplicitSolutionOpen`, `SemanticReadiness`, `Completion`, `DocumentSynchronization`, `Navigation`, `Diagnostics`, `Rename`, and `Recovery`. Only the required primary-generation diagnostic-first `SemanticReadiness` completion at the stored true-editor caret with exact `ProbeInstanceProperty` can set `FixtureSemanticRequestSucceeded=true`. Definition PASS alone, diagnostic-pull completion alone, or a non-null completion lacking the exact member cannot do so. Once fixture readiness is established, the later true-editor, same-document, and diagnostic-pull disambiguation generations are skipped because further completion disambiguation is no longer required.

The controlled fixture now distinguishes the next results explicitly:

- **Case J1 — full success.** `SemanticReadiness`, `Completion`, `DocumentSynchronization`, `Navigation`, `Diagnostics`, `Rename`, and `Recovery` all pass. With no real-workspace arguments, classification is `SuitableCandidateForRealWorkspaceValidation`; the controlled suite is then ready for separate real-workspace validation.
- **Case J2 — readiness passes but DocumentSynchronization fails.** Probe 1.3.3 now runs the isolated Target `didChange` -> immediate completion failure -> diagnostic re-readiness -> identical completion discriminator. This remains conditional diagnostic evidence only; no automatic mutation-readiness policy is promoted by this patch.
- **Case J3 — primary diagnostic-first readiness fails, but the dedicated fresh diagnostic-first control passes.** Investigate LSP solution identity, workspace update timing, registration timing, and compilation-tracker identity rather than changing completion payload/capabilities.
- **Case J4 — both diagnostic-first paths fail.** Investigate diagnostic source/identifier/state execution and the solution/compilation state reached by the diagnostic operation.

### Probe 1.3.3 — document-mutation semantic-readiness disambiguation

Probe 1.3.3 runs this exact controlled sequence in the existing primary Roslyn generation:

```text
SemanticReadiness established

DocumentSynchronization:
    baseline Consumer completion
        -> require ProbeDiskMember

    Target full didChange v2
        -> immediate Consumer completion
        -> preserve the existing truthful required result

    if the immediate full update is not observed and the process remains live:
        -> existing SemanticReadinessOperation
        -> await Consumer diagnostic pull
        -> immediate identical Consumer completion at the same caret
        -> require ProbeUnsavedMember
        -> require ProbeDiskMember absent

    Target incremental didChange v3
        -> immediate Consumer completion
        -> preserve the existing truthful required result

    if the immediate incremental update is not observed and the process remains live:
        -> existing SemanticReadinessOperation
        -> await Consumer diagnostic pull
        -> immediate identical Consumer completion at the same caret
        -> require ProbeIncrementalMember
        -> require ProbeUnsavedMember absent
        -> require ProbeDiskMember absent

    verify disk Target still contains only ProbeDiskMember
```

The immediate completion result is retained as a bounded `CompletionRequestResult` long enough to
report result kind, raw count, normalized count, `isIncomplete`, expected/stale-member booleans,
and at most 32 deterministic `Probe*` labels. The diagnostic retry persists only diagnostic
capability, count/codes, durations, bounded completion evidence, and immediate-vs-post-diagnostic
response-shape comparison. It does not persist raw completion/diagnostic JSON, diagnostic messages,
or source text.

> Diagnostic semantic re-readiness after document mutation is a controlled disambiguation experiment in probe 1.3.3. It is not yet the required DocumentSynchronization policy and does not make an immediate post-didChange failure acceptable.

Initial `SemanticReadiness` remains the sole primary readiness authority. Mutation re-readiness does
not modify `FixtureSemanticRequestSucceeded`, `FixtureSemanticReadyMs`, or `PrimaryCompletionEvidence`.
The existing immediate `FullDocumentDidChangeSemanticUpdateObserved` and
`IncrementalDidChangeSemanticUpdateObserved` checks remain required, so `DocumentSynchronization`
continues to FAIL when either immediate assertion fails even if the diagnostic retry later proves
that current semantics can be restored. No new `didOpen`, `didClose`, `didChange`, file write,
restore, workspace reload, restart, definition, references, rename, delay, CompletionContext,
capability, configuration, or project-context change is inserted between the immediate completion
and the controlled re-readiness attempt. The diagnostic identifier and `previousResultId` remain
omitted through the unchanged helper. The known dynamic-diagnostic registration-options gap remains
deferred.

The controlled mutation outcomes are interpreted as follows:

- **Case K1 — both mutations recover after diagnostic re-readiness.** If full immediate update FAILs but post-diagnostic completion contains `ProbeUnsavedMember` and excludes `ProbeDiskMember`, and incremental immediate update FAILs but post-diagnostic completion contains `ProbeIncrementalMember` while excluding both older members, this is strong causal evidence that Target mutation invalidates the semantic state required by Consumer cross-document completion and that the existing diagnostic readiness operation can restore the current unsaved project semantics in the same Roslyn generation. The next patch should consider explicit mutation-aware semantic-readiness invalidation/promotion rather than treating immediate failure as acceptable here.
- **Case K2 — full recovers but incremental does not.** Full-document synchronization can be recovered through semantic re-readiness, while incremental synchronization has an additional problem. Next investigate incremental range correctness, version/range mapping, and Roslyn tracked `SourceText` state rather than generic completion capabilities.
- **Case K3 — diagnostics complete but neither updated member appears.** Startup semantic readiness and same-generation post-mutation semantic re-readiness are not equivalent. Next investigate LSP `Solution` identity, compilation-tracker invalidation, whether post-change diagnostics compile the tracked Target text, frozen-partial snapshot membership, and request workspace/version identity.
- **Case K4 — post-diagnostic completion is non-null but stale.** Presence of `ProbeDiskMember` after full mutation, or `ProbeUnsavedMember`/`ProbeDiskMember` after incremental mutation, is stale semantic-state evidence. Do not weaken the exact current-member assertions.
- **Case K5 — immediate completion now passes.** Record the non-reproduction and do not conclude that re-readiness is required until the immediate failure reproduces. Because re-readiness is conditional, no mutation diagnostic is run for a stage whose immediate required assertion already passes.

Recovery remains functionally unchanged.

### Verified 1.3.3 result — same-generation diagnostics do not restore changed Target semantics

The controlled 1.3.3 runtime result falsified Case K1 as the simple explanation. Initial semantic
readiness returned current completion including `ProbeDiskMember`, but both mutation stages remained
stale at the original v1 Target even after the same-generation diagnostic pull completed:

```text
initial readiness
    -> ProbeDiskMember=true

full didChange v2
    -> immediate: ProbeUnsavedMember=false, ProbeDiskMember=true
    -> diagnostic pull
    -> immediate: ProbeUnsavedMember=false, ProbeDiskMember=true

incremental didChange v3
    -> immediate: ProbeIncrementalMember=false, ProbeUnsavedMember=false, ProbeDiskMember=true
    -> diagnostic pull
    -> immediate: ProbeIncrementalMember=false, ProbeUnsavedMember=false, ProbeDiskMember=true

fresh generation + Target v3 replay + Consumer v1 replay + diagnostic readiness
    -> ProbeIncrementalMember=true
    -> ProbeDiskMember=false
```

Thus `mutation invalidates readiness -> diagnostic restores it` is no longer a viable simple model.
Probe-owned unsaved Target v3 is correct; the unresolved problem is which solution/compilation
lineage the same-generation completion actually consumes after mutation.

### Source-supported mechanism — pending TouchDocuments + frozen partial semantics

Exact source at Roslyn commit `3aeb96c9ecc56a5ee483558f9e648e33e7bfe756` supports this mechanism:

```text
didChange
-> tracked SourceText/version updated + cached LSP solutions cleared
-> workspace/LSP solution can carry pending TouchDocumentsAction
-> completion calls WithFrozenPartialSemantics
-> InProgress WithDoNotCreateCreationPolicy uses First().OldProjectState
-> requested Consumer is restored into frozen solution
-> unrelated changed Target may remain at old state
```

Consecutive `TouchDocumentsAction` changes can also merge while retaining the first action's
`OldProjectState`, so `v1 -> v2 -> v3` may become a pending translation effectively rooted as
`v1 -> v3`. That is source-consistent with the observed v1 `ProbeDiskMember` staleness after v3.
It is a **source-supported mechanism**, not yet the complete runtime root cause. In particular, it
does not answer why a completed diagnostic request fails to make the next completion reuse current
full compilation state.

### Probe 1.3.4 — optional RoslynStateLineageTrace

Probe 1.3.4 leaves the official 1.3.3 fixture chain, `DocumentSynchronization`, classifier,
completion/rename blockers, protocol payloads, timing, and official tool provenance unchanged.
After the official fixture scenario/process snapshot is frozen and before `RealGodotWorkspace`, it
adds one optional diagnostic-only scenario named exactly `RoslynStateLineageTrace`.

Without both arguments it is skipped exactly as follows:

```text
RoslynStateLineageTrace SKIPPED
No --state-trace-server/--state-trace-provenance supplied.
```

Trace mode must be explicitly selected with an existing absolute pair:

```text
--state-trace-server <absolute instrumented wrapper>
--state-trace-provenance <absolute provenance.json>
```

The normal `--server` remains the official `roslyn-language-server` 5.12.0-1.26426.8 package
authority and continues through `RoslynLanguageServerToolVerifier`. The state-trace server is a
separate diagnostic command, receives no package-provenance claim, never becomes `PrimarySession`,
and cannot update fixture semantic-readiness/current-document authority. Its process is included in
the final `Processes` array but is excluded from the already-frozen official fixture process/stderr
metrics. `RoslynStateLineageTrace` is absent from `RequiredFixtureScenarios`, so PASS/FAIL/SKIPPED
cannot change candidate classification.

The trace scenario uses only the controlled fixture and a fresh instrumented generation. Its exact
semantic request sequence is:

```text
Target didOpen v1
Consumer didOpen v1 at return target.|

diagnostic #1
completion #1

full Target didChange v2
completion #2

diagnostic #2
completion #3

incremental Target didChange v3
completion #4

diagnostic #3
completion #5
```

No recovery, navigation, rename, definition, delay, retry, reopen, restore, source write, real
workspace, CompletionContext, capability imitation, or dynamic-registration fix is added. The
scenario computes local SHA-256/UTF-8 hashes for exact Target v1/v2/v3 and compares those hashes to
`SETRACE|` observations without persisting source text. Instrumentation perturbation is reported
truthfully rather than compensated for with retries or sleeps.

Temporary upstream tooling lives under `Instrumentation/RoslynStateTrace/`.
`Prepare-RoslynStateTrace.ps1` requires a clean **throwaway** checkout at the exact pinned commit,
preflights exact source anchors before any write, applies only observational calls, builds the
repository's existing LanguageServer project, and generates a wrapper plus `provenance.json` outside
the SystemExplorer source tree. Neither the Roslyn checkout, Roslyn build output, wrapper nor
provenance file belongs in the SystemExplorer release. The helper is disabled unless the trace env
vars are explicit, emits at most 256 stderr events, locks complete trace-line writes, uses monotonic
sequence numbers and `RuntimeHelpers.GetHashCode` process-local identities, and uses only existing
objects, `TryGetText`, already-available `SourceText`, pending tracker fields and non-creating
`TryGetCompilation`. It must not materialize semantic state merely to trace it.

The trace is intended to distinguish:

- **L1:** tracked v3 + completion pre-freeze v3 + post-freeze v1 => frozen-partial rollback.
- **L2:** tracked v3 + completion pre-freeze v1 => state is lost before frozen completion.
- **L3:** diagnostic and next completion share one InProgress tracker => diagnostic did not finalize the assumed state.
- **L4:** diagnostic ends Final/v3 but next completion uses a different InProgress/v1-rooted tracker => cross-request lineage recreation.
- **L5:** completion pre/post-freeze remain v3 while items stay stale => reopen provider/result-cache investigation.
- **L6:** instrumentation removes the stale behavior => instrumentation perturbation; reduce observation rather than add delays.

`CompletionContext` remains intentionally deferred. Matching Roslyn protocol conversion maps both missing completion context and public `CompletionTriggerKind.Invoked` to the internal invoke trigger for core C# completion, so explicit `CompletionContext` would not isolate the semantic-order hypothesis being tested here. `contextSupport`, richer VS Code-like completion capabilities, concrete completion configuration defaults, dynamic-registration handling, and initialization options remain unchanged.

The stale-version experiment is deliberately run in its own server generation. It opens both Target and Consumer before its version experiment and completion requests, closes them in reverse-open order when the generation remains live, and remains observational only; it never replaces future CodeService-side document-version authority.

Temporary fixture files are deleted after the run unless `--keep-artifacts` is supplied.

## Real workspace mode

A real Godot-generated C# workspace can be tested read-only with either:

```text
--solution <absolute .sln/.slnx path>
```

or:

```text
--project <absolute .csproj path>
```

Optionally select read-only completion and/or definition smoke on one existing C# source. Completion and definition use independent positions because a useful completion position is normally not a useful definition position:

```text
--document <absolute .cs path>
--completion-line <zero-based-line>
--completion-character <zero-based-character>
--definition-line <zero-based-line>
--definition-character <zero-based-character>
--expected-definition <absolute .cs path>
```

The completion pair is atomic: `--document`, `--completion-line`, and `--completion-character` must be present for completion smoke. The definition group is also atomic: `--document`, `--definition-line`, `--definition-character`, and `--expected-definition` must all be present. Partial groups are invalid arguments. `--document` and `--expected-definition` must be absolute existing `.cs` files.

If completion is selected, PASS requires a non-empty completion result and a still-live server generation. If definition is selected, PASS requires a non-empty definition result and at least one returned `file:` location whose normalized filesystem path exactly matches `--expected-definition` (ordinal-ignore-case on Windows, ordinal elsewhere). A ceremonial non-empty or wrong-target definition response is not enough.

When either semantic smoke is selected, the real-workspace scenario reads/hashes the source, sends exactly one `didOpen`, runs the selected semantic requests at their separate positions, sends exactly one `didClose`, and verifies the file bytes are unchanged. It never sends `didChange`, never applies rename edits, and never writes, creates, deletes, or rewrites user source files.

Example:

```powershell
$ProbeRoot = "C:\Temp\System Explorer Roslyn Probe"

dotnet run --project .\Spikes\RoslynLanguageServerCapabilityProbe\RoslynLanguageServerCapabilityProbe.csproj -c Release -- `
  --server "$ProbeRoot\tools\roslyn-language-server.cmd" `
  --solution "C:\Projects\My Godot Project\My Game.sln" `
  --document "C:\Projects\My Godot Project\Scripts\Player.cs" `
  --completion-line 42 `
  --completion-character 20 `
  --definition-line 18 `
  --definition-character 15 `
  --expected-definition "C:\Projects\My Godot Project\Scripts\PlayerHealth.cs"
```

If no semantic document selection is supplied, the real-workspace semantic smoke is reported as skipped and only project load/readiness is evaluated. Selecting only completion or only definition can strengthen real-workspace evidence, but it does not produce final `SuitableCandidate`; final classification requires both completion and exact-target definition smoke plus the read-only source check.

## Report

A machine-readable JSON report with `schemaVersion = 3` and `probeVersion = 1.3.3` is written after a completed probe run. By default it is created under:

```text
<system-temp>/SystemExplorer.CodeService/RoslynProbe/roslyn_probe_<timestamp>.json
```

Use `--report <absolute-or-resolved-path>` to choose another location.

The report contains:

- expected and private-inventory-observed Roslyn LS versions plus exact verification state;
- pinned StreamJsonRpc version;
- logical `serverCommandPath` and top-level `serverLaunchKind`;
- platform/framework information;
- controlled fixture and optional real-workspace summaries;
- advertised and dynamically registered semantic capabilities;
- separate scenario/check results and durations, including bounded completion response-shape evidence (`resultKind`, raw item count, normalized item count, optional `isIncomplete`) and bounded recent server-message evidence on controlled semantic-gate failure;
- per-generation launch-root identity: `processId`, start time, `launcherExecutablePath`, `serverCommandPath`, `launchKind`, and generation;
- bounded stderr plus truncation state;
- coarse live **owned root-process** working-set/private-memory samples;
- forced-kill state and exit code;
- deterministic candidate classification.

Root-process metrics are not aggregate Roslyn process-tree memory and are not specifically `Microsoft.CodeAnalysis.LanguageServer` memory. In a Windows `.cmd` run they describe the command-launcher root and are useful only as coarse lifecycle evidence. This patch intentionally does not add process-tree memory aggregation.

A produced report cannot represent an unverified normal startup: `roslynLanguageServerActualVersion` comes from the exact private `dotnet tool list` row, and `roslynLanguageServerVersionVerified` is true only after exact package/version/command verification succeeds.

Source contents are not written to the report.

Candidate values are:

- `SuitableCandidateForRealWorkspaceValidation` — all required fixture hard gates passed but real semantic validation was not selected/completed;
- `SuitableCandidate` — all required fixture hard gates passed and the real workspace passed both non-empty completion smoke and exact-target definition smoke while the source remained unmodified;
- `UnsuitableCandidate` — one or more required capability gates failed;
- `Inconclusive` — reserved for infrastructure conditions where a semantic decision cannot safely be made.

These statuses are spike evidence only. They do **not** mean Roslyn LS has been selected for production and do not implement Roadmap Phase 6, Phase 7, or Phase 8.

## Cancellation and process-tree retirement

Fixture preparation temporarily owns one bounded `dotnet restore` process before any Roslyn generation exists. Cancellation during that preparation cannot admit Roslyn work: the restore process tree and both redirected output drains are retired first, and cancellation is propagated only after that owned setup operation is terminal.

One server generation owns the complete non-daemon process tree created for one `roslyn-language-server` command launch. On Windows that tree may begin at `cmd.exe`; on direct-launch platforms it begins at the tool command itself. The thin client and its dedicated language-server child belong to that same generation.

Ctrl+C closes probe-level admission by cancelling the run. Outstanding waits are bounded. Cancellation is not treated as proof that an owned subprocess retired: before the runner returns, an active fixture restore or every owned Roslyn generation is driven to a terminal root-process state, using forced process-tree retirement for restore and graceful LSP shutdown when sensible for Roslyn generations, with forced process-tree kill as fallback.

Normal retirement is:

```text
LSP shutdown request
  -> response
  -> exit notification
  -> bounded owned-root exit wait
  -> Kill(entireProcessTree: true) if still alive
```

The explicit recovery scenario remains intentionally different from graceful retirement:

```text
pre-crash semantic request
  -> confirm old generation root is alive
  -> issue OS entire-process-tree kill while JSON-RPC/stdin/stdout client is still live
  -> wait for old launch root terminal state
  -> dispose JSON-RPC/client transport
  -> finish stderr drain/process result materialization
  -> start a fresh generation
  -> reopen workspace
  -> replay Target unsaved text/version and reopen Consumer version 1
  -> RefreshDynamicCapabilities
  -> require diagnostic capability
  -> await Consumer diagnostic pull
  -> immediately ordinary completion
  -> require ProbeIncrementalMember present and ProbeDiskMember absent
  -> close Consumer then Target
```

The old generation is not sent `shutdown` or `exit` on this crash path. `ForcedKill=true` is recorded only when a kill call was actually issued successfully; a root that exited spontaneously before the crash boundary cannot be mislabeled as a forced crash. Client-disposal failure cannot skip owned process retirement/result recording.

`NewProcessIdentityDifferent` continues to compare the launch-root `ProcessId` and `StartTimeUtcTicks`. The probe does not discover a child PID merely to label it a "Roslyn PID". The required invariant is that the old owned generation is terminal before the new one starts.

The probe does not silently restart after a failed `didChange`, retry semantic requests until they happen to pass, or convert one document-sync shape into another to hide a server defect.

## Exit codes

```text
0 = all required selected capability scenarios passed
1 = capability/scenario failure
2 = invalid arguments
3 = private tool provenance / server launch setup failure
4 = probe infrastructure failure or cancellation
```

## Post-hardening decision gate

Use this spike as evidence only after the following order has completed:

```text
clean production build
  -> clean spike build
  -> verify exact private roslyn-language-server inventory
  -> controlled fixture capability probe using the private command path
  -> inspect schemaVersion=3 report
  -> only then run a real Godot workspace probe with completion + exact-target definition
```

A semantic scenario failure after successful setup is capability evidence. Do not weaken capability assertions merely to obtain PASS.

If all hard gates pass, Roslyn Language Server becomes the leading Phase-6 semantic-engine candidate and the next work is production architecture/design for a CodeService-owned Roslyn process host, semantic workspace readiness, and Roslyn fault/restart containment. If an important gate fails, test that exact failed capability against another candidate before considering a custom Roslyn host. Do not begin production Roslyn integration or a custom Roslyn host from this spike alone.

## Build isolation

Normal production build:

```bash
dotnet build SystemExplorer.CodeService.slnx -c Release
```

Dedicated spike build:

```bash
dotnet build Spikes/SystemExplorer.CodeService.Spikes.slnx -c Release
```

The production solution intentionally does not build the spike or restore StreamJsonRpc. `SystemExplorer.CodeService.csproj` also keeps `<Compile Remove="Spikes/**/*.cs" />` as an independent source-glob isolation boundary.


## Probe 1.3.6 — CompletionSemanticOrigin one-command verification

Probe 1.3.6 keeps ordinary report schema 3, all ordinary `ProbeBaseline` capabilities, the existing
full-suite suitability classifier, and the diagnostic-only status of `CompletionSemanticOrigin`
unchanged. Full capability mode still requires `--server`, still runs
`RoslynLanguageServerToolVerifier`, still executes the current `ProbeScenarioRunner`, and still derives
its exit semantics from `ProbeOverallDecision`.

The existing semantic-origin scenario remains available in full mode through the optional pair:

```text
--semantic-origin-server <absolute generated wrapper>
--semantic-origin-provenance <absolute generated provenance.json>
```

It remains outside `RequiredFixtureScenarios`; its PASS/FAIL/SKIPPED result does not affect
`SuitableCandidate` / `UnsuitableCandidate`, `FixtureSemanticRequestSucceeded`,
`FixtureSemanticReadyMs`, fixture server capabilities, or primary document/version state.

Probe 1.3.6 additionally adds an explicit dedicated mode:

```text
--semantic-origin-only
--semantic-origin-server <absolute generated wrapper>
--semantic-origin-provenance <absolute generated provenance.json>
[--report <path>]
[--keep-artifacts]
```

This mode rejects ordinary/full-suite options, does not require or verify an ordinary private
`--server`, does not execute the full scenario runner, StateTrace, real workspace, recovery,
document synchronization, comparisons, or the ordinary suitability classifier. It creates/restores the
same controlled `ProbeFixtureWorkspace` and runs exactly `CompletionSemanticOriginScenario`. The
scenario's existing `ProbeCheckResult` assertions are the semantic authority:

```text
Local
Parameter
LocalFunction
CurrentType        depth 0
BaseType           depth 1
BaseType           depth 2
OtherUserCode
source-backed reduced extension
FrameworkOrOther
Unknown/non-symbol control
metadata well-formedness
server survival
graceful retirement
```

`CompletionSemanticOriginScenario.Status == Pass` returns exit 0, `Fail` returns exit 1, unexpected
`Skipped` returns infrastructure exit 4. Invalid CLI combinations remain exit 2, instrumented
server/provenance setup failures use exit 3 where setup semantics apply, and fixture/general
infrastructure failures use exit 4. This dedicated decision path does not add semantic origin to the
ordinary `RequiredFixtureScenarios`.

Only the semantic-origin scenario uses
`RoslynLspClientCapabilityProfile.ProductionCompletionWire`, which mirrors the current production
completion-relevant VS-extension wire. Existing scenarios continue to use `ProbeBaseline` exactly as
before. The temporary semantic classifier and its fail-closed rules remain diagnostic evidence only;
production now carries the same verified classifier semantics through the separate private Roslyn v2
runtime and Service `CompletionSchemaVersion = 3`. The probe still does not own or compile into that
production implementation.

### Normal one-command semantic-origin verification

On Windows, normal verification is now the supported owner-facing runner:

```bat
Instrumentation\CompletionSemanticOrigin\Run-CompletionSemanticOrigin.cmd ^
  -RoslynRepositoryRoot "C:\Source\roslyn" ^
  -ServiceThirdPartyZip "C:\Artifacts\Service.ThirdParty.zip"
```

The two paths can instead be configured once through:

```text
SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
```

after which `Run-CompletionSemanticOrigin.cmd` is sufficient. Explicit parameters take precedence over
environment values. The runner does not scan disks or guess locations.

The runner verifies the exact current production-v2 `Service.ThirdParty.zip` SHA-256, opens it through
.NET zip APIs, verifies unique canonical `0001` and `0002` entries plus both pinned hashes, and extracts
only `ThirdParty/RoslynLanguageServer/PROVENANCE.txt` and canonical semantic-reuse `0001`. The low-level
preparation intentionally rebuilds the historical semantic-reuse-only v1 baseline before adding
throwaway diagnostic instrumentation, so production `0002` is validated as current archive provenance
but is not applied to that temporary baseline. The supplied normal Roslyn repository is not checked
out, reset, cleaned, patched, or otherwise source-mutated.

The runner then invokes the existing low-level `Prepare-CompletionSemanticOrigin.ps1` against the owned
clean worktree. That remains the single implementation of Roslyn restore, canonical patch application,
source-anchor verification, semantic-origin instrumentation, targeted LanguageServer build, wrapper
generation, and preparation provenance. Generated `provenance.json` is independently validated before
the C# probe is invoked.

The C# invocation is dedicated:

```text
dotnet run ... -- --semantic-origin-only
    --semantic-origin-server <generated wrapper>
    --semantic-origin-provenance <generated provenance>
    --report <persistent report>
```

No ordinary `--server` is supplied. The C# scenario, not PowerShell console-text parsing, owns semantic
PASS/FAIL. Console output is a concise matrix and the process exit code is the actual test result:

```text
RESULT: PASS
exit code 0
```

or:

```text
RESULT: FAIL
exit code 1
```

The runner uses process-local `powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass` only through
its `.cmd` launcher; it never calls `Set-ExecutionPolicy` or changes registry/Machine/User policy.
Temporary worktree, extracted ThirdParty inputs, Roslyn instrumentation output, and probe build
artifacts live outside the Service source tree. By default the owned dirty worktree and transient
staging are safely removed after the run using a run-specific ownership marker. Native Git stderr is
captured as diagnostics while Git's exit code remains authority; temporary Roslyn restore/build work
also disables persistent MSBuild/shared-compilation processes, and owned worktree removal uses bounded
Windows retries before failing closed and retaining the owned path. Instrumentation source anchors are
exact-content checks with only LF/CRLF representation normalized to the checked-out pinned source.
Reports and bounded runner diagnostics survive cleanup. `-KeepArtifacts` retains runner-owned state for
debugging.

`Prepare-CompletionSemanticOrigin.cmd` / `.ps1` remain supported expert/debug low-level entrypoints for
an already-clean throwaway checkout. They deliberately do not reset or clean an arbitrary caller-owned
checkout after failure. Normal project-owner verification should use `Run-CompletionSemanticOrigin.cmd`
instead of manually preparing a checkout, running the unrelated full probe, reading raw LSP JSON, or
classifying semantic categories by hand.


### Production private-runtime reproduction

`Instrumentation/CompletionSemanticOrigin/ProductionRuntime/` is the deterministic reproduction and
promotion tooling for the separate private Roslyn production runtime. Its canonical build-input copy:

```text
patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch
```

has SHA-256:

```text
6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
```

and is byte-identical to the shipped canonical `0002` in production `Service.ThirdParty.zip`.

The Windows builder takes a local Roslyn repository plus the previous verified v1 ThirdParty archive,
creates its own pinned detached worktree, extracts and applies unchanged semantic-reuse `0001`, applies
canonical production `0002`, uses repository-native restore/build, packages one coherent win-x64
LanguageServer output, and generates hashes, `PROVENANCE.txt`, machine-readable evidence, and the new
ThirdParty archive. The owner source Roslyn working tree may be dirty and is never reset/cleaned or
patched by the builder.

The successful project-owner production build on 2026-09-03 produced:

```text
roslyn-3aeb96c9-systemexplorer-6818cc1b3a10-win-x64-v2
Service.ThirdParty.zip SHA-256:
45f152e900326520626b5f17248fdf608d7a7e61f01da42b480dce138f5453d8
```

This production builder is not a second semantic assertion engine. Semantic expected-value authority
remains the C# `CompletionSemanticOriginScenario`; normal Service runtime validation owns only shipped
binary/provenance identity and strict private-metadata contract parsing.

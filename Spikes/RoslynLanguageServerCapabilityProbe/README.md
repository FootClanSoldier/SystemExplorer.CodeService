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

Restore latency is setup/preparation latency and is not included in `SemanticReadyMs`. The semantic-ready stopwatch still begins only after the first Roslyn session has been started and immediately around workspace initialization/readiness plus the first semantic gate.

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
2. baseline `didOpen` of `ProbeTarget.cs` version 1 followed by `ProbeConsumer.cs` version 1;
3. the first semantic gate plus baseline/warm completion and instance/static/private/protected/generic/extension completion checks;
4. full-document `didChange` of Target to version 2 changing `ProbeDiskMember` to `ProbeUnsavedMember` without a disk write;
5. incremental range `didChange` of Target to version 3 changing it to `ProbeIncrementalMember`;
6. definition and references;
7. protocol-aware pull or publish diagnostics using its own separately opened diagnostics document and an unsaved semantic error, followed by clearing and closing it;
8. `prepareRename` when supported and `rename`, verifying the returned `WorkspaceEdit` without applying it;
9. forced server-generation process-tree death while the semantic Target/Consumer documents and JSON-RPC/stdin/stdout are still live, exact old-generation root retirement, fresh generation initialization/project load, Target unsaved text/version replay plus Consumer reopen, and post-restart semantic verification;
10. an optional isolated stale-version observation (`--stale-version-experiment`).

Fixture `textDocument/*` semantic requests are issued only after the request document has been opened in that active server generation. Open-document state is generation-local: after recovery retires the old generation, Target is reopened with the current unsaved text/version while unchanged Consumer is reopened from disk at version 1 before the post-restart completion request. The auto-load comparison uses the same baseline Target-then-Consumer `didOpen` state before its completion probe, and closes Consumer then Target before normal retirement.

Controlled fixture marker comments have two intentionally different position semantics. Completion markers identify an editor-caret location and resolve to the **start of the marker comment**, which is fail-closed verified to be immediately after a member-access dot. For example:

```text
target.|/*PROBE_INSTANCE_COMPLETION*/ProbeInstanceProperty
```

Definition/reference/rename markers retain the existing symbol-target semantics and resolve to the **end of the marker comment**, immediately before the existing identifier:

```text
target./*PROBE_DEFINITION*/|ProbeDefinitionSymbol
```

The source fixture is not rewritten to create these caret positions; existing member identifiers remain present. The controlled completion request still sends `TextDocumentPositionParams` without an explicit LSP completion context. In particular, this probe version does not add `contextSupport`, `CompletionContext`, `triggerKind`, or `triggerCharacter`; cursor position remains the single semantic input dimension changed by this experiment.

Each completion request now records bounded response-shape evidence before item normalization. The evidence distinguishes JSON `null`, an explicit completion-item array, a `CompletionList` object with an `items` array, undefined/default response state, and unexpected object/value shapes. It records the raw response array length and optional boolean `isIncomplete`, while normalized persisted items remain capped by `MaxCompletionItems`. The full completion JSON payload and arbitrary completion-label sets are not persisted; semantic-gate details continue to keep only a deterministic, bounded `Probe*` label summary.

The explicit semantic gate also records `FixtureRestoreVerified` and a required `NoUnresolvedDependencyWarning` check against the existing bounded server-message capture. The auto-load comparison records `AutoLoadFixtureRestoreReused` and `AutoLoadNoUnresolvedDependencyWarning`. The known Roslyn message fragment `has unresolved dependencies` is matched case-insensitively. Existing `ServerMessagesObserved` / `AutoLoadServerMessagesObserved` failure evidence is retained so other project-load or build-host messages remain visible if completion still fails.

The primary semantic gate remains the marker-based completion request and still requires the exact `ProbeInstanceProperty` label. If that primary request fails while its Roslyn generation remains alive, the probe now runs a bounded `SemanticGateDisambiguation` scenario against the **same already-open Target/Consumer documents and the same primary generation**. It does not restore again, send another `didOpen`, send `didChange`, or change semantic readiness. The diagnostic requests are:

```text
primary marker completion:
    target.|/*PROBE_INSTANCE_COMPLETION*/ProbeInstanceProperty

natural no-comment member completion:
    return target.|ProbeExtension();

exact-target definition:
    ProbeDefinitionSymbol -> ProbeTarget.cs
```

The natural completion position is derived from the unique existing statement `return target.ProbeExtension();`, immediately after `return target.`. No marker comment or other trivia sits between the member-access dot and that diagnostic caret, and the fixture source itself is not modified. Natural completion records the same bounded response shape and at most 32 ordinal-distinct/sorted `Probe*` labels as the primary gate. The diagnostic definition requires at least one returned location and separately reports whether one location matches `ProbeTarget.cs` at the expected `ProbeDefinitionSymbol` line. Marker-vs-natural response-shape comparison is observational evidence only.

Interpret the failure-only evidence as follows:

- marker fails + natural completion includes `ProbeInstanceProperty` + exact-target definition passes -> marker/trivia fixture ambiguity;
- marker fails + natural completion fails + exact-target definition passes -> completion-specific failure;
- marker fails + natural completion fails + exact-target definition fails -> broader workspace/document semantic-state problem;
- natural completion returns items but omits `ProbeInstanceProperty` while definition passes -> completion content/semantic-context mismatch requiring bounded label analysis.

The auto-load comparison runs the same natural-completion and exact-target-definition diagnostics inline when its primary completion fails and that generation remains alive. Diagnostic success never promotes `FixtureSemanticRequestSucceeded`, never assigns `FixtureSemanticReadyMs`, and never replaces the primary marker gate. `SemanticGateDisambiguation` is deliberately absent from `RequiredFixtureScenarios`, so it cannot make an otherwise suitable candidate unsuitable or suitable by itself. The required classifier gates remain exactly `ExplicitSolutionOpen`, `Completion`, `DocumentSynchronization`, `Navigation`, `Diagnostics`, `Rename`, and `Recovery`.

`CompletionContext` remains intentionally unchanged in this probe version. Completion still uses `TextDocumentPositionParams`; `contextSupport`, `triggerKind`, and `triggerCharacter` are not added. The diagnostic step is intended to disambiguate the existing completion failure before another completion-input or client-capability dimension is introduced.

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

A machine-readable JSON report with `schemaVersion = 3` and `probeVersion = 1.2.5` is written after a completed probe run. By default it is created under:

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
  -> semantic verification
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

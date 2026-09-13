# CompletionSemanticOrigin verification and production-runtime tooling

This directory contains two deliberately separate responsibilities:

- temporary diagnostic semantic-origin instrumentation used by the C# capability/regression scenario;
- `ProductionRuntime/`, the deterministic reproduction/promotion recipe for the shipped private Roslyn
  production runtime.

The temporary instrumentation does not compile into production Service code. The production runtime
builder likewise remains spike/tooling orchestration; the actual shipped runtime lives only in the
separate `Service.ThirdParty.zip` artifact.

The required semantic baseline is exactly:

```text
dotnet/roslyn
upstream commit: 3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
+ canonical SystemExplorer semantic-reuse patch SHA-256:
  11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
baseline distribution:
  roslyn-3aeb96c9-systemexplorer-405fb7f9860-win-x64-v1
+ temporary semantic-origin instrumentation
```

## Entrypoints

```text
Run-CompletionSemanticOrigin.cmd
    recommended Windows end-to-end verification entrypoint

Run-CompletionSemanticOrigin.ps1
    owner-facing orchestration implementation

Prepare-CompletionSemanticOrigin.cmd
    low-level preparation entrypoint

Prepare-CompletionSemanticOrigin.ps1
    low-level instrumentation/build implementation
```

`Run-CompletionSemanticOrigin.cmd` uses
`powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass` only for its child process and propagates
the exact child exit code. It never mutates persistent execution policy or the registry.

Normal verification accepts a normal local Roslyn repository plus the unextracted
`Service.ThirdParty.zip`:

```bat
Run-CompletionSemanticOrigin.cmd ^
  -RoslynRepositoryRoot "C:\Source\roslyn" ^
  -ServiceThirdPartyZip "C:\Artifacts\Service.ThirdParty.zip"
```

The explicit parameters may be replaced by
`SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT` and
`SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP`. The runner verifies the exact current production-v9 archive SHA, verifies unique canonical semantic-reuse `0001`,
semantic-origin `0002`, receiver-relative semantic-origin `0007`, named-type receiver semantic-origin `0008`, and qualified-name receiver-recovery `0009` entries plus their pinned hashes through .NET
zip APIs, and extracts only PROVENANCE plus `0001` because the temporary instrumentation path intentionally
reconstructs the semantic-reuse-only v1 preparation baseline. It
requires the pinned commit object to already exist locally, creates a unique detached runner-owned
worktree, and invokes the existing low-level prepare script. It performs no clone, fetch, Roslyn
download, ThirdParty download, or disk scanning.

The high-level runner validates the generated `provenance.json`, invokes the C# probe in
`--semantic-origin-only` mode, prints the C# summary live, and propagates the C# exit code. It does not
parse console text to decide semantic PASS/FAIL. The actual assertions remain exclusively in
`CompletionSemanticOriginScenario` and its existing `ProbeCheckResult` comparisons.

The runner's temporary worktree and staging live below a supplied/default temp `WorkRoot`, never under
the Service source tree. Default cleanup is permitted only for the worktree the runner itself created
and only after run-root/ownership-marker verification. Git native stderr is treated as diagnostic text;
the actual Git process exit code is success/failure authority, which keeps normal Git-for-Windows
progress output from becoming a PowerShell terminating error. Owned-worktree removal uses bounded
retries for transient Windows file-handle release and may delete only a deregistered residual directory
that is still proven to lie below the current owned run root. It never runs `git reset --hard` or
`git clean -fdx` against the supplied Roslyn repository. `-KeepArtifacts` retains runner-owned state for
debugging. Reports and bounded runner diagnostics are retained outside transient Roslyn state.

## Low-level preparation

`Prepare-CompletionSemanticOrigin.cmd` / `.ps1` remain supported for expert/debug use with an
already-clean throwaway checkout. The preparation script requires `-RoslynRoot`,
`-CanonicalSystemExplorerPatchPath`, and `-OutputRoot`. It verifies exact HEAD, a completely clean
worktree, canonical patch SHA, `git apply --check`, expected pinned files, repository-native restore
while pristine, and clean/exact state after restore. It then applies the canonical patch and validates
every temporary instrumentation anchor exactly once. Anchor matching remains exact for source content
but adapts the template to the checked-out file's existing LF/CRLF convention, so normal Windows Git
line-ending policy cannot make a valid pinned anchor disappear. Restore/build disable MSBuild node reuse
and the targeted LanguageServer build disables shared compilation to avoid retaining handles into an
owned temporary worktree. The script then emits an absolute launcher plus `provenance.json`.

Low-level preparation deliberately does not reset/clean a caller-owned checkout after mutation. That
safety rule is unchanged; automatic cleanup exists only in the high-level runner because it owns the
temporary worktree itself.

## Semantic authority

The launcher opts in with `SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN=1`. The helper otherwise returns
the original completion item unchanged. It writes no source text or logs. The provider call site owns
the protected nested `SymbolAndSelectionInfo` representation and projects the complete grouped list to
`ImmutableArray<ISymbol>` before crossing into the standalone helper. The helper therefore classifies
the same complete symbol group without depending on an inaccessible Roslyn nested type; grouped symbols
must agree on both origin and depth or the item becomes `Unknown` without depth. Instrumentation version 4 mirrors
production V9 receiver semantics: safe ordinary explicit value receivers keep the V7 `TypeInfo`-based anchor; an
unambiguous resolved named-type receiver or alias targeting a non-error named type is accepted as the semantic anchor
before the namespace/type fail-closed guard; and cross-line `QualifiedName` parser recovery binds the left receiver
speculatively with `BindAsExpression` before feeding the same authority-resolution rules. `this`, `base`, unqualified completion, namespaces, namespace aliases,
ambiguous/error type authority, and unsupported receiver shapes fall back to the lexical containing type. Reduced
extension methods still classify from `ReducedFrom` declaration authority and therefore remain `OtherUserCode` in the probe case.

Temporary metadata flows only as:

```text
ISymbol + SyntaxContext
  -> private CompletionItem.Properties
  -> temporary VSInternalCompletionItem fields
  -> OptimizedVSCompletionListJsonConverter explicit serialization
  -> private diagnostic JSON fields
```

No ranking, filtering, SortText interpretation, item ordinal inference, label heuristic, or Godot
location contract is introduced. No Roslyn binaries, generated wrapper, generated provenance, temporary
checkout, build output, extracted ThirdParty files, reports, or logs belong in the Service patch zip.



## Current V9 production runtime reproduction

`ProductionRuntime/Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.cmd` is the retained Windows one-command
reproduction/promotion entrypoint for the adopted private Roslyn V9 runtime. Its companion `.ps1`, bundled canonical
`0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch`, and `README-V9-Builder-v3.txt`
are preserved from the successful V9 build kit. The builder consumes the verified V8 ThirdParty baseline, verifies
canonical `0001 -> 0008`, applies corrected canonical `0009`, runs the retained protocol/workspace gates plus the full
C# `CompletionServiceTests` class, builds a coherent LanguageServer Release payload, and produces
`Service.ThirdParty_V9.zip` plus evidence/adoption values.

The adopted V9 identities are:

```text
0009 SHA-256:
2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd

distribution id:
roslyn-3aeb96c9-systemexplorer-2b9ee3aff616-win-x64-v9

Service.ThirdParty_V9.zip SHA-256:
93fdbbcbbf384f14a72d7ab778dbfa43eb03431db76022aa1487137820572321
```

The materializing V9 build reported `CompletionServiceTests` total 24, passed 24, failed 0, skipped 0. Runtime
bytes remain external ThirdParty authority; these retained scripts are reproducibility/build-verification tooling only.

## Historical V8 production runtime reproduction

`ProductionRuntime/Build-ProductionTypeReceiverSemanticOriginRuntime_v2.cmd` is the retained Windows one-command
reproduction/promotion entrypoint for the adopted private Roslyn V8 runtime. Its companion `.ps1` and
`README-V8-Builder-v2.txt` are preserved byte-for-byte from the successful V8 build kit. The builder consumes the
verified V7 ThirdParty baseline plus canonical external `0008`, verifies the pinned `0001 -> 0007` chain, applies
`0001 -> 0008`, runs the retained protocol/workspace gates plus the full C# `CompletionServiceTests` class, builds
a coherent LanguageServer Release payload, and produces `Service.ThirdParty_V8.zip` plus evidence/adoption values.

The adopted V8 identities are:

```text
0008 SHA-256:
df87da9cf8f7a02217e71341734ae892d653506838680a2768c1177782fbd400

distribution id:
roslyn-3aeb96c9-systemexplorer-df87da9cf8f7-win-x64-v8

Service.ThirdParty_V8.zip SHA-256:
ad2c4801a8dc06b4d564e46436c2006af7f502e885b5e1df61f767c2c292129f
```

The materializing V8 build reported `CompletionServiceTests` total 21, passed 21, failed 0, skipped 0. Runtime
bytes remain external ThirdParty authority; these retained scripts are reproducibility/build-verification tooling only.


## Historical V2 production runtime reproduction

`ProductionRuntime/Build-ProductionCompletionSemanticOriginRuntime.cmd` is the Windows one-command
reproduction/promotion entrypoint for the private Roslyn production runtime. It accepts a normal local
Roslyn repository plus the previous verified v1 `Service.ThirdParty.zip`, owns its detached temporary
worktree, applies canonical `0001` and the byte-pinned `ProductionRuntime/patches/0002-...patch`, runs
repository-native restore/build, packages one coherent win-x64 runtime, and generates hashes,
`PROVENANCE.txt`, machine-readable evidence and the new ThirdParty archive.

The canonical production `0002` build-input SHA-256 is:

```text
6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
```

The successful project-owner build produced:

```text
roslyn-3aeb96c9-systemexplorer-6818cc1b3a10-win-x64-v2
Service.ThirdParty.zip SHA-256:
45f152e900326520626b5f17248fdf608d7a7e61f01da42b480dce138f5453d8
```

The shipped `Service.ThirdParty.zip` canonical `0002` entry must remain byte-identical to the source-tree
build input. This builder is reproducibility tooling, not a second semantic assertion engine; semantic
PASS/FAIL authority remains the C# `CompletionSemanticOriginScenario`.

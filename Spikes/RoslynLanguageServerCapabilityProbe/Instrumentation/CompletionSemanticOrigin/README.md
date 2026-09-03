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
`SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP`. The runner verifies the exact current production-v2 archive
SHA, verifies unique canonical semantic-reuse `0001` and semantic-origin `0002` entries plus both pinned
hashes through .NET zip APIs, and extracts only PROVENANCE plus `0001` because the temporary
instrumentation path intentionally reconstructs the semantic-reuse-only v1 preparation baseline. It
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
must agree on both origin and depth or the item becomes `Unknown` without depth. Reduced extension methods classify from `ReducedFrom`, and current/base authority is the
lexical containing type at the completion position rather than an arbitrary receiver type.

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


## Production runtime reproduction

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

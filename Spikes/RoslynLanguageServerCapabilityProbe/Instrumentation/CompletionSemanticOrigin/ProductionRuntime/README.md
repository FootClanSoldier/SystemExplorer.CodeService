# Production completion semantic-origin runtime reproduction

Current adopted runtime: V9. Use `Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.cmd` / `.ps1`,
`README-V9-Builder-v3.txt`, and the bundled corrected canonical `0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch`
for the canonical V9 reproduction path. The materialized V9 runtime is external ThirdParty authority and is not
embedded in the normal Service source zip.

The historical material immediately below documents the retained V2 reproduction baseline; later sections inventory newer retained reproduction kits.

This directory contains the deterministic external build recipe used to produce the
SystemExplorer private Roslyn Language Server win-x64 v2 distribution for production
completion semantic-origin metadata.

The canonical build input is:

```text
pinned dotnet/roslyn commit
3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
    +
current v1 Service.ThirdParty.zip semantic-reuse patch 0001
    +
patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch
```

The production `0002` build-input copy has SHA-256:

```text
6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
```

The project-owner production build on 2026-09-03 completed successfully and produced
stable distribution id:

```text
roslyn-3aeb96c9-systemexplorer-6818cc1b3a10-win-x64-v2
```

The shipped canonical `0002` entry in the separate `Service.ThirdParty.zip` must remain
byte-identical to the build-input copy here.

The builder intentionally accepts the previous verified v1 `Service.ThirdParty.zip`
as reproduction input because it extracts the unchanged canonical semantic-reuse `0001`
plus redistribution license/notices and then rebuilds the complete v2 runtime. It never
uses the old v1 runtime binaries as build output.

Normal invocation from this directory on Windows:

```bat
Build-ProductionCompletionSemanticOriginRuntime.cmd ^
  -RoslynRepositoryRoot "C:\Temp\roslyn" ^
  -CurrentServiceThirdPartyZip "C:\Temp\Service.ThirdParty.zip"
```

The owner Roslyn working tree may be dirty and is not mutated. The builder owns a detached
temporary worktree, applies canonical 0001 and 0002 there, runs repository-native restore
and the targeted LanguageServer build, packages a coherent win-x64 runtime, generates
provenance and machine-readable evidence, and performs ownership-checked cleanup.

This tooling is a reproducibility/promotion path. Production runtime selection and semantic
metadata parsing remain in normal CodeService source; semantic expected-value assertions
remain in the C# capability scenario rather than PowerShell.

## Final Service verification

After the generated v2 `ThirdParty/` tree is staged beside the final Service source, normal final
verification is:

```bat
dotnet build SystemExplorer.CodeService.slnx
dotnet pack SystemExplorer.CodeService.csproj -c Release
```

The pack target independently validates the pinned LanguageServer, Features and
LanguageServer.Protocol DLL hashes. Generated `bin/`, `obj/` and `nupkg/` are verification artifacts
and do not belong in the source delivery zip.


## Adopted V9 qualified-name receiver-recovery reproduction

The current retained V9 one-command builder is:

```text
Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.cmd
Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.ps1
README-V9-Builder-v3.txt
0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch
```

It consumes the verified V8 baseline (`Service.ThirdParty_V8.zip` SHA-256
`ad2c4801a8dc06b4d564e46436c2006af7f502e885b5e1df61f767c2c292129f`) and the corrected canonical 0009
SHA-256 `2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd`. The successful materialization
produced distribution `roslyn-3aeb96c9-systemexplorer-2b9ee3aff616-win-x64-v9`,
`Service.ThirdParty_V9.zip` SHA-256 `93fdbbcbbf384f14a72d7ab778dbfa43eb03431db76022aa1487137820572321`, and a full
`CompletionServiceTests` result of 24 passed / 0 failed / 0 skipped. The retained builder is reproducibility tooling;
the separate verified `Service.ThirdParty_V9.zip` remains the authoritative ThirdParty payload.

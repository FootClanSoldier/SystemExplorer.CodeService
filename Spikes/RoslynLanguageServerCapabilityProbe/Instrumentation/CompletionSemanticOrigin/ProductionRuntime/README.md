# Production completion semantic-origin runtime reproduction

Current materialized/adopted runtime: V12. Canonical 0012 method-shape metadata is built into distribution `roslyn-3aeb96c9-systemexplorer-322210505af7-win-x64-v12`; canonical `Service.ThirdParty_V12.zip` SHA-256 is `fbe44840bd389dc1a87d2e2e675add2407518d17dcef3b2cfa69d7155ef1567c`.

Use `Build-ProductionCompletionMethodShapeRuntime_v1.cmd` / `.ps1`, `README-V12-Builder-v1.txt`, and bundled canonical `0012-Expose-SystemExplorer-completion-method-parameter-shape.patch` to promote exact V11 to V12. The V12 builder consumes `Service.ThirdParty_V11.zip` SHA-256 `b36406247f23129a63c7092ea882c01106d2a3c5e5eefc458f7eb84b8d1cda4f`, re-verifies canonical 0001 -> 0011, and applies canonical 0012 SHA-256 `322210505af78564ed4a2ff4f86099abcbafcae304d35f4f417b64f3435205ed`.

For the retained historical V11 reproduction, use `Build-ProductionCompletionSourceExclusionRuntime_v1.cmd` / `.ps1`, `README-V11-Builder-v1.txt`, and canonical `0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch`. The materialized V11 runtime is external ThirdParty authority and is not embedded in the normal Service source zip.

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


## Adopted V10 import-completion path-exclusion reproduction

The current retained V10 one-command builder is:

```text
Build-ProductionImportCompletionPathExclusionRuntime_v1.cmd
Build-ProductionImportCompletionPathExclusionRuntime_v1.ps1
README-V10-Builder-v1.txt
0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch
```

It consumes the verified V9 baseline (`Service.ThirdParty_V9.zip` SHA-256
`93fdbbcbbf384f14a72d7ab778dbfa43eb03431db76022aa1487137820572321`) and canonical 0010
SHA-256 `6276ff5707ac41f47fab8e8298a226f2486d9ff2746ecec54568a82f1c7caaf6`. The successful materialization
produced distribution `roslyn-3aeb96c9-systemexplorer-6276ff5707ac-win-x64-v10`,
`Service.ThirdParty_V10.zip` SHA-256 `aae523641e34bf38543ef1f2a3acb9c30f1a6eceff42d02960a16832535caa3a`, and a full
`CompletionServiceTests` result of 24 passed / 0 failed / 0 skipped. The retained builder is reproducibility tooling;
the separate verified `Service.ThirdParty_V10.zip` remains the authoritative ThirdParty payload.


## Retained V11 completion-source exclusion reproduction

The retained V11 one-command builder is:

```text
Build-ProductionCompletionSourceExclusionRuntime_v1.cmd
Build-ProductionCompletionSourceExclusionRuntime_v1.ps1
README-V11-Builder-v1.txt
0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch
```

It consumes exact V10 baseline `Service.ThirdParty_V10.zip` SHA-256
`aae523641e34bf38543ef1f2a3acb9c30f1a6eceff42d02960a16832535caa3a`, re-verifies canonical `0001 -> 0010`,
and applies canonical 0011 SHA-256 `aeafddd7b52a8c1b44a455965e7c5d4b48b5e7e795291ea55f1f3bdc3d3ea054` to the
pinned upstream checkout. The successful materialization produced distribution
`roslyn-3aeb96c9-systemexplorer-aeafddd7b52a-win-x64-v11`, `Service.ThirdParty_V11.zip` SHA-256
`b36406247f23129a63c7092ea882c01106d2a3c5e5eefc458f7eb84b8d1cda4f`, and a full `CompletionServiceTests`
result of 24 passed / 0 failed / 0 skipped. The separate verified `Service.ThirdParty_V11.zip` remains historical V11
authority; current ThirdParty authority is the separately verified `Service.ThirdParty_V12.zip`. The retained V11
builder/patch copies here are reproducibility tooling only.

## Adopted V12 completion method-shape reproduction

The V12 source promotion kit is:

```text
Build-ProductionCompletionMethodShapeRuntime_v1.cmd
Build-ProductionCompletionMethodShapeRuntime_v1.ps1
README-V12-Builder-v1.txt
0012-Expose-SystemExplorer-completion-method-parameter-shape.patch
```

It consumes exact V11 `Service.ThirdParty_V11.zip` SHA-256 `b36406247f23129a63c7092ea882c01106d2a3c5e5eefc458f7eb84b8d1cda4f`, verifies V11 provenance plus canonical 0001 -> 0011, and applies canonical 0012 SHA-256 `322210505af78564ed4a2ff4f86099abcbafcae304d35f4f417b64f3435205ed`. The successful materialization produced distribution `roslyn-3aeb96c9-systemexplorer-322210505af7-win-x64-v12`, runtime archive SHA-256 `4bb8d44e335cb1ddf7f12874db7ccb9ae6eba05cb996e501087df5805f5b38c0`, and `Service.ThirdParty_V12.zip` SHA-256 `fbe44840bd389dc1a87d2e2e675add2407518d17dcef3b2cfa69d7155ef1567c`. Full C# CompletionServiceTests recorded 24 passed / 0 failed / 0 skipped. The actual DLL hashes emitted by that build are now adopted by `RoslynLanguageServerRuntime.cs` and `SystemExplorer.CodeService.csproj`.

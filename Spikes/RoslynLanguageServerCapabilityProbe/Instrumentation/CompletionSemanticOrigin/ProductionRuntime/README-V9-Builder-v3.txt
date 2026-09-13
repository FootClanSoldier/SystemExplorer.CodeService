SystemExplorer private Roslyn V9 one-click builder v3
=====================================================


V2 CORRECTION
=============
The superseded 0009 SHA 34d25abb... compiled far enough to expose CS0136 because the ordinary member-access branch and the later qualified-name recovery branch both declared receiverExpression in overlapping C# local scopes.

V3 fixes a CMD-only path-expansion bug in v2. The expression `%~dp0009-...` is parsed by cmd.exe as `%~dp0` followed by `009-...`, dropping one leading zero from the bundled patch filename. V3 first stores `%~dp0` in `SCRIPT_DIR`, then builds `PATCH_0009` from `%SCRIPT_DIR%0009-...`, so the canonical filename remains exactly `0009-...`. If the bundled patch is absent, the CMD falls back to `C:\Temp\buildpatch\0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch`; the PowerShell builder still fail-closes on the exact canonical SHA-256.

V3 carries corrected canonical 0009 SHA 2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd. The only semantic-neutral source correction is that the ordinary path local is named memberAccessReceiverExpression.

The canonical patch is bundled next to the CMD/PS1. The CMD uses the bundled patch by default, so extracting this package and starting the CMD is sufficient.
Purpose
-------
Build the next coherent SystemExplorer private Roslyn runtime from the verified V8
ThirdParty baseline plus canonical 0009 qualified-name receiver-recovery patch.

This kit is adapted directly from the current verified V8 builder:
  Build-ProductionTypeReceiverSemanticOriginRuntime_v2.cmd/.ps1

It retains the same detached-worktree, fail-closed verification, restore, regression-test,
LanguageServer build, packaging, provenance, evidence and cleanup model, while extending
canonical patch materialization from 0001-0008 to 0001-0009.

How to run
----------
Extract this build kit to any directory and double-click:

  Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.cmd

No arguments are required for the paths below.

Default inputs
--------------
Roslyn repository:
  C:\Temp\roslyn\

Verified V8 ThirdParty baseline:
  C:\Temp\Service.ThirdParty_V8.zip

Canonical 0009 patch:
  Bundled beside the CMD as:
  0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch

If the kit is extracted to C:\Temp\buildpatch\, this is exactly:
  C:\Temp\buildpatch\0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch

Persistent Roslyn .NET cache:
  C:\Temp\SECR4\cache\dotnet\

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Verified V8 baseline identity
-----------------------------
Service.ThirdParty_V8.zip SHA-256:
  ad2c4801a8dc06b4d564e46436c2006af7f502e885b5e1df61f767c2c292129f

Stable V8 distribution id expected in PROVENANCE.txt:
  roslyn-3aeb96c9-systemexplorer-df87da9cf8f7-win-x64-v8

Canonical patch chain retained byte-for-byte from V8
-----------------------------------------------------
0001 11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
0002 6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
0003 17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6
0004 39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec
0005 608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0
0006 8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524
0007 cd4f905c4b2b60cec000dcaad83241d18d151edb1aff625f4588effcc180fe3c
0008 df87da9cf8f7a02217e71341734ae892d653506838680a2768c1177782fbd400

Expected canonical 0009
-----------------------
Filename:
  0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch

SHA-256:
  2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd

Expected V9 distribution id after a successful real build
----------------------------------------------------------
  roslyn-3aeb96c9-systemexplorer-2b9ee3aff616-win-x64-v9

The builder derives this identity from the verified 0009 bytes at runtime. It is not
runtime/build evidence until the CMD completes successfully.

What the builder verifies before restore
----------------------------------------
- Windows host and git availability.
- The supplied Roslyn path is the repository root and contains the pinned commit.
- Pinned Roslyn License.txt and ThirdPartyNotices.rtf blobs.
- Exact V8 Service.ThirdParty.zip SHA-256.
- V8 PROVENANCE contains the pinned upstream commit, V8 distribution id and canonical
  0001-0008 identities.
- Canonical 0001-0008 are extracted directly from V8 and independently re-hashed.
- Exact bundled canonical 0009 SHA-256 and unified-diff hunk counts.
- 0009 touches exactly:
    src/Features/Core/Portable/Completion/Providers/SystemExplorerCompletionSemanticOrigin.cs
    src/EditorFeatures/CSharpTest/Completion/CompletionServiceTests.cs
- 0009 contains structural cross-line QualifiedName recovery, speculative
  BindAsExpression binding, common receiver-authority resolution and the three new
  parser-recovery regression tests.
- 0009 does not patch candidate-discovery helpers, AbstractSymbolCompletionProvider,
  import-completion production files, LanguageServer/wire files or PROVENANCE.
- A fresh detached worktree is created at the pinned upstream commit.
- Canonical 0001 -> 0008 are applied in order from the verified V8 baseline.
- 0009 is gated with:
    git apply --check --whitespace=error-all
  and then applied with whitespace errors treated as fatal.
- git diff --check passes after the full 0001 -> 0009 chain.
- Ordinary member-access resolution remains ahead of QualifiedName recovery.
- QualifiedName recovery is cross-line only and occurs before speculative binding.
- No production helper hard-codes the token text "var".
- Named-type/alias authority remains accepted before namespace/type fail-closed rejection.
- 0007 request-local receiver cache wiring remains present.
- 0008 named-type receiver tests remain present.
- Reduced-extension declaration authority, import completion and private wire/schema keys
  remain present.

Regression/build gates
----------------------
The V8 gate set is retained:
- 0005/0006 SystemExplorer import-completion/readiness + complex-edit protocol tests.
- 0003/0004 WithFrozenPartialSemanticsForCompletion workspace tests.
- Build of Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests net472 assembly.
- The full Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Completion.CompletionServiceTests
  class through Visual Studio TestPlatform.
- Release build of the coherent Microsoft.CodeAnalysis.LanguageServer payload.

The full CompletionServiceTests class therefore includes all existing 0007/0008 tests
plus the three 0009 recovery regressions. The builder reads CompletionServiceTests.trx
and fail-closes on zero execution, failures or inconsistent result counters.

Expected outputs
----------------
Outputs default under a unique directory below:
  C:\Temp\SECR9\o\

The exact output directory is printed in the console.

Expected files include:
  Service.ThirdParty_V9.zip
  roslyn-qualified-name-receiver-recovery-server.zip
  QualifiedNameReceiverRecoveryProductionRuntimeEvidence.json
  ServiceRuntimeAdoptionValues.txt
  CompletionServiceTestsResult.txt
  logs\bounded-build.log

Service.ThirdParty_V9.zip contains canonical 0001-0009, newly generated V9
PROVENANCE.txt, preserved redistribution license/notices, and the coherent built win-x64
Roslyn Language Server runtime.

Advanced invocation
-------------------
The CMD forwards explicit arguments when any are supplied. Supported parameters:

  -RoslynRepositoryRoot
  -CurrentServiceThirdPartyZip
  -QualifiedNameReceiverRecoveryPatch
  -WorkRoot
  -OutputRoot
  -DotNetCacheRoot
  -KeepArtifacts

Environment fallbacks:
  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
  SYSTEMEXPLORER_QUALIFIED_NAME_RECEIVER_RECOVERY_PATCH
  SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites
-------------
- Windows.
- git on PATH.
- The pinned Roslyn repository/commit available at C:\Temp\roslyn.
- Visual Studio TestPlatform/vstest.console.exe available for the net472 completion
  regression stage. The builder retains the verified V8 discovery path via VSINSTALLDIR,
  vswhere or PATH.
- Exact V8 ThirdParty and exact canonical 0009 patch bytes listed above.

Important
---------
Creating this build kit does NOT itself build or test Roslyn. Build/test/runtime success
may only be claimed from the console output and generated evidence after the CMD is run on
the Windows build machine.

The builder does not modify SystemExplorer.CodeService source, the Godot plugin, native
bootstrap code, or the supplied V8 archive. It materializes a new V9 ThirdParty/runtime
artifact separately.

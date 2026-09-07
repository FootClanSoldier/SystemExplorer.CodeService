SystemExplorer private Roslyn V6 one-click builder v1
=====================================================

Purpose
-------
Build the next coherent SystemExplorer private Roslyn runtime from the verified V5
ThirdParty baseline plus canonical 0006 import-completion semantic-readiness warm-up
patch.

This is the successor to the V5 one-click builder v1. It preserves the same
fail-closed build/verification model, reuses the established persistent Roslyn
.NET cache, applies the full canonical 0001 -> 0006 patch chain before restore,
and adds targeted verification for the new public textDocument/diagnostic-driven
completion-readiness path.

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Verified baseline input
-----------------------
Service.ThirdParty_V5.zip SHA-256:
7b079af666741532b5cbcfbfd12c081e743facd17efa7f09b8c869423954dee3

The builder extracts and independently verifies canonical 0001-0005 from that
archive before touching the Roslyn checkout.

Expected canonical 0006
-----------------------
Filename:
0006-Warm-SystemExplorer-import-completion-during-semantic-readiness.patch

SHA-256:
8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524

Default one-click inputs
------------------------
C:\Temp\roslyn
C:\Temp\Service.ThirdParty_V5.zip
C:\Temp\buildpatch\0006-Warm-SystemExplorer-import-completion-during-semantic-readiness.patch

Persistent .NET cache default
-----------------------------
C:\Temp\SECR4\cache\dotnet

Both the CMD wrapper and the PowerShell fallback intentionally reuse this cache.
The pinned Roslyn commit, SDK and runtime are unchanged from V4/V5. If the cache is
incomplete, the normal Roslyn bootstrap path may populate missing components there.

Transient V6 work/output roots
------------------------------
Runner-owned work/output state defaults under:
C:\Temp\SECR6

The final output directory is printed by the builder.

Key behavior
------------
- Requires Windows because the packaged production payload is win-x64.
- Verifies exact V5 Service.ThirdParty.zip SHA-256.
- Verifies V5 PROVENANCE contains the expected upstream commit, V5 distribution id,
  and canonical 0001/0002/0003/0004/0005 hashes.
- Verifies exact canonical 0006 SHA-256 before doing build work.
- Verifies 0006 unified-diff hunk counts before touching Roslyn.
- Rejects obvious scope regressions in 0006 such as DidOpenHandler modification,
  extension-method import warm-up, fake forced-expanded production behavior, or a
  direct CompletionHandler.GetCompletionListAsync warm-up.
- Creates a clean detached worktree at the pinned Roslyn commit.
- Runs the complete 0001 -> 0002 -> 0003 -> 0004 -> 0005 -> 0006
  git apply --check/apply chain BEFORE Restore.cmd.
- Runs git diff --check before restore.
- Performs static source checks for existing 0002/0003/0004/0005 contracts.
- Performs static 0006 checks for:
    * CompletionService.WarmUpForSystemExplorerImportCompletionAsync
    * imported/MEF provider preload
    * project provider preload trigger
    * awaitable ITypeImportCompletionService.WarmUpCacheAsync
    * forceCacheCreation:true reuse of the real type-import cache path
    * post-diagnostic hook placement before ClearSolutionContext
    * SystemExplorer capability gate
    * C# / tracked-document gate
    * real LSP completion options reuse
    * cancellation propagation and bounded non-cancellation failure handling
    * no fake GetCompletionsAsync request in production diagnostic code
    * new protocol regression test source.
- Verifies Restore.cmd does not mutate the patched source diff.
- Reuses C:\Temp\SECR4\cache\dotnet for the pinned SDK/runtime when present.
- Runs the SystemExplorer import-completion protocol regression group on net10.0,
  which includes the new:
    TestSystemExplorerImportCompletionWarmedByPublicDocumentDiagnostics
  together with existing 0005 import-completion tests and the complex-edit safety
  regression.
- Runs the existing WithFrozenPartialSemanticsForCompletion workspace regression set
  for 0003/0004.
- Builds and runs the CSharp CompletionServiceTests class through Visual Studio
  TestPlatform, preserving the V5 builder's net472 workaround.
- Builds one coherent Release Microsoft.CodeAnalysis.LanguageServer payload only
  after all targeted regression stages pass.
- Generates a real runtime archive, V6 ThirdParty package, provenance, evidence,
  adoption values and hashes from the actual built output.
- Fails closed on patch, restore-source-mutation, test, build, packaging, provenance
  or hash mismatch.
- Uses recursive bounded cleanup for runner-owned transient state. The shared
  C:\Temp\SECR4\cache\dotnet cache is explicitly outside runner ownership.

Expected output names
---------------------
Service.ThirdParty_V6.zip
roslyn-import-completion-readiness-server.zip
ImportCompletionReadinessProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
logs\bounded-build.log

Distribution identity
---------------------
The builder derives the V6 distribution id from the verified canonical 0006 SHA
prefix. For the approved patch bytes this is expected to be:

roslyn-3aeb96c9-systemexplorer-8dd66da05d85-win-x64-v6

Do not use that identity as runtime/build evidence unless this builder actually
completes and produces the corresponding package/provenance outputs.

How to run
----------
Put these three builder files together in one directory and double-click:

Build-ProductionImportCompletionReadinessRuntime_v1.cmd

No arguments are needed for the default paths listed above.

Advanced invocation
-------------------
The CMD forwards explicit arguments to the PowerShell builder when arguments are
provided. Supported parameters include:

-RoslynRepositoryRoot
-CurrentServiceThirdPartyZip
-ImportCompletionReadinessPatch
-WorkRoot
-OutputRoot
-DotNetCacheRoot
-KeepArtifacts

Environment fallbacks remain available for automation:
SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
SYSTEMEXPLORER_IMPORT_COMPLETION_READINESS_PATCH
SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites retained from the V5 builder
------------------------------------------
- git on PATH.
- The pinned Roslyn repository/commit available at C:\Temp\roslyn.
- Visual Studio TestPlatform/vstest.console.exe available for the net472 completion
  regression stage. The builder can discover it via VSINSTALLDIR, vswhere, or PATH.
- The V5 ThirdParty input and canonical 0006 patch must match the exact expected
  SHA-256 values above.

Important
---------
Generating this build kit does NOT build Roslyn. Build/test/runtime success must only
be claimed from the console output and evidence files produced when the CMD is run on
the Windows build machine.

The new 0006 mechanism is a performance/readiness experiment with production intent.
A successful build and unit-test run proves source/build integrity, not that first
interactive completion is faster. Runtime acceptance still requires fresh Roslyn
process tests in Godot/CodeService showing that cold completion work moves into the
existing semantic-readiness diagnostic chain and that the first real completion is
materially warm-path-like.

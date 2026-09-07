SystemExplorer private Roslyn V5 one-click builder v1
=====================================================

Purpose
-------
Build the next coherent SystemExplorer private Roslyn runtime from the verified V4
ThirdParty baseline plus canonical 0005 import-completion contract patch.

This is the successor to the V4 one-click builder v3. It preserves the same
fail-closed build/verification model and adds the 0005 LanguageServer protocol
regressions before runtime packaging.

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Verified baseline input
-----------------------
Service.ThirdParty_V4.zip SHA-256:
8d418b18c2228acf64a38a1282d2a1e3a029f057ee66ece0d20242f14d6b8112

The builder extracts and independently verifies canonical 0001-0004 from that
archive before touching the Roslyn checkout.

Expected canonical 0005
-----------------------
Filename:
0005-Enable-SystemExplorer-import-completion-with-vs-extensions.patch

SHA-256:
608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0

Default one-click inputs
------------------------
C:\Temp\roslyn
C:\Temp\Service.ThirdParty_V4.zip
C:\Temp\buildpatch\0005-Enable-SystemExplorer-import-completion-with-vs-extensions.patch

Persistent .NET cache default
-----------------------------
C:\Temp\SECR4\cache\dotnet

This intentionally reuses the already-established V4 builder cache because the
pinned Roslyn commit, SDK and runtime are unchanged. If the cache is absent, the
normal Roslyn bootstrap path may populate it.

Transient V5 work/output roots
------------------------------
Work/output state defaults under C:\Temp\SECR5.
The final output directory is printed by the builder.

Key behavior
------------
- Verifies exact V4 Service.ThirdParty.zip SHA-256.
- Verifies V4 PROVENANCE contains the expected upstream commit, V4 distribution id,
  and canonical 0001/0002/0003/0004 hashes.
- Verifies exact canonical 0005 SHA-256 before doing build work.
- Creates a clean detached worktree at the pinned Roslyn commit.
- Runs the complete 0001 -> 0002 -> 0003 -> 0004 -> 0005 git apply --check/apply
  chain BEFORE Restore.cmd.
- Runs git diff --check before restore.
- Performs static source checks for the existing 0002/0003/0004 contracts and the
  new 0005 capability, import discriminator, serializer support, declaration
  authority and regression tests.
- Verifies Restore.cmd does not mutate the patched source diff.
- Runs the new 0005 LanguageServer protocol tests on net10.0:
    * TestSystemExplorerImportCompletionDisabledByDefaultForVsExtensions
    * TestSystemExplorerImportCompletionWithVsExtensionsSourceType
    * TestSystemExplorerImportCompletionWithVsExtensionsMetadataType
    * TestDoNotProvideOverrideTextEditsOrInsertTextAsync
  These also exercise the existing 0002 semantic-origin wire contract for imported
  source/metadata types.
- Runs the existing WithFrozenPartialSemanticsForCompletion workspace regression set
  for 0003/0004.
- Builds and runs the CSharp CompletionServiceTests class through Visual Studio
  TestPlatform, preserving the V4 builder's net472 workaround.
- Builds one coherent Release Microsoft.CodeAnalysis.LanguageServer payload only
  after all targeted regression stages pass.
- Generates a real runtime archive, V5 ThirdParty package, provenance, evidence,
  adoption values and hashes from the actual built output.
- Fails closed on patch, restore-source-mutation, test, build, packaging, provenance
  or hash mismatch.
- Uses recursive bounded cleanup for the runner-owned RunRoot; this hardens the
  cleanup prompt/residue issue observed with the V4 builder.

Expected output names
---------------------
Service.ThirdParty_V5.zip
roslyn-import-completion-contract-server.zip
ImportCompletionContractProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
logs\bounded-build.log

Distribution identity
---------------------
The actual builder derives the V5 distribution id from the verified canonical 0005
SHA prefix. For the expected canonical patch this is:

roslyn-3aeb96c9-systemexplorer-608b4efa8b50-win-x64-v5

Do not use that identity as evidence unless this builder actually completes and
produces the corresponding runtime/provenance package.

How to run
----------
Put these three builder files together in one directory and double-click:

Build-ProductionImportCompletionContractRuntime_v1.cmd

No arguments are needed for the default paths above.

Advanced invocation
-------------------
The CMD forwards explicit arguments to the PowerShell builder when arguments are
provided. Supported parameters include:

-RoslynRepositoryRoot
-CurrentServiceThirdPartyZip
-ImportCompletionContractPatch
-WorkRoot
-OutputRoot
-DotNetCacheRoot
-KeepArtifacts

Environment fallbacks remain available for automation:
SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
SYSTEMEXPLORER_IMPORT_COMPLETION_CONTRACT_PATCH
SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Important
---------
The builder itself has not built Roslyn merely by being generated. Build/test/runtime
success must only be claimed from the console log and output evidence produced when
this CMD is actually run on the Windows machine with the pinned repository and inputs.

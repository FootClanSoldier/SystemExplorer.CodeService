SystemExplorer private Roslyn V8 one-click builder v2
=====================================================

v2 correction
-------------
The v1 build kit incorrectly required the literal `GetTypeInfo(` to occur inside the
canonical 0008 patch text. Canonical 0008 intentionally leaves the ordinary value-receiver
GetTypeInfo path unchanged from 0007, so that line is not part of the 0008 diff hunk.
v2 removes only that invalid pre-apply patch-text assertion. The stronger post-apply source
gate remains unchanged and still requires
`GetTypeInfo(receiverExpression, cancellationToken)` in the fully patched helper, and still
verifies the required ordering: named-type authority -> namespace/type rejection -> ordinary
value-receiver TypeInfo fallback. Canonical 0008 bytes and SHA-256 are unchanged.

Purpose
-------
Build the next coherent SystemExplorer private Roslyn runtime from the verified V7
ThirdParty baseline plus canonical 0008 named-type/type-receiver completion
semantic-origin patch.

This kit is adapted directly from the verified V7 one-click builder:
Build-ProductionReceiverRelativeSemanticOriginRuntime_v1.cmd/.ps1.

It preserves the same fail-closed detached-worktree, restore, regression-test,
packaging, provenance and cleanup model, extends the canonical chain to 0008, and
records the actual CompletionServiceTests total/passed/failed/skipped counts from the
generated TRX.

How to run
----------
Put the three files from this build kit together in one directory and double-click:

Build-ProductionTypeReceiverSemanticOriginRuntime_v2.cmd

No arguments are required for the owner's current paths.

Default inputs
--------------
Roslyn repository:
C:\Temp\roslyn\

Verified V7 ThirdParty baseline:
C:\Temp\Service.ThirdParty_V7.zip

Canonical 0008 patch:
C:\Temp\buildpatch\0008-Classify-static-member-completion-relative-to-type-receiver.patch

Persistent Roslyn .NET cache:
C:\Temp\SECR4\cache\dotnet\

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Verified V7 baseline identity
-----------------------------
Service.ThirdParty_V7.zip SHA-256:
dc00bf7b49b2a22783983238ca471a5cda8caf58cd6e82f55535707843896830

Stable V7 distribution id expected in PROVENANCE.txt:
roslyn-3aeb96c9-systemexplorer-cd4f905c4b2b-win-x64-v7

Canonical patch chain retained byte-for-byte from V7
-----------------------------------------------------
0001 11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
0002 6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
0003 17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6
0004 39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec
0005 608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0
0006 8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524
0007 cd4f905c4b2b60cec000dcaad83241d18d151edb1aff625f4588effcc180fe3c

Expected canonical 0008
-----------------------
Filename:
0008-Classify-static-member-completion-relative-to-type-receiver.patch

SHA-256:
df87da9cf8f7a02217e71341734ae892d653506838680a2768c1177782fbd400

Expected V8 distribution id after a successful real build
----------------------------------------------------------
roslyn-3aeb96c9-systemexplorer-df87da9cf8f7-win-x64-v8

The builder derives this identity from the verified canonical 0008 bytes at runtime.
It is not runtime/build evidence until the CMD completes successfully and produces
the corresponding output.

What the builder verifies before restore
----------------------------------------
- Windows host and git availability.
- The supplied Roslyn path is the repository root and contains the pinned commit.
- Pinned Roslyn License.txt and ThirdPartyNotices.rtf blobs.
- Exact V7 Service.ThirdParty.zip SHA-256.
- V7 PROVENANCE contains the pinned upstream commit, V7 distribution id and canonical
  0001-0007 identities.
- Canonical 0001-0007 are extracted directly from V7 and independently re-hashed.
- Exact external canonical 0008 SHA-256 and unified-diff hunk counts.
- 0008 touches exactly:
    src/Features/Core/Portable/Completion/Providers/SystemExplorerCompletionSemanticOrigin.cs
    src/EditorFeatures/CSharpTest/Completion/CompletionServiceTests.cs
- 0008 contains the named-type authority resolver and all four new regression tests.
- 0008 does not patch AbstractSymbolCompletionProvider, import-completion production
  files, LanguageServer/wire files, PROVENANCE or static candidate eligibility.
- A fresh detached worktree is created at the pinned upstream commit.
- Canonical 0001 -> 0007 are applied in order from the verified V7 baseline.
- 0008 is gated with:
    git apply --check --whitespace=error-all
  and then applied with whitespace errors treated as fatal.
- git diff --check passes after the full 0001 -> 0008 chain.
- Named-type authority acceptance occurs before the namespace/type fail-closed guard.
- Ordinary value-receiver GetTypeInfo fallback remains after that guard.
- 0007 request-local provider cache wiring remains present and unchanged.
- AttachEvidence(...), AttachDeclarationAuthority(...) and reduced-extension
  declaration authority remain present.
- TypeImportCompletionCacheEntry still uses AttachDeclarationAuthority(item, symbol).
- Existing 0005 import and 0006 readiness/warm-up contracts remain present.

Completion regression source gates
----------------------------------
The four unchanged 0007 regressions must remain present:
- CompletionSemanticOriginUsesExplicitReceiverTypeForMemberAccess
- CompletionSemanticOriginPreservesLexicalAnchorForThisMemberAccess
- CompletionSemanticOriginPreservesLexicalAnchorForUnqualifiedCompletion
- CompletionSemanticOriginUsesFullChainedReceiverTypeForMemberAccess

The four 0008 regressions must also be present:
- CompletionSemanticOriginUsesNamedTypeReceiverForStaticMemberAccess
- CompletionSemanticOriginUsesNamedTypeReceiverIndependentOfLexicalContainingType
- CompletionSemanticOriginUsesNamedTypeAliasReceiverForStaticMemberAccess
- CompletionSemanticOriginPreservesDeclarationAuthorityForNamespaceReceiver

Regression/build gates
----------------------
The complete V7 gate set is retained:
- 0005/0006 SystemExplorer import-completion/readiness + complex-edit protocol tests.
- 0003/0004 WithFrozenPartialSemanticsForCompletion workspace tests.
- Build of Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests net472 assembly.
- The full Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Completion.CompletionServiceTests
  class through Visual Studio TestPlatform.
- Release build of the coherent Microsoft.CodeAnalysis.LanguageServer payload.

The builder reads CompletionServiceTests.trx after the full-class run and fail-closes
if the test class did not execute, reports failures, or the total/passed/failed/skipped
counters do not form a clean partition.

Expected output names
---------------------
Service.ThirdParty_V8.zip
roslyn-type-receiver-semantic-origin-server.zip
TypeReceiverSemanticOriginProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
CompletionServiceTestsResult.txt
logs\bounded-build.log

Outputs default under a unique directory below:
C:\Temp\SECR8\o\

The exact output directory is printed in the console.

Service.ThirdParty_V8.zip contains canonical 0001-0008, a newly generated V8
PROVENANCE.txt, preserved redistribution license/notices, and the coherent built
win-x64 Roslyn Language Server runtime.

V8 provenance/evidence
----------------------
The generated V8 PROVENANCE.txt records:
- pinned upstream commit,
- canonical 0001-0008 identities,
- runtime archive identity,
- LanguageServer/Features/LanguageServer.Protocol DLL hashes,
- preserved redistribution material hashes.

The generated evidence JSON also records the actual CompletionServiceTests:
total, passed, failed and skipped.

Important CodeService adoption note
-----------------------------------
This builder does NOT modify SystemExplorer.CodeService and does not adopt V8.

After the successful real V8 build, runtime adoption remains a separate CodeService
patch. That later adoption should extend CompletionSemanticOriginScenario with at
least:
- TypeReceiverCurrentType
- TypeReceiverBaseType

and preferably a lexical-context-independence fixture where the receiver type and one
consumer share a base. Both consumer contexts must classify the same named-type
receiver members identically relative to the receiver type.

The existing reduced-extension expectation must remain declaration-authority based.

Advanced invocation
-------------------
The CMD forwards explicit arguments when any are supplied. Supported parameters:

-RoslynRepositoryRoot
-CurrentServiceThirdPartyZip
-TypeReceiverSemanticOriginPatch
-WorkRoot
-OutputRoot
-DotNetCacheRoot
-KeepArtifacts

Environment fallbacks:
SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
SYSTEMEXPLORER_TYPE_RECEIVER_SEMANTIC_ORIGIN_PATCH
SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites
-------------
- Windows.
- git on PATH.
- The pinned Roslyn repository/commit available at C:\Temp\roslyn.
- Visual Studio TestPlatform/vstest.console.exe available for the net472 completion
  regression stage. The builder retains the V7 discovery path via VSINSTALLDIR,
  vswhere or PATH.
- Exact V7 ThirdParty and exact canonical 0008 patch bytes listed above.

Important
---------
Generating this corrected v2 build kit does NOT itself build or test Roslyn. Build/test/runtime
success may only be claimed from the console output and generated evidence after you
run the CMD on the Windows build machine.

No V8 binary hashes, runtime archive hash/size, Service.ThirdParty_V8.zip hash or
PROVENANCE hash are pre-fabricated here.

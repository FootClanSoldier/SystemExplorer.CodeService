SystemExplorer private Roslyn V7 one-click builder v1
=====================================================

Purpose
-------
Build the next coherent SystemExplorer private Roslyn runtime from the verified V6
ThirdParty baseline plus canonical 0007 receiver-relative completion semantic-origin
patch.

This kit is adapted from the verified V6 one-click builder. It preserves the same
fail-closed detached-worktree, restore, regression-test, packaging, provenance and
cleanup model while extending the canonical chain to 0007.

Builder maintenance note
------------------------
This corrected kit wraps the 0007 touched-path Compare-Object result in an explicit
array before reading Count. Under Set-StrictMode -Version Latest, a perfect match makes
Compare-Object emit no objects; without @(...), reading .Count from that null result
terminates the builder before patch application. No Roslyn build, patch-chain, test,
packaging, provenance, or runtime contract is changed by this correction.

How to run
----------
Put the three files from this build kit together in one directory and double-click:

Build-ProductionReceiverRelativeSemanticOriginRuntime_v1.cmd

No arguments are required for the owner's current paths.

Default inputs
--------------
Roslyn repository:
C:\Temp\roslyn\

Verified V6 ThirdParty baseline:
C:\Temp\Service.ThirdParty_V6.zip

Canonical 0007 patch:
C:\Temp\buildpatch\0007-Classify-member-completion-relative-to-receiver-type.patch

Persistent Roslyn .NET cache:
C:\Temp\SECR4\cache\dotnet\

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Verified V6 baseline identity
-----------------------------
Service.ThirdParty_V6.zip SHA-256:
d3fa03c662af9d2f981b84ecfadae2d79115561ba88d48a813958b2c23e507bb

Stable V6 distribution id expected in PROVENANCE.txt:
roslyn-3aeb96c9-systemexplorer-8dd66da05d85-win-x64-v6

Canonical patch chain retained byte-for-byte from V6
-----------------------------------------------------
0001 11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
0002 6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
0003 17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6
0004 39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec
0005 608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0
0006 8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524

Expected canonical 0007
-----------------------
Filename:
0007-Classify-member-completion-relative-to-receiver-type.patch

SHA-256:
cd4f905c4b2b60cec000dcaad83241d18d151edb1aff625f4588effcc180fe3c

Expected V7 distribution id after a successful real build
----------------------------------------------------------
roslyn-3aeb96c9-systemexplorer-cd4f905c4b2b-win-x64-v7

This identity is derived from the approved 0007 bytes. It is not runtime/build evidence
until the CMD actually completes successfully and generates the corresponding output.

What the builder verifies before restore
----------------------------------------
- Windows host and git availability.
- The supplied Roslyn path is the repository root and contains the pinned commit.
- Pinned Roslyn License.txt and ThirdPartyNotices.rtf blobs.
- Exact V6 Service.ThirdParty.zip SHA-256.
- V6 PROVENANCE contains the pinned upstream commit, V6 distribution id and canonical
  0001-0006 identities.
- Canonical 0001-0006 are extracted directly from V6 and independently re-hashed.
- Exact external canonical 0007 SHA-256 and unified-diff hunk counts.
- 0007 touches exactly these three paths:
    src/Features/Core/Portable/Completion/Providers/AbstractSymbolCompletionProvider.cs
    src/Features/Core/Portable/Completion/Providers/SystemExplorerCompletionSemanticOrigin.cs
    src/EditorFeatures/CSharpTest/Completion/CompletionServiceTests.cs
- 0007 contains the receiver resolver/cache and all four required regression tests.
- 0007 does not patch LanguageServer, import-completion, diagnostics/warm-up or
  PROVENANCE source files.
- A fresh detached worktree is created at the pinned upstream commit.
- Canonical 0001 -> 0006 are applied in order.
- 0007 is gated with:
    git apply --check --whitespace=error-all
  and then applied with whitespace errors treated as fatal.
- git diff --check passes after the full 0001 -> 0007 chain.
- Receiver-resolution helper/provider wiring is present after apply.
- AttachEvidence(...) and AttachDeclarationAuthority(...) remain present.
- TypeImportCompletionCacheEntry still uses AttachDeclarationAuthority(item, symbol).
- Existing 0005 import and 0006 warm-up contracts remain present.

Regression/build gates
----------------------
The V6 gates are retained:
- 0005/0006 SystemExplorer import-completion/readiness + complex-edit protocol tests.
- 0003/0004 WithFrozenPartialSemanticsForCompletion workspace tests.
- Build of Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests net472 assembly.
- The full CompletionServiceTests class via Visual Studio TestPlatform.

Because all four 0007 tests live in CompletionServiceTests, that existing full-class gate
now also executes:
- CompletionSemanticOriginUsesExplicitReceiverTypeForMemberAccess
- CompletionSemanticOriginPreservesLexicalAnchorForThisMemberAccess
- CompletionSemanticOriginPreservesLexicalAnchorForUnqualifiedCompletion
- CompletionSemanticOriginUsesFullChainedReceiverTypeForMemberAccess

Only after those tests pass does the builder build and package the coherent Release
Microsoft.CodeAnalysis.LanguageServer payload.

Expected output names
---------------------
Service.ThirdParty_V7.zip
roslyn-receiver-relative-semantic-origin-server.zip
ReceiverRelativeSemanticOriginProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
logs\bounded-build.log

Outputs default under a unique directory below:
C:\Temp\SECR7\o\

The exact output directory is printed in the console.

Service.ThirdParty_V7.zip contains canonical 0001-0007, a newly generated V7
PROVENANCE.txt, preserved redistribution license/notices, and the coherent built
win-x64 Roslyn Language Server runtime.

Important CodeService adoption note
-----------------------------------
This builder does NOT modify SystemExplorer.CodeService and does not adopt V7.
After the real V7 build, CodeService runtime adoption is a separate patch.

At that later adoption step, the capability probe receiver case currently expecting:
other.ProbeOriginOtherUser... -> OtherUserCode
must be updated so a member declared directly on the receiver type expects:
CurrentType / 0

A reduced extension method in the same scenario must remain:
OtherUserCode

Do not make that CodeService probe change merely to build V7.

Advanced invocation
-------------------
The CMD forwards explicit arguments when any are supplied. Supported parameters:

-RoslynRepositoryRoot
-CurrentServiceThirdPartyZip
-ReceiverRelativeSemanticOriginPatch
-WorkRoot
-OutputRoot
-DotNetCacheRoot
-KeepArtifacts

Environment fallbacks:
SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
SYSTEMEXPLORER_RECEIVER_RELATIVE_SEMANTIC_ORIGIN_PATCH
SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites
-------------
- Windows.
- git on PATH.
- The pinned Roslyn repository/commit available at C:\Temp\roslyn.
- Visual Studio TestPlatform/vstest.console.exe available for the net472 completion
  regression stage. The builder retains the V6 discovery path via VSINSTALLDIR,
  vswhere or PATH.
- Exact V6 ThirdParty and exact canonical 0007 patch bytes listed above.

Important
---------
Generating this build kit does NOT itself build or test Roslyn. Build/test/runtime
success may only be claimed from the console output and generated evidence after you
run the CMD on the Windows build machine.

No V7 binary hashes, archive size or runtime PASS values are pre-fabricated here.

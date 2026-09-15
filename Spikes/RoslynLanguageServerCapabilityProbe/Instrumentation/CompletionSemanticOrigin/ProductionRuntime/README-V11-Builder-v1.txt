SystemExplorer private Roslyn V11 one-click builder v1 (build-kit revision 2)
=======================================================================

Build-kit revision 2 fixes only fail-closed source-shape assertions so they match the
final canonical 0011 implementation. Canonical 0011 bytes/SHA and intended Roslyn
source behavior are unchanged.

Purpose
-------
Reproduce the next coherent SystemExplorer private Roslyn Language Server runtime from
exactly the verified V10 ThirdParty baseline plus canonical patch 0011.

0011 changes the visibility model from the 0010 Type Import candidate filter to a
completion-specific source projection. Configured excluded ordinary source documents remain
in the full Roslyn workspace/project and therefore remain available to compiler diagnostics,
normal semantic authority, navigation, references and other non-completion features. They are
removed only from the immutable Solution/Compilation view used by SystemExplorer completion.

The completion projection is created before completion-specific frozen partial semantics and
before SemanticModel creation. Normal completion providers receive only the projected
Document/Project. Type Import completion also receives the projected project, so excluded
source is absent before namespace/type traversal and cache insertion rather than being
filtered after candidate discovery.

How to run
----------
Extract this build kit and double-click:

  Build-ProductionCompletionSourceExclusionRuntime_v1.cmd

The CMD uses the bundled canonical 0011 by default. Explicit arguments are forwarded to the
PowerShell builder unchanged.

Default inputs
--------------
Roslyn repository:
  C:\Temp\roslyn\

Exact V10 ThirdParty baseline:
  C:\Temp\Service.ThirdParty_V10.zip

Canonical 0011 patch bundled beside the scripts:
  0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch

Fallback patch location when the bundled file is absent:
  C:\Temp\buildpatch\0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch

Persistent Roslyn .NET cache:
  C:\Temp\SECR4\cache\dotnet\

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Exact V10 baseline identity
---------------------------
Service.ThirdParty_V10.zip SHA-256:
  aae523641e34bf38543ef1f2a3acb9c30f1a6eceff42d02960a16832535caa3a

Stable V10 distribution id expected in PROVENANCE.txt:
  roslyn-3aeb96c9-systemexplorer-6276ff5707ac-win-x64-v10

Canonical patch chain extracted and independently re-hashed from V10
---------------------------------------------------------------------
0001 11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
0002 6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
0003 17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6
0004 39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec
0005 608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0
0006 8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524
0007 cd4f905c4b2b60cec000dcaad83241d18d151edb1aff625f4588effcc180fe3c
0008 df87da9cf8f7a02217e71341734ae892d653506838680a2768c1177782fbd400
0009 2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd
0010 6276ff5707ac41f47fab8e8298a226f2486d9ff2746ecec54568a82f1c7caaf6

Canonical 0011
--------------
Filename:
  0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch

SHA-256:
  aeafddd7b52a8c1b44a455965e7c5d4b48b5e7e795291ea55f1f3bdc3d3ea054

Expected V11 distribution id after a successful real build
-----------------------------------------------------------
  roslyn-3aeb96c9-systemexplorer-aeafddd7b52a-win-x64-v11

The builder derives the V11 id from the verified canonical 0011 bytes. This README is
reproduction metadata, not evidence that a real V11 runtime has already been built.

Wire contract retained from 0010
--------------------------------
The private initialization property remains exactly:

  initialize.initializationOptions._systemExplorer_importCompletionExcludedPathPrefixes

Missing initializationOptions or a missing property means an empty exclusion set. An explicit
array must contain 1..16 non-empty absolute filesystem directory paths, each at most 4096
characters. InitializeManager performs Path.GetFullPath once, normalizes trailing separators,
deduplicates and deterministically sorts with OS-correct comparison. It performs no filesystem
existence checks. The existing local fully-qualified-path predicate remains in place for
netstandard2.0 compatibility; Path.IsPathFullyQualified is not used.

0011 completion-source architecture
-----------------------------------
- CompletionService owns the single immutable SystemExplorer completion-source exclusion
  snapshot and publishes replacements atomically.
- SystemExplorerCompletionSourcePathExclusions lives in Workspaces Core and classifies only
  Document.FilePath metadata. It contains no symbol/location traversal.
- Solution owns a completion-source-projection cache keyed by the exclusion-set Checksum.
- Projection scans ordinary source DocumentStates only via SolutionState/ProjectState metadata and
  removes matching DocumentIds through Solution.RemoveDocuments. AdditionalDocuments and AnalyzerConfigDocuments are not removed.
- A projected Solution is seeded with its own checksum so projection-of-projection is a no-op.
- CompletionService.GetCompletionsAsync obtains the projected request Document before
  WithFrozenPartialSemanticsForCompletion and before SemanticModel creation.
- If the request Document itself is excluded, completion returns CompletionList.Empty and does
  not fall back to the original full-project Document.
- Diagnostic readiness warm-up uses the same projected Project before provider preload and
  Type Import cache warm-up.
- ITypeImportCompletionService no longer owns path configuration.
- AbstractTypeImportCompletionService contains no source-symbol path filter. Its source cache
  still carries SystemExplorerCompletionSourceExclusionChecksum so transitions between
  exclusion sets cannot reuse stale source entries. Metadata entries continue to carry null.
- Individual completion providers remain path-blind.

What the builder verifies before restore
----------------------------------------
1. Windows host, git and a real Roslyn repository are available.
2. The repository contains pinned commit 3aeb96c9ecc56a5ee483558f9e648e33e7bfe756.
3. Exact V10 ThirdParty SHA-256 and V10 provenance identity match.
4. Canonical 0001-0010 are extracted from V10 and independently re-hashed.
5. Bundled canonical 0011 matches the exact SHA-256 above.
6. 0011 touches exactly the approved completion projection/LSP/test surface.
7. A fresh detached worktree is created at the pinned commit.
8. Canonical 0001 -> 0010 are applied in order from the verified V10 baseline.
9. 0011 is gated with git apply --check --whitespace=error-all and then applied with whitespace
   errors fatal.
10. git diff --check passes for the full 0001 -> 0011 chain.
11. Source-shape gates verify the completion-source projection/cache architecture, ordering
    before frozen semantics, single CompletionService config authority, retained Type Import
    cache checksum identity, deletion of the old symbol path filter, and provider path-blindness.
12. The established pinned SDK/runtime restore model is used.
13. Retained private regressions, all TestSystemExplorer... protocol tests, structural
    projection/cache tests, full C# CompletionServiceTests and the coherent Release Language
    Server build are executed.
14. Canonical 0001-0011, new V11 provenance/evidence/adoption values, preserved license/notices
    and the built runtime are materialized into Service.ThirdParty_V11.zip.

Regression/build gates
----------------------
Protocol filter:
  FullyQualifiedName~TestSystemExplorer

This captures retained 0005/0006/0010 import-completion regressions and the new 0011
completion-source-exclusion regressions.

Additional explicit gates include:
- TestDoNotProvideOverrideTextEditsOrInsertTextAsync.
- current-source/frozen-partial workspace regressions from 0003/0004.
- SystemExplorerCompletionSourceProjectionTests, including structural DocumentId/SyntaxTree
  exclusion, original-workspace preservation, and per-Solution projection-cache semantics.
- receiver-relative semantic-origin and qualified-name recovery regressions through the full
  C# CompletionServiceTests class.
- coherent Release build of Microsoft.CodeAnalysis.LanguageServer.

New 0011 protocol regressions include:
- TestSystemExplorerCompletionSourceExclusionQualifiedNamespaceAsync
- TestSystemExplorerCompletionSourceExclusionRootNamespaceAsync
- TestSystemExplorerCompletionSourceExclusionGlobalTypeAsync
- TestSystemExplorerCompletionSourceExclusionExplicitMemberReceiverAsync
- TestSystemExplorerCompletionSourceExclusionRequestDocumentItselfExcludedAsync
- TestSystemExplorerCompletionSourceExclusionKeepsMetadataCompletionAsync
- TestSystemExplorerCompletionSourceExclusionHonorsDirectoryBoundaryAsync
- TestSystemExplorerCompletionSourceExclusionMultiplePathsAsync

Expected outputs
----------------
Outputs default under a unique directory below:
  C:\Temp\SECR11\o\

Expected files include:
  Service.ThirdParty_V11.zip
  roslyn-completion-source-exclusion-server.zip
  CompletionSourceExclusionProductionRuntimeEvidence.json
  ServiceRuntimeAdoptionValues.txt
  CompletionServiceTestsResult.txt
  logs\bounded-build.log

Service.ThirdParty_V11.zip contains canonical 0001-0011, newly generated V11
PROVENANCE.txt, redistribution license/notices copied byte-for-byte from exact V10, and the
coherent built win-x64 Roslyn Language Server runtime.

Advanced invocation
-------------------
Supported PowerShell parameters:
  -RoslynRepositoryRoot
  -CurrentServiceThirdPartyZip
  -CompletionSourceExclusionPatch
  -WorkRoot
  -OutputRoot
  -DotNetCacheRoot
  -KeepArtifacts

Environment fallbacks:
  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
  SYSTEMEXPLORER_COMPLETION_SOURCE_EXCLUSION_PATCH
  SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites
-------------
- Windows.
- git on PATH.
- A local dotnet/roslyn checkout containing the pinned commit.
- Access to the same SDK/runtime acquisition model used by the established V10 builder.

Fail-closed behavior
--------------------
The builder stops on baseline, provenance, patch identity, patch shape, worktree, whitespace,
source-shape, restore, regression, build, runtime-layout or packaging mismatches. It does not
silently fall back to a different Roslyn commit, ThirdParty baseline, patch chain or runtime.

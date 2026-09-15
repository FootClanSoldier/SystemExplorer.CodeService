SystemExplorer private Roslyn V10 one-click builder v1
======================================================

Purpose
-------
Reproduce the next coherent SystemExplorer private Roslyn Language Server runtime from
exactly the verified V9 ThirdParty baseline plus canonical patch 0010, which excludes
configured absolute source-directory prefixes from the type import-completion cache only.

The patch does not remove documents from Roslyn projects or compilations. Excluded source
continues to participate in semantic models, diagnostics, symbol resolution, references,
definition/navigation and the normal workspace. Only source type import-completion cache
insertion is filtered.

How to run
----------
Extract this build kit and double-click:

  Build-ProductionImportCompletionPathExclusionRuntime_v1.cmd

The CMD uses the bundled canonical 0010 by default. Explicit arguments are forwarded to
the PowerShell builder unchanged.

Default inputs
--------------
Roslyn repository:
  C:\Temp\roslyn\

Exact V9 ThirdParty baseline:
  C:\Temp\Service.ThirdParty_V9.zip

Canonical 0010 patch bundled beside the scripts:
  0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch

Fallback patch location when the bundled file is absent:
  C:\Temp\buildpatch\0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch

Persistent Roslyn .NET cache:
  C:\Temp\SECR4\cache\dotnet\

Pinned source identity
----------------------
Repository: dotnet/roslyn
Commit:     3aeb96c9ecc56a5ee483558f9e648e33e7bfe756
SDK:        11.0.100-preview.6.26359.118
Runtime:    10.0.10

Exact V9 baseline identity
--------------------------
Service.ThirdParty_V9.zip SHA-256:
  93fdbbcbbf384f14a72d7ab778dbfa43eb03431db76022aa1487137820572321

Stable V9 distribution id expected in PROVENANCE.txt:
  roslyn-3aeb96c9-systemexplorer-2b9ee3aff616-win-x64-v9

Canonical patch chain extracted and independently re-hashed from V9
--------------------------------------------------------------------
0001 11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be
0002 6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb
0003 17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6
0004 39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec
0005 608b4efa8b50e85efbd9a7d6cf2bf0c31200809ee7d42e2c9b243a40858770b0
0006 8dd66da05d857ecb0737973c343f04a9d20bb7b4c35da5a010e9ca4109405524
0007 cd4f905c4b2b60cec000dcaad83241d18d151edb1aff625f4588effcc180fe3c
0008 df87da9cf8f7a02217e71341734ae892d653506838680a2768c1177782fbd400
0009 2b9ee3aff616702ac2b40a3fc1ba70eedb81c006d891f144b0580c3ba53b4ffd

Canonical 0010
--------------
Filename:
  0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch

SHA-256:
  6276ff5707ac41f47fab8e8298a226f2486d9ff2746ecec54568a82f1c7caaf6

Expected V10 distribution id after a successful real build
-----------------------------------------------------------
  roslyn-3aeb96c9-systemexplorer-6276ff5707ac-win-x64-v10

The builder derives the V10 id from the verified canonical 0010 bytes. This README is
reproduction metadata, not evidence that a real V10 runtime has already been built.


Netstandard2.0 compatibility hardening
--------------------------------------
InitializeManager does not call System.IO.Path.IsPathFullyQualified because that API is
not available when Microsoft.CodeAnalysis.LanguageServer.Protocol is compiled for
netstandard2.0. The 0010 implementation instead uses a local allocation-free fully-qualified
path predicate for Unix absolute paths, Windows drive-rooted paths, and UNC/device-rooted
paths, followed by the existing Path.GetFullPath normalization. Drive-relative/root-relative
Windows forms remain rejected. The builder source-shape gate explicitly rejects reintroducing
Path.IsPathFullyQualified.

Private initialization contract implemented by 0010
----------------------------------------------------
The LSP client may supply:

  initialize.initializationOptions._systemExplorer_importCompletionExcludedPathPrefixes

as an array of 1..16 absolute filesystem directory paths. Missing initializationOptions
or a missing property means an empty exclusion set and preserves previous behavior.

Configured paths are normalized once during initialize, deduplicated and deterministically
sorted with OS-correct path semantics. Windows comparison is OrdinalIgnoreCase; Unix is
Ordinal. The normalized set is immutable and its Roslyn Checksum participates in source
import-cache identity. Metadata import-completion cache semantics are unchanged.

A type is excluded only when it has at least one source location and every usable source
location is under one of the configured directory prefixes. Any source declaration lacking
an usable SourceTree.FilePath fails open for the whole type. Partial types with any source
declaration outside the excluded set remain available.

What the builder verifies before restore
----------------------------------------
1. Windows host, git and a real Roslyn repository are available.
2. The repository contains pinned commit 3aeb96c9ecc56a5ee483558f9e648e33e7bfe756.
3. Exact V9 ThirdParty SHA-256 and V9 provenance identity match.
4. Canonical 0001-0009 are extracted from V9 and independently re-hashed.
5. Bundled canonical 0010 matches the exact SHA-256 above.
6. 0010 touches only the approved Roslyn import-completion/LSP/test files and contains
   the expected initialize contract, bounded path-filter, cache identity and regressions.
7. A fresh detached worktree is created at the pinned commit.
8. Canonical 0001 -> 0009 are applied in order from the verified V9 baseline.
9. 0010 is gated with git apply --check --whitespace=error-all and then applied with
   whitespace errors fatal.
10. git diff --check passes for the full 0001 -> 0010 chain.
11. The existing pinned SDK/runtime restore model is used.
12. All retained V9 regression gates plus the 0010 gates are executed.
13. A coherent Release LanguageServer runtime is built.
14. Canonical 0001-0010, new provenance/evidence/adoption values, preserved license/notices
    and the built runtime are materialized into Service.ThirdParty_V10.zip.

Regression/build gates
----------------------
The V9 gates remain in place, including:
- exact protocol filter:
    FullyQualifiedName~TestSystemExplorerImportCompletion
  This captures the existing 0005/0006 regressions and all new 0010 regressions.
- TestDoNotProvideOverrideTextEditsOrInsertTextAsync.
- current-source/frozen-partial workspace regressions from 0003/0004.
- build and full execution of the C# CompletionServiceTests class.
- coherent Release build of Microsoft.CodeAnalysis.LanguageServer.

New 0010 protocol regressions include:
- TestSystemExplorerImportCompletionExcludesConfiguredPathAfterDiagnosticWarmUp
- TestSystemExplorerImportCompletionExcludesConfiguredPathWithoutDiagnosticWarmUp
- TestSystemExplorerImportCompletionExcludedPathHonorsDirectoryBoundary
- TestSystemExplorerImportCompletionKeepsPartialTypeWithIncludedDeclaration
- TestSystemExplorerImportCompletionExcludesMultipleConfiguredPaths

The existing SystemExplorer metadata import-completion regression remains in the same
FullyQualifiedName filter and continues to cover metadata candidates such as StringBuilder.

Expected outputs
----------------
Outputs default under a unique directory below:
  C:\Temp\SECR10\o\

Expected files include:
  Service.ThirdParty_V10.zip
  roslyn-import-completion-path-exclusion-server.zip
  ImportCompletionPathExclusionProductionRuntimeEvidence.json
  ServiceRuntimeAdoptionValues.txt
  CompletionServiceTestsResult.txt
  logs\bounded-build.log

Service.ThirdParty_V10.zip contains canonical 0001-0010, newly generated V10
PROVENANCE.txt, redistribution license/notices copied byte-for-byte from exact V9, and the
coherent built win-x64 Roslyn Language Server runtime.

Advanced invocation
-------------------
Supported PowerShell parameters:
  -RoslynRepositoryRoot
  -CurrentServiceThirdPartyZip
  -ImportCompletionPathExclusionPatch
  -WorkRoot
  -OutputRoot
  -DotNetCacheRoot
  -KeepArtifacts

Environment fallbacks:
  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
  SYSTEMEXPLORER_IMPORT_COMPLETION_PATH_EXCLUSION_PATCH
  SYSTEMEXPLORER_ROSLYN_DOTNET_CACHE_ROOT

Prerequisites
-------------
- Windows.
- git on PATH.
- A local dotnet/roslyn checkout containing the pinned commit.
- Access to the same SDK/runtime acquisition model used by the established V9 builder.

Fail-closed behavior
--------------------
The builder stops on baseline, provenance, patch identity, patch shape, worktree,
whitespace, restore, regression, build, runtime-layout or packaging mismatches. It does not
silently fall back to a different Roslyn commit, ThirdParty baseline, patch chain or runtime.

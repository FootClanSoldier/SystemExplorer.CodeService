SystemExplorer private Roslyn V14 same-label named-type completion-candidate production builder
===============================================================================================

Purpose
-------
This retained build kit promotes the exact verified V13 baseline to V14 with canonical 0014
same-label named-type completion-candidate preservation on top of canonical 0001 -> 0013.

The canonical V14 materialization has completed and its emitted values are now adopted by CodeService.
The builder remains reproduction/materialization infrastructure; adoption itself is owned by the Service source.

0014 is intentionally narrow. It changes ordinary symbol completion only for the established
SystemExplorer private completion path so namespace-distinct INamedTypeSymbol candidates that
share the same display/suffix/insertion text survive as separate completion items before item
materialization. Existing canonical 0013 remains the sole containing-namespace metadata authority.

The builder materializes Roslyn/ThirdParty V14 only. It does not perform CodeService adoption,
protocol/schema changes, plugin changes, local qualification/commit logic, or resolve-correlation
hardening.

Authoritative inputs
--------------------
Pinned upstream Roslyn commit:
3aeb96c9ecc56a5ee483558f9e648e33e7bfe756

Exact input archive:
Service.ThirdParty_V13.zip
SHA-256:
60b38e8d1a9cd6600c3f0eef1c3a9021d047a8c8d24223eb556d2d6ed7430fa0

Expected V13 distribution id:
roslyn-3aeb96c9-systemexplorer-23295a8179b4-win-x64-v13

Retained canonical V13 tail patch:
0013-Expose-SystemExplorer-completion-containing-namespace.patch
SHA-256:
23295a8179b4c02149ed2377a1d9d00ad623f986f7911b5551c90ffecba85a78

Canonical new patch:
0014-Preserve-SystemExplorer-same-label-named-type-completion-candidates.patch
SHA-256:
1dd7224435bc59ccb616804dc990e7af1cb438869e0cee1c4529b82140005cf6

Builder entry points
--------------------
Build-ProductionCompletionSameLabelNamedTypeCandidatesRuntime_v1.cmd
Build-ProductionCompletionSameLabelNamedTypeCandidatesRuntime_v1.ps1

The canonical 0014 patch is retained beside this README. The one-click .cmd defaults to:
  Roslyn checkout: C:\Temp\roslyn
  V13 input:       C:\Temp\Service.ThirdParty_V13.zip
  0014 patch:      C:\Temp\0014-Preserve-SystemExplorer-same-label-named-type-completion-candidates.patch
  shared SDK cache:C:\Temp\SECR4\cache\dotnet

An explicit -CompletionSameLabelNamedTypeCandidatesPatch parameter or
SYSTEMEXPLORER_COMPLETION_SAME_LABEL_NAMED_TYPE_CANDIDATES_PATCH environment variable may
override the patch path.

Requirements
------------
- Windows x64 build environment.
- A complete local dotnet/roslyn checkout containing the pinned commit above.
- The exact V13 ThirdParty archive above.
- Canonical 0014 with the exact SHA-256 above.
- Visual Studio TestPlatform / vstest.console.exe for the retained net472 CompletionServiceTests gate.
- The retained builder still derives build output values from an actual run rather than trusting predeclared
  generated artifacts. The adopted values below came from the successful canonical materialization.

0014 semantic contract
----------------------
The new private CompletionOptions flag is default-off and is enabled only when the already existing
SystemExplorer import-completion capability is active.

Within AbstractSymbolCompletionProvider.CreateItems:
  existing display/suffix/insertion grouping
      -> existing DeduplicateSymbols(...)
      -> optional SystemExplorer namespace split
      -> final noMerge calculation
      -> existing CreateAndAddItem(...)

A deduplicated symbol list may split only when:
- it contains more than one entry;
- every entry is INamedTypeSymbol;
- every containing namespace is non-global;
- at least two ordinally distinct containing-namespace display strings are present.

Entries with the same containing namespace remain grouped together. Methods, properties, fields,
events, locals, parameters, namespaces, aliases, mixed symbol kinds, global-namespace types,
nested-type collisions inside the same namespace and same-namespace assembly identity collisions
retain existing behavior.

Existing canonical 0013 remains responsible for:
  SystemExplorer.CompletionContainingNamespace
  _systemExplorer_completionContainingNamespace

No new wire property is introduced by 0014.

Fail-closed build flow
----------------------
1. Verify exact V13 Service.ThirdParty ZIP SHA-256.
2. Verify V13 PROVENANCE, pinned upstream commit and V13 distribution id.
3. Extract canonical 0001 -> 0013 and verify retained patch hashes/bytes.
4. Verify LICENSE and ThirdPartyNotices provenance against the pinned Roslyn commit.
5. Verify canonical 0014 exact SHA-256 and exact approved four-file touched surface.
6. Create a runner-owned detached worktree at the pinned upstream commit.
7. Apply canonical 0001 -> 0013.
8. Run git apply --check --whitespace=error-all for canonical 0014 and apply it.
9. Run git diff --check.
10. Run structural gates for default-off option, SystemExplorer capability gating, named-type-only
    namespace grouping, global-namespace fail-closed behavior and split-before-noMerge ordering.
11. Restore with the pinned repository toolchain without allowing restore to mutate patched source.
12. Run all retained/new TestSystemExplorer protocol regressions.
13. Explicitly run the three canonical 0014 regressions:
    - TestSystemExplorerPreservesSameLabelNamedTypeCandidatesAcrossContainingNamespacesAsync
    - TestSameLabelNamedTypeCandidatesPreserveUpstreamBehaviorWithoutSystemExplorerCapabilityAsync
    - TestSystemExplorerSameLabelCandidatePreservationDoesNotSplitMethodOverloadsAsync
14. Explicitly rerun canonical 0013 direct/import collision and unique/import metadata regressions.
15. Run retained complex-edit, frozen/current-source, incremental-reuse and completion-source gates.
16. Build and run the full C# CompletionServiceTests class through Visual Studio TestPlatform.
17. Build one coherent Release LanguageServer payload.
18. Materialize V14 outputs with canonical 0001 -> 0014 and generated V14 PROVENANCE.
19. Re-open generated Service.ThirdParty_V14.zip and verify retained/new patch and runtime DLL hashes.
20. Clean only runner-owned transient state unless -KeepArtifacts is supplied.

Expected output names
---------------------
Service.ThirdParty_V14.zip
roslyn-completion-same-label-named-type-candidates-server.zip
CompletionSameLabelNamedTypeCandidatesProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
CompletionServiceTestsResult.txt

V14 materialized/adopted identity
---------------------------------
Distribution id:
  roslyn-3aeb96c9-systemexplorer-1dd7224435bc-win-x64-v14

Service.ThirdParty_V14.zip SHA-256:
  08b68d7bb68f61c727c724e4d0a1fb350da70d979770bfda42e2c246d1489e64

Runtime archive SHA-256:
  1f6756306a4818a1a51fbdfc6a727a4544b701b5b5ca8dae3ef5b17a5418c305

Materialized DLL SHA-256 values:
  Microsoft.CodeAnalysis.LanguageServer.dll          d87f9bff2026a545dfc9a3986328ba1549dffbf02ca6fa3e14c8c75ba6f7714f
  Microsoft.CodeAnalysis.Features.dll                8e9004b65d694b9e97c2d8dc7d810b352c3b4177ca5aaeff99485a51c1071b80
  Microsoft.CodeAnalysis.LanguageServer.Protocol.dll 73682faae696f7eb576b310eee1e615933886f3c7bddde683f430604a203b886

Generated V14 PROVENANCE SHA-256:
  e35338a8fba5256826e1952173cf2396af0e3874dcdc079ebc5a9d028774e261

The materializing V14 build passed the retained/new SystemExplorer protocol regressions, the explicit
canonical 0014 same-label/upstream-gate/method-overload regression group, retained 0013 namespace
regressions, full C# CompletionServiceTests 24/24, and a coherent Release LanguageServer build.

Adoption status
---------------
The successful V14 materialization is now the current CodeService production runtime authority. Service
adoption pins the exact V14 distribution/DLL identities and records canonical 0014 in runtime diagnostics.
The existing schema-v6 Service pipeline remains unchanged: once Roslyn publishes separate same-label
ordinary named-type items with authoritative containing namespaces, existing publication-time namespace
disambiguation can preserve and expose them separately.

No CodeService resolve-hardening or plugin change is part of this V14 runtime adoption.

Do not check generated Roslyn build directories, temporary worktrees, SDK caches, logs, package
output, bin/, obj/, .vs/, runtime session files or other local artifacts into source control.

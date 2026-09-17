SystemExplorer private Roslyn V13 containing-namespace production builder
==========================================================================

Purpose
-------
This retained build kit promotes the exact verified V12 baseline to V13 with canonical 0013 containing-namespace semantic metadata on top of canonical 0001 -> 0012.

The canonical V13 materialization has completed and its emitted values are now adopted by CodeService. The builder remains reproduction/materialization infrastructure; it does not itself define Service schema or presentation policy, and it does not modify the Godot plugin.

Authoritative inputs
--------------------
Pinned upstream Roslyn commit:
3aeb96c9ecc56a5ee483558f9e648e33e7bfe756

Exact input archive:
Service.ThirdParty_V12.zip
SHA-256:
fbe44840bd389dc1a87d2e2e675add2407518d17dcef3b2cfa69d7155ef1567c

Expected V12 distribution id:
roslyn-3aeb96c9-systemexplorer-322210505af7-win-x64-v12

Retained canonical V12 tail patch:
0012-Expose-SystemExplorer-completion-method-parameter-shape.patch
SHA-256:
322210505af78564ed4a2ff4f86099abcbafcae304d35f4f417b64f3435205ed

Canonical new patch:
0013-Expose-SystemExplorer-completion-containing-namespace.patch
SHA-256:
23295a8179b4c02149ed2377a1d9d00ad623f986f7911b5551c90ffecba85a78

Builder entry points
--------------------
Build-ProductionCompletionContainingNamespaceRuntime_v1.cmd
Build-ProductionCompletionContainingNamespaceRuntime_v1.ps1

The canonical 0013 patch is retained beside this README. To follow the established one-click builder layout, the .cmd default still expects a copy at:
  C:\Temp\0013-Expose-SystemExplorer-completion-containing-namespace.patch

An explicit -CompletionContainingNamespacePatch parameter or SYSTEMEXPLORER_COMPLETION_CONTAINING_NAMESPACE_PATCH environment variable may override that path.

The .cmd defaults to:
  Roslyn checkout: C:\Temp\roslyn
  V12 input:       C:\Temp\Service.ThirdParty_V12.zip
  0013 patch:      C:\Temp\0013-Expose-SystemExplorer-completion-containing-namespace.patch
  shared SDK cache:C:\Temp\SECR4\cache\dotnet

Requirements
------------
- Windows x64 build environment.
- A complete local dotnet/roslyn checkout containing the pinned commit above.
- The exact V12 ThirdParty archive above.
- Canonical 0013 with the exact SHA-256 above.
- The retained builder still derives build output values from an actual run rather than trusting predeclared generated artifacts. The adopted values below came from the successful canonical materialization.

0013 semantic contract
----------------------
Roslyn is the semantic authority for containing namespace. The metadata is general, not conflict-dependent.

Normal grouped symbol completion:
- the represented symbol group must be non-empty;
- every represented symbol must be INamedTypeSymbol;
- every represented type must have the same non-global containing namespace;
- otherwise metadata is absent (fail closed).

Import type completion:
- ImportCompletionItem.Create already owns the authoritative containingNamespace string;
- eligible type items receive the same private property directly from that authority;
- extension-method import items do not receive type namespace metadata from this path;
- InlineDescription, sort/display text, generic suffix, complex-edit state and resolve behavior remain unchanged.

Private Roslyn CompletionItem property:
  SystemExplorer.CompletionContainingNamespace

Private LSP property:
  _systemExplorer_completionContainingNamespace

The wire value is a nullable JSON string. Null/absence means no namespace authority. Empty strings are never intentionally attached. Global namespace is not rendered as a synthetic name.

0013 does not detect duplicate labels or decide whether namespace text should be visible. Future CodeService adoption must preserve the raw semantic ContainingNamespace independently from publication-time namespace disambiguation/presentation.

Fail-closed build flow
----------------------
1. Verify exact V12 Service.ThirdParty ZIP SHA-256.
2. Verify V12 PROVENANCE and expected V12 distribution id.
3. Extract canonical 0001 -> 0012 and verify every retained patch byte-for-byte/hash-by-hash.
4. Verify LICENSE and ThirdPartyNotices bytes/provenance against the pinned Roslyn commit.
5. Verify canonical 0013 exact SHA-256 and exact approved seven-file touched surface.
6. Create a runner-owned detached worktree at the pinned upstream commit.
7. Apply canonical 0001 -> 0012.
8. Run `git apply --check --whitespace=error-all` for canonical 0013 and apply it.
9. Run `git diff --check`.
10. Run structural gates for grouped named-type authority, import type authority, nullable LSP projection and optimized string serialization; reject conflict/UI/Service logic in 0013.
11. Restore with the pinned repository toolchain without allowing restore to mutate patched source.
12. Run retained and new `TestSystemExplorer*` protocol regressions.
13. Run the containing-namespace direct/import collision regression explicitly.
14. Run non-conflicting direct/import/metadata namespace regressions explicitly.
15. Run retained complex-edit, frozen/current-source, incremental-reuse and completion-source projection regressions.
16. Build and run the full C# CompletionServiceTests class through Visual Studio TestPlatform.
17. Build one coherent Release LanguageServer payload.
18. Materialize V13 outputs, including canonical 0001 -> 0013 and generated V13 PROVENANCE.
19. Re-open the generated V13 ThirdParty ZIP, re-hash retained patches/runtime DLLs, and clean only runner-owned transient state.

V13 materialized/adopted identity
---------------------------------
Distribution id:
  roslyn-3aeb96c9-systemexplorer-23295a8179b4-win-x64-v13

Service.ThirdParty_V13.zip SHA-256:
  60b38e8d1a9cd6600c3f0eef1c3a9021d047a8c8d24223eb556d2d6ed7430fa0

Runtime archive SHA-256:
  1e01af18b11692fc3737f189dbe4468b358cc0174ad7ff063db8c282617a0f58

Materialized DLL SHA-256 values:
  Microsoft.CodeAnalysis.LanguageServer.dll          a44753b921f40623e97e0dbcc0805b4e52be6acce1c2ea9588009a1ddf5377bf
  Microsoft.CodeAnalysis.Features.dll                427f6328bd6ed55550f7daa365f7260709b499c501e4b4be741f98595a50c286
  Microsoft.CodeAnalysis.LanguageServer.Protocol.dll fa3e27d86ee868047d737beb092931d7347b257d4941edf7ae48e41d061d7646

Generated V13 PROVENANCE records the pinned upstream commit, canonical 0001 -> 0013 with each patch SHA, distribution id, runtime archive hash/size, materialized LanguageServer/Features/Protocol DLL hashes, and LICENSE/ThirdPartyNotices provenance.

Expected output names
---------------------
Service.ThirdParty_V13.zip
roslyn-completion-containing-namespace-server.zip
CompletionContainingNamespaceProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
CompletionServiceTestsResult.txt

Adoption status
---------------
The successful V13 materialization is now the current CodeService production runtime authority. Service adoption pins the exact V13 distribution/DLL identities, records canonical 0013 in runtime diagnostics, strictly preserves raw containing-namespace metadata, and exposes publication-derived namespace disambiguation through completion schema v6.

The Godot plugin remains a separate adoption step and must be updated from its own current source-of-truth project before schema-v6 completion is usable end-to-end.

Do not check generated Roslyn build directories, temporary worktrees, SDK caches, logs, package output, bin/, obj/, .vs/, runtime session files or other local artifacts into the source delivery.

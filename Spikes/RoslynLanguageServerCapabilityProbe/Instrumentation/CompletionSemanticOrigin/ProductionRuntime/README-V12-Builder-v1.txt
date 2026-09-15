SystemExplorer private Roslyn V12 completion method-shape production builder
============================================================================

Purpose
-------
This retained one-click build kit promotes the exact verified V11 private Roslyn baseline to V12 by applying only canonical 0012 method-parameter-shape metadata on top of canonical 0001 -> 0011.

Authoritative inputs
--------------------
Pinned upstream Roslyn commit:
3aeb96c9ecc56a5ee483558f9e648e33e7bfe756

Exact input archive:
Service.ThirdParty_V11.zip
SHA-256:
b36406247f23129a63c7092ea882c01106d2a3c5e5eefc458f7eb84b8d1cda4f

Expected V11 distribution id:
roslyn-3aeb96c9-systemexplorer-aeafddd7b52a-win-x64-v11

Canonical new patch:
0012-Expose-SystemExplorer-completion-method-parameter-shape.patch
SHA-256:
322210505af78564ed4a2ff4f86099abcbafcae304d35f4f417b64f3435205ed

Builder entry points
--------------------
Build-ProductionCompletionMethodShapeRuntime_v1.cmd
Build-ProductionCompletionMethodShapeRuntime_v1.ps1

The builder files may remain in the CodeService project tree. The canonical 0012 patch does not need to be copied beside them; the default one-click location is C:\Temp\0012-Expose-SystemExplorer-completion-method-parameter-shape.patch. An explicit -CompletionMethodShapePatch parameter or SYSTEMEXPLORER_COMPLETION_METHOD_SHAPE_PATCH environment variable may still override that default.

The .cmd defaults to:
  Roslyn checkout: C:\Temp\roslyn
  V11 input:       C:\Temp\Service.ThirdParty_V11.zip
  0012 patch:      C:\Temp\0012-Expose-SystemExplorer-completion-method-parameter-shape.patch
  shared SDK cache:C:\Temp\SECR4\cache\dotnet

Requirements
------------
- Windows x64 build environment.
- A complete local dotnet/roslyn checkout containing the pinned commit above.
- The exact V11 ThirdParty archive above.
- No build-derived V12 distribution or DLL hash is predeclared. Those values are accepted only from this builder's actual materialized output.

Fail-closed build flow
----------------------
1. Verify the exact V11 input archive SHA-256 and V11 PROVENANCE/distribution identity.
2. Extract canonical 0001 -> 0011, re-hash every retained patch, and preserve LICENSE/ThirdPartyNotices bytes.
3. Verify canonical 0012 bytes/hash and its approved metadata-only touched-file/content surface.
4. Create a runner-owned detached worktree at the pinned upstream commit.
5. Apply canonical 0001 -> 0011, then run `git apply --check --whitespace=error-all` for 0012 and apply 0012.
6. Run `git diff --check` and structural source gates for the 0012 nullable-bool/false-serialization contract.
7. Restore with the pinned repository toolchain without allowing restore to mutate the patched source.
8. Run retained plus new `TestSystemExplorer*` protocol regressions, retained complex-edit, frozen/current-source, incremental-reuse and completion-source-projection gates.
9. Run the full C# CompletionServiceTests class.
10. Build one coherent Release LanguageServer payload.
11. Materialize `Service.ThirdParty_V12.zip`, runtime archive, PROVENANCE, evidence JSON and ServiceRuntimeAdoptionValues.txt.
12. Re-open/re-hash the generated archive and clean only runner-owned transient state.

0012 contract
-------------
Roslyn remains the only method-shape semantic authority. A grouped completion item gets private method-shape metadata only when the complete represented symbol group is non-empty and all symbols are IMethodSymbol. false means every represented method has zero visible parameters; true means at least one represented overload has one or more visible parameters. The optimized VS completion-list path explicitly serializes both false and true. 0012 does not change labels, insertion text, filter/sort/preselect, ordering, snippets, resolve or commit behavior.

Promotion/adoption
------------------
The successful canonical materialization emitted the values now adopted by `Roslyn/RoslynLanguageServerRuntime.cs` and `SystemExplorer.CodeService.csproj`. For any future reproduction, only values emitted by that new materialization may replace those pins; verify the final CodeService source against its generated V12 ThirdParty tree before promotion.

Expected output names
---------------------
Service.ThirdParty_V12.zip
roslyn-completion-method-shape-server.zip
CompletionMethodShapeProductionRuntimeEvidence.json
ServiceRuntimeAdoptionValues.txt
CompletionServiceTestsResult.txt

Do not check generated Roslyn build directories, temporary worktrees, logs, caches, bin/obj/nupkg or other local artifacts into the source delivery.

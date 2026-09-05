SystemExplorer private Roslyn V3 one-click builder
=================================================

Drop these three files into:

C:\Users\p_sha\source\repos\SystemExplorer.CodeService\Spikes\RoslynLanguageServerCapabilityProbe\Instrumentation\CompletionSemanticOrigin\ProductionRuntime

Files:
  Build-ProductionCurrentSourceFrozenPartialRuntime.cmd
  Build-ProductionCurrentSourceFrozenPartialRuntime.ps1
  README-V3-Builder.txt

The CMD is preconfigured for the current local paths:
  Roslyn repo:      C:\Temp\roslyn
  V2 ThirdParty:    C:\Temp\Service.ThirdParty_V2.zip
  canonical 0003:  C:\Temp\buildpatch\0003-Preserve-current-source-for-frozen-partial-completion.patch

Expected immutable inputs:
  upstream commit:
    3aeb96c9ecc56a5ee483558f9e648e33e7bfe756

  Service.ThirdParty_V2.zip SHA-256:
    45f152e900326520626b5f17248fdf608d7a7e61f01da42b480dce138f5453d8

  canonical 0001 SHA-256:
    11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be

  canonical 0002 SHA-256:
    6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb

  canonical 0003 SHA-256:
    17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6

Normal use:
  Double-click Build-ProductionCurrentSourceFrozenPartialRuntime.cmd

The builder:
  1. validates the pinned Roslyn repository commit and redistribution blobs;
  2. validates the exact V2 ThirdParty zip and its V2 provenance;
  3. extracts canonical 0001 + 0002 from V2 ThirdParty;
  4. validates the external canonical 0003;
  5. creates a runner-owned detached worktree (the owner checkout is not modified);
  6. runs Roslyn Restore.cmd with dotnet MSBuild and node reuse disabled;
  7. verifies the pinned repository-local .NET SDK;
  8. applies 0001 -> 0002 -> 0003 and runs git diff --check;
  9. runs the new workspace regression test and the CompletionServiceTests class;
 10. builds the LanguageServer Release payload with build servers/shared compilation disabled;
 11. packages Service.ThirdParty_V3.zip with canonical 0001/0002/0003, runtime, licenses and provenance;
 12. writes CurrentSourceFrozenPartialProductionRuntimeEvidence.json;
 13. writes ServiceRuntimeAdoptionValues.txt containing the exact DistributionId and the three DLL hashes that must replace the fail-closed Service placeholders.

Default work/output roots are deliberately short to keep .NET Framework/xUnit below legacy path limits:
  work:   C:\Temp\SECR3\w\r-...
  output: C:\Temp\SECR3\o\o-...

The current Service source zip is intentionally NOT consumed by this runtime builder. After a successful V3 build, use ServiceRuntimeAdoptionValues.txt to adopt the generated runtime into:
  Service.completion_current_source_frozen_partial_v1.zip

The one-click CMD pauses at the end so the final output paths or failure message remain visible.

The CMD wrapper propagates the PowerShell exit code and must report a non-zero build as failed.

VSTEST FIX:
- net472 EditorFeatures tests are built with the pinned Roslyn SDK but run with Visual Studio vstest.console.exe.
- Visual Studio is discovered through VSINSTALLDIR/vswhere/PATH.
- xunit.runner.visualstudio adapter is resolved from the test project's exact project.assets.json.
- This bypasses the pinned .NET 11 Preview 6 SDK TestHostNetFramework packaging/probing failure.

Builder fix: completion test project.assets.json is resolved from Roslyn's Arcade artifacts/obj layout (with a project-local obj fallback) and must uniquely contain xunit.runner.visualstudio.

PATH-LENGTH FIX:
- The runner-owned Roslyn worktree now lives under C:\Temp\SECR3 rather than the long user TEMP hierarchy.
- Run IDs and internal work directories are shortened while ownership-marker cleanup remains fail-closed.
- Visual Studio VSTest receives a short per-run TEMP/TMP directory during net472 execution to keep xUnit/TestPlatform shadow-copy paths short.
- No Roslyn source, patch, runtime identity, or test acceptance criteria are relaxed by this workaround.

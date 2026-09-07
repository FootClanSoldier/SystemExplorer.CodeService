SystemExplorer private Roslyn V4 one-click builder v3
=====================================================

This builder is the v2 hardened builder updated for the corrected canonical 0004 patch.

Key behavior:
- Verifies the V3 Service.ThirdParty.zip and canonical 0001/0002/0003 hashes.
- Verifies corrected canonical 0004 SHA-256 before doing work.
- Creates a clean detached worktree at pinned Roslyn commit 3aeb96c9ecc56a5ee483558f9e648e33e7bfe756.
- Runs the complete 0001 -> 0002 -> 0003 -> 0004 git apply --check/apply chain BEFORE Restore.cmd.
- Reuses a persistent Roslyn bootstrap .NET cache under C:\Temp\SECR4\cache\dotnet by default.
- Only after the patch chain passes does it restore, run targeted regression tests, build the Language Server, materialize V4, and generate real provenance/hashes.
- Fails closed on patch, test, build, packaging, provenance, or hash mismatch.

Expected corrected 0004 SHA-256:
39d4217634dcf32304e071b1d8e01fa42778461ca903b07544490e451807f6ec

Default inputs from the CMD wrapper:
C:\Temp\roslyn
C:\Temp\Service.ThirdParty_V3.zip
C:\Temp\buildpatch\0004-Preserve-incremental-reuse-for-current-source-completion.patch



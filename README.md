# SystemExplorer.CodeService

SystemExplorer.CodeService is the standalone code-intelligence backend for the System Explorer Godot editor plugin. It runs outside the Godot process so workspace, indexing, and Roslyn-host lifetime can remain independent of editor/plugin reloads.

## Requirements and installation

SystemExplorer.CodeService targets .NET 10 and is distributed as a .NET Tool:

```text
dotnet tool install --global SystemExplorer.CodeService
```

The installed command is:

```text
system-explorer-code
```

The service is normally started and owned by the System Explorer plugin. Server mode validates the exact Godot owner process identity and retires when that owner lifetime ends.

## Private Roslyn runtime

The win-x64 tool package includes the SystemExplorer private patched Roslyn Language Server runtime. Normal users on Windows x64 do not need to install Roslyn separately or pass a Roslyn path.

`--roslyn-runtime <absolute-directory>` remains an explicit development/test override for controlled fixed-runtime verification. Packaged Roslyn provisioning in this version is supported only on Windows x64; on unsupported platforms the service retains its indexing-only behavior.

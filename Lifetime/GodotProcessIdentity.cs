namespace SystemExplorer.CodeService;

internal readonly record struct GodotProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks);

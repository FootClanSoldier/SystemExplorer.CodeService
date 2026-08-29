namespace SystemExplorer.CodeService;

internal readonly record struct DiagnosticResourceSnapshot(
    long? WorkingSetBytes,
    long? ManagedMemoryBytes)
{
    public static DiagnosticResourceSnapshot CaptureIfEnabled(DiagnosticLogging diagnosticLogging)
    {
        ArgumentNullException.ThrowIfNull(diagnosticLogging);

        if (!diagnosticLogging.IsEnabled)
        {
            return default;
        }

        long? workingSetBytes = null;
        long? managedMemoryBytes = null;

        try
        {
            long value = Environment.WorkingSet;
            if (value >= 0)
            {
                workingSetBytes = value;
            }
        }
        catch
        {
            // Resource telemetry is best-effort and never service authority.
        }

        try
        {
            long value = GC.GetTotalMemory(forceFullCollection: false);
            if (value >= 0)
            {
                managedMemoryBytes = value;
            }
        }
        catch
        {
            // Resource telemetry is best-effort and never service authority.
        }

        return new DiagnosticResourceSnapshot(workingSetBytes, managedMemoryBytes);
    }
}

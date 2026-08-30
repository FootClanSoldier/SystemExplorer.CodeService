using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;

internal sealed record RoslynStateTraceProvenance(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("instrumentationVersion")] int InstrumentationVersion,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("baseCommit")] string BaseCommit,
    [property: JsonPropertyName("serverCommandPath")] string ServerCommandPath,
    [property: JsonPropertyName("targetFileName")] string TargetFileName)
{
    public static async Task<RoslynStateTraceProvenance> LoadAndVerifyAsync(
        string provenancePath,
        string expectedServerCommandPath,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(provenancePath) || !File.Exists(provenancePath))
            throw new InvalidDataException("State trace provenance path must be an existing absolute file path.");
        if (!Path.IsPathFullyQualified(expectedServerCommandPath) || !File.Exists(expectedServerCommandPath))
            throw new InvalidDataException("State trace server path must be an existing absolute file path.");

        await using FileStream stream = File.OpenRead(Path.GetFullPath(provenancePath));
        RoslynStateTraceProvenance? provenance = await JsonSerializer.DeserializeAsync<RoslynStateTraceProvenance>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (provenance is null)
            throw new InvalidDataException("State trace provenance file did not contain the required object.");

        if (provenance.SchemaVersion != 1)
            throw new InvalidDataException($"Unexpected state trace provenance schemaVersion: {provenance.SchemaVersion}");
        if (provenance.InstrumentationVersion != ProbeConstants.RoslynStateTraceVersion)
            throw new InvalidDataException($"Unexpected state trace instrumentationVersion: {provenance.InstrumentationVersion}");
        if (!string.Equals(provenance.Repository, "dotnet/roslyn", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected state trace repository: {provenance.Repository}");
        if (!string.Equals(provenance.BaseCommit, ProbeConstants.RoslynSourceCommit, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected state trace baseCommit: {provenance.BaseCommit}");
        if (!string.Equals(provenance.TargetFileName, "ProbeTarget.cs", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected state trace targetFileName: {provenance.TargetFileName}");
        if (!Path.IsPathFullyQualified(provenance.ServerCommandPath))
            throw new InvalidDataException("State trace provenance serverCommandPath was not absolute.");

        string actualServerPath = Path.GetFullPath(provenance.ServerCommandPath);
        string expectedServerPath = Path.GetFullPath(expectedServerCommandPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(actualServerPath, expectedServerPath, pathComparison))
        {
            throw new InvalidDataException(
                $"State trace provenance serverCommandPath mismatch: expected={expectedServerPath}; actual={actualServerPath}");
        }

        return provenance with { ServerCommandPath = actualServerPath };
    }
}

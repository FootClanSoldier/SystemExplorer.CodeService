using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;

internal sealed record CompletionSemanticOriginProvenance(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("instrumentationVersion")] int InstrumentationVersion,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("baseCommit")] string BaseCommit,
    [property: JsonPropertyName("baselineDistributionId")] string BaselineDistributionId,
    [property: JsonPropertyName("canonicalSystemExplorerPatchSha256")] string CanonicalSystemExplorerPatchSha256,
    [property: JsonPropertyName("serverCommandPath")] string ServerCommandPath,
    [property: JsonPropertyName("semanticOriginJsonPropertyName")] string SemanticOriginJsonPropertyName,
    [property: JsonPropertyName("inheritanceDepthJsonPropertyName")] string InheritanceDepthJsonPropertyName)
{
    public static async Task<CompletionSemanticOriginProvenance> LoadAndVerifyAsync(
        string provenancePath,
        string expectedServerCommandPath,
        CancellationToken cancellationToken)
    {
        string normalizedProvenancePath = RequireExistingAbsoluteFile(provenancePath, "Semantic-origin provenance");
        string normalizedExpectedServerPath = RequireExistingAbsoluteFile(expectedServerCommandPath, "Semantic-origin server");

        await using FileStream stream = File.OpenRead(normalizedProvenancePath);
        CompletionSemanticOriginProvenance? provenance = await JsonSerializer.DeserializeAsync<CompletionSemanticOriginProvenance>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (provenance is null)
            throw new InvalidDataException("Semantic-origin provenance file did not contain the required object.");

        if (provenance.SchemaVersion != 1
            || provenance.InstrumentationVersion != ProbeConstants.CompletionSemanticOriginInstrumentationVersion
            || !string.Equals(provenance.Repository, "dotnet/roslyn", StringComparison.Ordinal)
            || !string.Equals(provenance.BaseCommit, ProbeConstants.RoslynSourceCommit, StringComparison.Ordinal)
            || !string.Equals(provenance.BaselineDistributionId, ProbeConstants.RoslynBaselineDistributionId, StringComparison.Ordinal)
            || !string.Equals(provenance.CanonicalSystemExplorerPatchSha256, ProbeConstants.CanonicalSystemExplorerPatchSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(provenance.SemanticOriginJsonPropertyName, ProbeConstants.CompletionSemanticOriginJsonPropertyName, StringComparison.Ordinal)
            || !string.Equals(provenance.InheritanceDepthJsonPropertyName, ProbeConstants.CompletionInheritanceDepthJsonPropertyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic-origin provenance did not match the pinned SystemExplorer instrumentation contract.");
        }

        if (!Path.IsPathFullyQualified(provenance.ServerCommandPath))
            throw new InvalidDataException("Semantic-origin provenance serverCommandPath was not absolute.");
        string actualServerPath = Path.GetFullPath(provenance.ServerCommandPath);
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(actualServerPath, normalizedExpectedServerPath))
        {
            throw new InvalidDataException(
                $"Semantic-origin provenance serverCommandPath mismatch: expected={normalizedExpectedServerPath}; actual={actualServerPath}");
        }

        return provenance with { ServerCommandPath = actualServerPath };
    }

    private static string RequireExistingAbsoluteFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"{description} path must be absolute.");
        string normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
            throw new InvalidDataException($"{description} path does not exist: {normalized}");
        return normalized;
    }
}

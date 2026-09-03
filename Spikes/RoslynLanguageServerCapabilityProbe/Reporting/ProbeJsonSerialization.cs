using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal static class ProbeJsonSerialization
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

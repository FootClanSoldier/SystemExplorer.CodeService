using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal static class ProbeReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<string> WriteAsync(
        ProbeReport report,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        string path = options.ReportPath ?? GetDefaultReportPath(report.StartedAtUtc);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string GetDefaultReportPath(DateTimeOffset startedAtUtc)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "SystemExplorer.CodeService",
            "RoslynProbe");
        return Path.Combine(directory, $"roslyn_probe_{startedAtUtc:yyyyMMdd_HHmmss_fff}.json");
    }
}

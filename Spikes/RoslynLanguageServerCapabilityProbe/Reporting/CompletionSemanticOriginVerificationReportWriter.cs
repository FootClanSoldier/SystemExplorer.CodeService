using System.Text.Json;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal static class CompletionSemanticOriginVerificationReportWriter
{
    public static async Task<string> WriteAsync(
        CompletionSemanticOriginVerificationReport report,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        string path = options.ReportPath ?? GetDefaultReportPath(report.StartedAtUtc);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, report, ProbeJsonSerialization.Options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string GetDefaultReportPath(DateTimeOffset startedAtUtc)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "SystemExplorer.CodeService",
            "RoslynProbe");
        return Path.Combine(directory, $"completion_semantic_origin_{startedAtUtc:yyyyMMdd_HHmmss_fff}.json");
    }
}

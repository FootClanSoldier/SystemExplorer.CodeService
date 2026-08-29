using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed class BootstrapReadinessWriter
{
    private const int ReadinessSchemaVersion = 1;
    private const string ReadinessRecordType = "codeservice.ready";

    private readonly BootstrapReadinessRecord _record;
    private int _attemptState;

    public BootstrapReadinessWriter(
        SessionProtocolContext protocolContext,
        string descriptorPath)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
        {
            throw new ArgumentException("descriptor path is required.", nameof(descriptorPath));
        }

        _record = new BootstrapReadinessRecord(
            ReadinessSchemaVersion,
            ReadinessRecordType,
            protocolContext.ProtocolVersion,
            protocolContext.ServiceVersion,
            protocolContext.SessionIdentity.SessionId,
            protocolContext.GodotOwnerIdentity.ProcessId,
            protocolContext.GodotOwnerIdentity.StartTimeUtcTicks,
            protocolContext.ServiceProcessIdentity.ProcessId,
            protocolContext.ServiceProcessIdentity.StartTimeUtcTicks,
            descriptorPath);
    }

    public BootstrapReadinessWriteResult TryWriteOnce()
    {
        if (Interlocked.CompareExchange(ref _attemptState, 1, 0) != 0)
        {
            return BootstrapReadinessWriteResult.Failure(
                new InvalidOperationException("bootstrap readiness was already attempted."));
        }

        try
        {
            string json = JsonSerializer.Serialize(_record);
            Console.Out.WriteLine(json);
            Console.Out.Flush();
            return BootstrapReadinessWriteResult.Success();
        }
        catch (Exception exception)
        {
            return BootstrapReadinessWriteResult.Failure(exception);
        }
    }
}

internal readonly record struct BootstrapReadinessWriteResult(
    bool IsSuccess,
    Exception? Exception)
{
    public static BootstrapReadinessWriteResult Success()
        => new(true, null);

    public static BootstrapReadinessWriteResult Failure(Exception exception)
        => new(false, exception);
}

internal sealed record BootstrapReadinessRecord(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("serviceVersion")] string ServiceVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("godotPid")] int GodotPid,
    [property: JsonPropertyName("godotStartTimeUtcTicks")] long GodotStartTimeUtcTicks,
    [property: JsonPropertyName("servicePid")] int ServicePid,
    [property: JsonPropertyName("serviceStartTimeUtcTicks")] long ServiceStartTimeUtcTicks,
    [property: JsonPropertyName("descriptorPath")] string DescriptorPath);

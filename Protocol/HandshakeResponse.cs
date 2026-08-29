using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed record HandshakeResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("serviceVersion")] string ServiceVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("godotPid")] int GodotPid,
    [property: JsonPropertyName("godotStartTimeUtcTicks")] long GodotStartTimeUtcTicks,
    [property: JsonPropertyName("servicePid")] int ServicePid,
    [property: JsonPropertyName("serviceStartTimeUtcTicks")] long ServiceStartTimeUtcTicks,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("port")] int Port);

internal sealed record HandshakeFailureResponse(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("requestId")] string? RequestId);

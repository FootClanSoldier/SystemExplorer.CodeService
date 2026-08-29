using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.CodeService;

internal sealed class DiagnosticLogging : IDisposable
{
    private const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly object _sync = new();
    private readonly ServiceProcessIdentity _serviceProcessIdentity;
    private readonly GodotProcessIdentity _godotOwnerIdentity;
    private StreamWriter? _writer;
    private SessionIdentity? _sessionIdentity;
    private bool _disposed;

    private DiagnosticLogging(
        StreamWriter? writer,
        string? logPath,
        ServiceProcessIdentity serviceProcessIdentity,
        GodotProcessIdentity godotOwnerIdentity)
    {
        _writer = writer;
        LogPath = logPath;
        _serviceProcessIdentity = serviceProcessIdentity;
        _godotOwnerIdentity = godotOwnerIdentity;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _writer is not null;
            }
        }
    }

    public string? LogPath { get; }

    public static DiagnosticLoggingCreationResult Create(
        bool enabled,
        GodotProcessIdentity godotOwnerIdentity,
        ServiceProcessIdentity serviceProcessIdentity)
    {
        if (!enabled)
        {
            return DiagnosticLoggingCreationResult.Success(
                CreateDisabled(godotOwnerIdentity, serviceProcessIdentity));
        }

        try
        {
            string diagnosticDirectory = DiagnosticLogPathResolver.ResolveDiagnosticDirectory();
            Directory.CreateDirectory(diagnosticDirectory);

            string logFileName =
                $"codeservice_{serviceProcessIdentity.ProcessId}_{serviceProcessIdentity.StartTimeUtcTicks}.jsonl";
            string logPath = Path.Combine(diagnosticDirectory, logFileName);

            FileStream stream = new(
                logPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            try
            {
                StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

                return DiagnosticLoggingCreationResult.Success(
                    new DiagnosticLogging(
                        writer,
                        logPath,
                        serviceProcessIdentity,
                        godotOwnerIdentity));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLogging disabledLogging = CreateDisabled(
                godotOwnerIdentity,
                serviceProcessIdentity);
            return DiagnosticLoggingCreationResult.Unavailable(
                disabledLogging,
                $"diagnostic logging unavailable: {ToSingleLine(exception.Message)}");
        }
    }

    public void BindSession(SessionIdentity identity)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sessionIdentity is SessionIdentity existingIdentity)
            {
                if (existingIdentity != identity)
                {
                    throw new InvalidOperationException(
                        "diagnostic logging is already bound to a different session identity.");
                }

                return;
            }

            _sessionIdentity = identity;
        }
    }

    public void WriteEvent(string eventName)
        => WriteRecord<object>(
            eventName,
            details: null,
            transportAddress: null,
            transportPort: null,
            errorType: null,
            errorMessage: null);

    public void WriteEvent<TDetails>(
        string eventName,
        TDetails details)
        where TDetails : class
    {
        ArgumentNullException.ThrowIfNull(details);

        WriteRecord(
            eventName,
            details,
            transportAddress: null,
            transportPort: null,
            errorType: null,
            errorMessage: null);
    }

    public void WriteTransportEvent(
        string eventName,
        LocalTransportEndpoint endpoint)
        => WriteRecord<object>(
            eventName,
            details: null,
            transportAddress: endpoint.Address,
            transportPort: endpoint.Port,
            errorType: null,
            errorMessage: null);

    public void WriteFault(string eventName, Exception exception)
        => WriteRecord<object>(
            eventName,
            details: null,
            transportAddress: null,
            transportPort: null,
            errorType: exception.GetType().FullName,
            errorMessage: exception.Message);

    public void WriteFault<TDetails>(
        string eventName,
        Exception exception,
        TDetails details)
        where TDetails : class
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(details);

        WriteRecord(
            eventName,
            details,
            transportAddress: null,
            transportPort: null,
            errorType: exception.GetType().FullName,
            errorMessage: exception.Message);
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
            }
            catch (Exception)
            {
                DisableSinkNoThrow();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
            }
            catch (Exception)
            {
                // Logging failures are contained and never become service failures.
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception)
            {
                // Logging failures are contained and never become service failures.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static DiagnosticLogging CreateDisabled(
        GodotProcessIdentity godotOwnerIdentity,
        ServiceProcessIdentity serviceProcessIdentity)
        => new(
            writer: null,
            logPath: null,
            serviceProcessIdentity,
            godotOwnerIdentity);

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');

    private void WriteRecord<TDetails>(
        string eventName,
        TDetails? details,
        string? transportAddress,
        int? transportPort,
        string? errorType,
        string? errorMessage)
        where TDetails : class
    {
        lock (_sync)
        {
            if (_disposed || _writer is null)
            {
                return;
            }

            try
            {
                DiagnosticLogRecord<TDetails> record = new(
                    SchemaVersion,
                    DateTimeOffset.UtcNow,
                    eventName,
                    _serviceProcessIdentity.ProcessId,
                    _serviceProcessIdentity.StartTimeUtcTicks,
                    _godotOwnerIdentity.ProcessId,
                    _godotOwnerIdentity.StartTimeUtcTicks,
                    _sessionIdentity?.SessionId,
                    transportAddress,
                    transportPort,
                    errorType,
                    errorMessage,
                    details);

                string json = JsonSerializer.Serialize(record, SerializerOptions);
                _writer.WriteLine(json);
            }
            catch (Exception)
            {
                DisableSinkNoThrow();
            }
        }
    }

    private void DisableSinkNoThrow()
    {
        StreamWriter? writer = _writer;
        _writer = null;

        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch (Exception)
        {
            // The sink is already disabled; no retry loop is started.
        }
    }
}

internal readonly record struct DiagnosticLoggingCreationResult(
    DiagnosticLogging Logging,
    string? WarningMessage)
{
    public static DiagnosticLoggingCreationResult Success(DiagnosticLogging logging)
        => new(logging, null);

    public static DiagnosticLoggingCreationResult Unavailable(
        DiagnosticLogging logging,
        string warningMessage)
        => new(logging, warningMessage);
}

internal sealed record DiagnosticLogRecord<TDetails>(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("servicePid")] int ServicePid,
    [property: JsonPropertyName("serviceStartTimeUtcTicks")] long ServiceStartTimeUtcTicks,
    [property: JsonPropertyName("godotPid")] int GodotPid,
    [property: JsonPropertyName("godotStartTimeUtcTicks")] long GodotStartTimeUtcTicks,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("transportAddress")] string? TransportAddress,
    [property: JsonPropertyName("transportPort")] int? TransportPort,
    [property: JsonPropertyName("errorType")] string? ErrorType,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TDetails? Details)
    where TDetails : class;

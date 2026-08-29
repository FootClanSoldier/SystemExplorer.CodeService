using System.Security.Cryptography;
using System.Text.Json;

namespace SystemExplorer.CodeService;

internal static class SessionDescriptorStore
{
    private const int DescriptorSchemaVersion = 1;
    private const int MaxDescriptorSizeBytes = 16 * 1024;

    public static SessionDescriptorPublicationResult Publish(
        GodotProcessIdentity ownerIdentity,
        ServiceProcessIdentity serviceProcessIdentity,
        SessionIdentity sessionIdentity,
        SessionCredentials credentials,
        int protocolVersion,
        string serviceVersion,
        LocalTransportEndpoint endpoint)
    {
        string? temporaryPath = null;

        try
        {
            ValidateEndpoint(endpoint);

            string descriptorDirectory = SessionRuntimePathResolver.ResolveDescriptorDirectory();
            SessionDescriptorPermissions.EnsureDescriptorDirectory(descriptorDirectory);

            string descriptorPath = SessionRuntimePathResolver.ResolveDescriptorPath(ownerIdentity);
            temporaryPath = CreateTemporaryPath(
                descriptorDirectory,
                ownerIdentity,
                sessionIdentity);

            WriteCompleteTemporaryDescriptor(
                temporaryPath,
                ownerIdentity,
                serviceProcessIdentity,
                sessionIdentity,
                credentials,
                protocolVersion,
                serviceVersion,
                endpoint);

            File.Move(temporaryPath, descriptorPath, overwrite: true);
            temporaryPath = null;

            return SessionDescriptorPublicationResult.Success(
                new SessionDescriptorRegistration(
                    descriptorPath,
                    sessionIdentity,
                    serviceProcessIdentity));
        }
        catch (Exception exception)
        {
            return SessionDescriptorPublicationResult.Failure(
                $"session descriptor publication failed: {ToSingleLine(exception.Message)}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    public static SessionDescriptorRemovalResult TryRemoveOwnedDescriptor(
        SessionDescriptorRegistration registration)
    {
        try
        {
            if (!File.Exists(registration.DescriptorPath))
            {
                return SessionDescriptorRemovalResult.NotPresent();
            }

            DescriptorOwnershipProbeResult probeResult =
                TryReadOwnershipProbe(registration.DescriptorPath);

            if (!probeResult.IsReadable)
            {
                return probeResult.Exception is null
                    ? SessionDescriptorRemovalResult.OwnershipNotProven()
                    : SessionDescriptorRemovalResult.Failed(probeResult.Exception);
            }

            DescriptorOwnershipProbe probe = probeResult.Probe!.Value;
            if (probe.SchemaVersion != DescriptorSchemaVersion
                || !string.Equals(
                    probe.SessionId,
                    registration.SessionIdentity.SessionId,
                    StringComparison.Ordinal)
                || probe.ServicePid != registration.ServiceProcessIdentity.ProcessId
                || probe.ServiceStartTimeUtcTicks
                    != registration.ServiceProcessIdentity.StartTimeUtcTicks)
            {
                return SessionDescriptorRemovalResult.OwnershipNotProven();
            }

            File.Delete(registration.DescriptorPath);
            return File.Exists(registration.DescriptorPath)
                ? SessionDescriptorRemovalResult.Failed(
                    new IOException("descriptor remained present after deletion was attempted."))
                : SessionDescriptorRemovalResult.Removed();
        }
        catch (FileNotFoundException)
        {
            return SessionDescriptorRemovalResult.NotPresent();
        }
        catch (DirectoryNotFoundException)
        {
            return SessionDescriptorRemovalResult.NotPresent();
        }
        catch (Exception exception)
        {
            return SessionDescriptorRemovalResult.Failed(exception);
        }
    }

    private static void ValidateEndpoint(LocalTransportEndpoint endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(endpoint.Address, "127.0.0.1", StringComparison.Ordinal)
            || endpoint.Port <= 0)
        {
            throw new InvalidOperationException(
                "session descriptor requires a verified http://127.0.0.1:<port> endpoint.");
        }
    }

    private static string CreateTemporaryPath(
        string descriptorDirectory,
        GodotProcessIdentity ownerIdentity,
        SessionIdentity sessionIdentity)
    {
        Span<byte> randomBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(randomBytes);
        string randomSuffix = Convert.ToHexString(randomBytes).ToLowerInvariant();

        return Path.Combine(
            descriptorDirectory,
            $".owner_{ownerIdentity.ProcessId}_{ownerIdentity.StartTimeUtcTicks}_{sessionIdentity.SessionId}_{randomSuffix}.tmp");
    }

    private static void WriteCompleteTemporaryDescriptor(
        string temporaryPath,
        GodotProcessIdentity ownerIdentity,
        ServiceProcessIdentity serviceProcessIdentity,
        SessionIdentity sessionIdentity,
        SessionCredentials credentials,
        int protocolVersion,
        string serviceVersion,
        LocalTransportEndpoint endpoint)
    {
        Span<char> encodedToken = stackalloc char[SessionCredentials.AuthenticationTokenBase64Length];

        try
        {
            if (!Convert.TryToBase64Chars(
                    credentials.AuthenticationToken,
                    encodedToken,
                    out int encodedLength)
                || encodedLength != encodedToken.Length)
            {
                throw new InvalidOperationException(
                    "session authentication token could not be encoded for descriptor publication.");
            }

            using FileStream stream = SessionDescriptorPermissions.CreateSecureTemporaryFile(
                temporaryPath);

            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", DescriptorSchemaVersion);
                writer.WriteNumber("protocolVersion", protocolVersion);
                writer.WriteString("serviceVersion", serviceVersion);
                writer.WriteString("sessionId", sessionIdentity.SessionId);
                writer.WriteNumber("godotPid", ownerIdentity.ProcessId);
                writer.WriteNumber("godotStartTimeUtcTicks", ownerIdentity.StartTimeUtcTicks);
                writer.WriteNumber("servicePid", serviceProcessIdentity.ProcessId);
                writer.WriteNumber(
                    "serviceStartTimeUtcTicks",
                    serviceProcessIdentity.StartTimeUtcTicks);
                writer.WriteString("transport", endpoint.Scheme);
                writer.WriteString("address", endpoint.Address);
                writer.WriteNumber("port", endpoint.Port);
                writer.WriteString("authenticationToken", encodedToken);
                writer.WriteEndObject();
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);

            if (stream.Length <= 0 || stream.Length > MaxDescriptorSizeBytes)
            {
                throw new InvalidOperationException(
                    "serialized session descriptor exceeded the allowed size boundary.");
            }
        }
        finally
        {
            encodedToken.Clear();
        }
    }

    private static DescriptorOwnershipProbeResult TryReadOwnershipProbe(string descriptorPath)
    {
        try
        {
            using FileStream stream = new(
                descriptorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (stream.Length < 0 || stream.Length > MaxDescriptorSizeBytes)
            {
                return DescriptorOwnershipProbeResult.Unreadable();
            }

            byte[] buffer = new byte[MaxDescriptorSizeBytes + 1];
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead == 0 || totalRead > MaxDescriptorSizeBytes)
            {
                return DescriptorOwnershipProbeResult.Unreadable();
            }

            if (stream.ReadByte() != -1)
            {
                return DescriptorOwnershipProbeResult.Unreadable();
            }

            return TryParseOwnershipProbe(buffer.AsMemory(0, totalRead));
        }
        catch (FileNotFoundException)
        {
            return DescriptorOwnershipProbeResult.Unreadable();
        }
        catch (DirectoryNotFoundException)
        {
            return DescriptorOwnershipProbeResult.Unreadable();
        }
        catch (Exception exception)
        {
            return DescriptorOwnershipProbeResult.Unreadable(exception);
        }
    }

    private static DescriptorOwnershipProbeResult TryParseOwnershipProbe(
        ReadOnlyMemory<byte> descriptorBytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                descriptorBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DescriptorOwnershipProbeResult.Unreadable();
            }

            int? schemaVersion = null;
            string? sessionId = null;
            int? servicePid = null;
            long? serviceStartTimeUtcTicks = null;
            bool duplicateOwnershipField = false;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        duplicateOwnershipField |= schemaVersion.HasValue;
                        if (!property.Value.TryGetInt32(out int parsedSchemaVersion))
                        {
                            return DescriptorOwnershipProbeResult.Unreadable();
                        }

                        schemaVersion = parsedSchemaVersion;
                        break;

                    case "sessionId":
                        duplicateOwnershipField |= sessionId is not null;
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            return DescriptorOwnershipProbeResult.Unreadable();
                        }

                        sessionId = property.Value.GetString();
                        break;

                    case "servicePid":
                        duplicateOwnershipField |= servicePid.HasValue;
                        if (!property.Value.TryGetInt32(out int parsedServicePid))
                        {
                            return DescriptorOwnershipProbeResult.Unreadable();
                        }

                        servicePid = parsedServicePid;
                        break;

                    case "serviceStartTimeUtcTicks":
                        duplicateOwnershipField |= serviceStartTimeUtcTicks.HasValue;
                        if (!property.Value.TryGetInt64(out long parsedStartTime))
                        {
                            return DescriptorOwnershipProbeResult.Unreadable();
                        }

                        serviceStartTimeUtcTicks = parsedStartTime;
                        break;
                }
            }

            if (duplicateOwnershipField
                || !schemaVersion.HasValue
                || string.IsNullOrWhiteSpace(sessionId)
                || !servicePid.HasValue
                || !serviceStartTimeUtcTicks.HasValue)
            {
                return DescriptorOwnershipProbeResult.Unreadable();
            }

            return DescriptorOwnershipProbeResult.Success(
                new DescriptorOwnershipProbe(
                    schemaVersion.Value,
                    sessionId,
                    servicePid.Value,
                    serviceStartTimeUtcTicks.Value));
        }
        catch (JsonException)
        {
            return DescriptorOwnershipProbeResult.Unreadable();
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception)
        {
            // Only this failed publication attempt's unique temp file is touched.
        }
    }

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal sealed record SessionDescriptorRegistration(
    string DescriptorPath,
    SessionIdentity SessionIdentity,
    ServiceProcessIdentity ServiceProcessIdentity);

internal readonly record struct SessionDescriptorPublicationResult(
    SessionDescriptorRegistration? Registration,
    string? ErrorMessage)
{
    public bool IsSuccess => Registration is not null;

    public static SessionDescriptorPublicationResult Success(
        SessionDescriptorRegistration registration)
        => new(registration, null);

    public static SessionDescriptorPublicationResult Failure(string errorMessage)
        => new(null, errorMessage);
}

internal enum SessionDescriptorRemovalStatus
{
    Removed,
    NotPresent,
    OwnershipNotProven,
    Failed,
}

internal readonly record struct SessionDescriptorRemovalResult(
    SessionDescriptorRemovalStatus Status,
    Exception? Exception)
{
    public bool WasRemoved => Status == SessionDescriptorRemovalStatus.Removed;

    public static SessionDescriptorRemovalResult Removed()
        => new(SessionDescriptorRemovalStatus.Removed, null);

    public static SessionDescriptorRemovalResult NotPresent()
        => new(SessionDescriptorRemovalStatus.NotPresent, null);

    public static SessionDescriptorRemovalResult OwnershipNotProven()
        => new(SessionDescriptorRemovalStatus.OwnershipNotProven, null);

    public static SessionDescriptorRemovalResult Failed(Exception exception)
        => new(SessionDescriptorRemovalStatus.Failed, exception);
}

internal readonly record struct DescriptorOwnershipProbe(
    int SchemaVersion,
    string SessionId,
    int ServicePid,
    long ServiceStartTimeUtcTicks);

internal readonly record struct DescriptorOwnershipProbeResult(
    DescriptorOwnershipProbe? Probe,
    Exception? Exception)
{
    public bool IsReadable => Probe.HasValue;

    public static DescriptorOwnershipProbeResult Success(DescriptorOwnershipProbe probe)
        => new(probe, null);

    public static DescriptorOwnershipProbeResult Unreadable(Exception? exception = null)
        => new(null, exception);
}

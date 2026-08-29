namespace SystemExplorer.CodeService;

internal sealed class SessionProtocolContext
{
    public SessionProtocolContext(
        int protocolVersion,
        string serviceVersion,
        SessionIdentity sessionIdentity,
        GodotProcessIdentity godotOwnerIdentity,
        ServiceProcessIdentity serviceProcessIdentity,
        SessionCredentials sessionCredentials)
    {
        if (protocolVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if (string.IsNullOrWhiteSpace(serviceVersion))
        {
            throw new ArgumentException("service version is required.", nameof(serviceVersion));
        }

        ProtocolVersion = protocolVersion;
        ServiceVersion = serviceVersion;
        SessionIdentity = sessionIdentity;
        GodotOwnerIdentity = godotOwnerIdentity;
        ServiceProcessIdentity = serviceProcessIdentity;
        SessionCredentials = sessionCredentials ?? throw new ArgumentNullException(nameof(sessionCredentials));
    }

    public int ProtocolVersion { get; }

    public string ServiceVersion { get; }

    public SessionIdentity SessionIdentity { get; }

    public GodotProcessIdentity GodotOwnerIdentity { get; }

    public ServiceProcessIdentity ServiceProcessIdentity { get; }

    public SessionCredentials SessionCredentials { get; }
}

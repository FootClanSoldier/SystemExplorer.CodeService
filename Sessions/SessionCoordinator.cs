namespace SystemExplorer.CodeService;

internal sealed class SessionCoordinator : IDisposable
{
    private readonly PerOwnerLaunchAuthority _launchAuthority;
    private readonly object _descriptorSync = new();
    private SessionDescriptorRegistration? _descriptorRegistration;
    private int _disposeState;

    private SessionCoordinator(
        GodotProcessIdentity ownerIdentity,
        PerOwnerLaunchAuthority launchAuthority,
        SessionIdentity identity,
        SessionCredentials credentials)
    {
        OwnerIdentity = ownerIdentity;
        _launchAuthority = launchAuthority;
        Identity = identity;
        Credentials = credentials;
    }

    public GodotProcessIdentity OwnerIdentity { get; }

    public SessionIdentity Identity { get; }

    public SessionCredentials Credentials { get; }

    public bool HasPublishedDescriptor
    {
        get
        {
            lock (_descriptorSync)
            {
                return _descriptorRegistration is not null;
            }
        }
    }

    public string? PublishedDescriptorPath
    {
        get
        {
            lock (_descriptorSync)
            {
                return _descriptorRegistration?.DescriptorPath;
            }
        }
    }

    public static SessionCoordinatorCreationResult TryCreate(GodotProcessIdentity ownerIdentity)
    {
        PerOwnerLaunchAuthorityAcquisitionResult authorityResult =
            PerOwnerLaunchAuthority.TryAcquire(ownerIdentity);

        if (!authorityResult.IsSuccess)
        {
            return SessionCoordinatorCreationResult.AuthorityNotAcquired(
                authorityResult.ErrorMessage!);
        }

        PerOwnerLaunchAuthority launchAuthority = authorityResult.Authority!;
        SessionCredentials? credentials = null;

        try
        {
            SessionIdentity identity = SessionIdentity.Create();
            credentials = SessionCredentials.Create();

            SessionCoordinator coordinator = new(
                ownerIdentity,
                launchAuthority,
                identity,
                credentials);
            credentials = null;

            return SessionCoordinatorCreationResult.Success(coordinator);
        }
        catch (Exception exception)
        {
            try
            {
                credentials?.Dispose();
            }
            finally
            {
                try
                {
                    launchAuthority.Dispose();
                }
                catch
                {
                    // The controlled session-startup failure remains authoritative.
                }
            }

            return SessionCoordinatorCreationResult.SessionInitializationFailed(
                $"session identity/credential creation failed: {ToSingleLine(exception.Message)}");
        }
    }

    public SessionDescriptorPublicationResult PublishDescriptor(
        int protocolVersion,
        string serviceVersion,
        ServiceProcessIdentity serviceProcessIdentity,
        LocalTransportEndpoint endpoint)
    {
        lock (_descriptorSync)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return SessionDescriptorPublicationResult.Failure(
                    "session descriptor cannot be published after session retirement started.");
            }

            if (_descriptorRegistration is not null)
            {
                return SessionDescriptorPublicationResult.Failure(
                    "session descriptor publication may only succeed once.");
            }

            SessionDescriptorPublicationResult publicationResult = SessionDescriptorStore.Publish(
                OwnerIdentity,
                serviceProcessIdentity,
                Identity,
                Credentials,
                protocolVersion,
                serviceVersion,
                endpoint);

            if (publicationResult.IsSuccess)
            {
                _descriptorRegistration = publicationResult.Registration;
            }

            return publicationResult;
        }
    }

    public SessionCoordinatorRetirementResult Retire()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return SessionCoordinatorRetirementResult.AlreadyRetired();
        }

        SessionDescriptorRegistration? descriptorRegistration;
        lock (_descriptorSync)
        {
            descriptorRegistration = _descriptorRegistration;
            _descriptorRegistration = null;
        }

        SessionDescriptorRemovalResult descriptorRemovalResult = descriptorRegistration is null
            ? SessionDescriptorRemovalResult.NotPresent()
            : SessionDescriptorStore.TryRemoveOwnedDescriptor(descriptorRegistration);

        Exception? retirementFailure = null;

        try
        {
            Credentials.Dispose();
        }
        catch (Exception exception)
        {
            retirementFailure = exception;
        }

        try
        {
            _launchAuthority.Dispose();
        }
        catch (Exception exception)
        {
            retirementFailure = retirementFailure is null
                ? exception
                : new AggregateException(retirementFailure, exception);
        }

        return new SessionCoordinatorRetirementResult(
            descriptorRemovalResult,
            retirementFailure,
            WasAlreadyRetired: false);
    }

    public void Dispose()
    {
        SessionCoordinatorRetirementResult result = Retire();
        if (result.RetirementFailure is not null)
        {
            throw new InvalidOperationException(
                "session retirement failed after descriptor cleanup was attempted.",
                result.RetirementFailure);
        }
    }

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct SessionCoordinatorRetirementResult(
    SessionDescriptorRemovalResult DescriptorRemoval,
    Exception? RetirementFailure,
    bool WasAlreadyRetired)
{
    public static SessionCoordinatorRetirementResult AlreadyRetired()
        => new(
            SessionDescriptorRemovalResult.NotPresent(),
            RetirementFailure: null,
            WasAlreadyRetired: true);
}

internal enum SessionCoordinatorCreationFailureKind
{
    None,
    LaunchAuthorityNotAcquired,
    SessionInitializationFailure,
}

internal readonly record struct SessionCoordinatorCreationResult(
    SessionCoordinator? Coordinator,
    SessionCoordinatorCreationFailureKind FailureKind,
    string? ErrorMessage,
    bool LaunchAuthorityWasAcquired)
{
    public bool IsSuccess =>
        Coordinator is not null && FailureKind == SessionCoordinatorCreationFailureKind.None;

    public static SessionCoordinatorCreationResult Success(SessionCoordinator coordinator)
        => new(
            coordinator,
            SessionCoordinatorCreationFailureKind.None,
            null,
            LaunchAuthorityWasAcquired: true);

    public static SessionCoordinatorCreationResult AuthorityNotAcquired(string errorMessage)
        => new(
            null,
            SessionCoordinatorCreationFailureKind.LaunchAuthorityNotAcquired,
            errorMessage,
            LaunchAuthorityWasAcquired: false);

    public static SessionCoordinatorCreationResult SessionInitializationFailed(string errorMessage)
        => new(
            null,
            SessionCoordinatorCreationFailureKind.SessionInitializationFailure,
            errorMessage,
            LaunchAuthorityWasAcquired: true);
}

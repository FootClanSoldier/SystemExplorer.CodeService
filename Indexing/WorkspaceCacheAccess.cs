namespace SystemExplorer.CodeService;

internal sealed class WorkspaceCacheAccess : IDisposable
{
    private WorkspaceCacheAuthority? _authority;
    private int _disposed;

    private WorkspaceCacheAccess(
        WorkspaceCacheLocation location,
        CacheCoordinationMode coordinationMode,
        WorkspaceCacheAuthority? authority,
        string? unavailabilityReason)
    {
        Location = location;
        CoordinationMode = coordinationMode;
        _authority = authority;
        UnavailabilityReason = unavailabilityReason;
    }

    public WorkspaceCacheLocation Location { get; }

    public CacheCoordinationMode CoordinationMode { get; }

    public WorkspaceCacheAuthority? Authority => Volatile.Read(ref _authority);

    public string? UnavailabilityReason { get; }

    public bool CanReadPersistentCache
        => Volatile.Read(ref _disposed) == 0
            && (CoordinationMode == CacheCoordinationMode.LegacyUncoordinatedNoGc
                || (CoordinationMode == CacheCoordinationMode.CoordinatedExclusive
                    && Volatile.Read(ref _authority) is not null));

    public bool CanWritePersistentCache => CanReadPersistentCache;

    public bool CanCollectGarbage
        => Volatile.Read(ref _disposed) == 0
            && CoordinationMode == CacheCoordinationMode.CoordinatedExclusive
            && Volatile.Read(ref _authority) is not null;

    public static WorkspaceCacheAccess Create(WorkspaceCacheLocation location)
    {
        if (!WorkspaceCacheAuthority.IsByteRangeLockSupported())
        {
            return new WorkspaceCacheAccess(
                location,
                CacheCoordinationMode.LegacyUncoordinatedNoGc,
                authority: null,
                unavailabilityReason: null);
        }

        WorkspaceCacheAuthorityAcquisitionResult acquisition =
            WorkspaceCacheAuthority.TryAcquire(location);
        if (acquisition.IsSuccess)
        {
            return new WorkspaceCacheAccess(
                location,
                CacheCoordinationMode.CoordinatedExclusive,
                acquisition.Authority,
                unavailabilityReason: null);
        }

        return new WorkspaceCacheAccess(
            location,
            CacheCoordinationMode.Unavailable,
            authority: null,
            acquisition.ErrorMessage);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        WorkspaceCacheAuthority? authority = Interlocked.Exchange(ref _authority, null);
        authority?.Dispose();
    }
}

internal enum CacheCoordinationMode
{
    CoordinatedExclusive,
    LegacyUncoordinatedNoGc,
    Unavailable,
}

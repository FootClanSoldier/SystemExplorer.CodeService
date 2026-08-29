using System.Collections.ObjectModel;

namespace SystemExplorer.CodeService;

internal sealed class ProjectIndexGeneration
{
    public ProjectIndexGeneration(
        string generationId,
        WorkspaceIdentity workspaceIdentity,
        IndexManifest manifest,
        IReadOnlyDictionary<int, ProjectIndexShardSnapshot> shardCatalog,
        ProjectIndexPersistenceState persistenceState)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationId);
        WorkspaceIdentity = workspaceIdentity ?? throw new ArgumentNullException(nameof(workspaceIdentity));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(shardCatalog);

        GenerationId = generationId;
        ShardCatalog = new ReadOnlyDictionary<int, ProjectIndexShardSnapshot>(
            new Dictionary<int, ProjectIndexShardSnapshot>(shardCatalog));
        PersistenceState = persistenceState;
    }

    public string GenerationId { get; }

    public WorkspaceIdentity WorkspaceIdentity { get; }

    public IndexManifest Manifest { get; }

    public ReadOnlyDictionary<int, ProjectIndexShardSnapshot> ShardCatalog { get; }

    public ProjectIndexPersistenceState PersistenceState { get; }
}

internal sealed class ProjectIndexShardSnapshot
{
    public ProjectIndexShardSnapshot(
        IndexManifestShardReference reference,
        string? persistentPath,
        IndexShard? inMemoryShard)
    {
        if (persistentPath is null && inMemoryShard is null)
        {
            throw new ArgumentException("shard snapshot requires persistent or in-memory state.");
        }

        Reference = reference;
        PersistentPath = persistentPath;
        InMemoryShard = inMemoryShard;
    }

    public IndexManifestShardReference Reference { get; }

    public string? PersistentPath { get; }

    public IndexShard? InMemoryShard { get; }
}

internal enum ProjectIndexPersistenceState
{
    Persisted,
    MemoryOnly,
}

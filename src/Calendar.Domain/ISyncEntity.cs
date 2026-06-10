namespace Calendar.Domain;

/// <summary>
/// Marker for user-metadata entities that cloud sync replicates. The four columns below are the
/// zero-knowledge, last-writer-wins-per-field sync quartet (DATA-SCHEMA §2.8). A model-building convention in
/// Calendar.Infrastructure applies these columns and a <c>!IsDeleted</c> global query filter uniformly, so
/// they are declared once on the interface rather than repeated per entity.
/// </summary>
public interface ISyncEntity
{
    /// <summary>Wall-clock of the last local mutation; LWW tiebreak after <see cref="Lamport"/>.</summary>
    DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>The device that authored the last write (origin id; part of the version vector).</summary>
    Guid DeviceId { get; set; }

    /// <summary>Monotone per-device counter — the primary causal-ordering / conflict key.</summary>
    long Lamport { get; set; }

    /// <summary>Tombstone. Soft-delete; replicated so deletions propagate.</summary>
    bool IsDeleted { get; set; }
}

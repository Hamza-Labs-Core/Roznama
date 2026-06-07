using Calendar.Plugin.Abstractions;

namespace Calendar.Domain.Entities;

/// <summary>An installed plugin's registry row (DATA-SCHEMA §2.1). Device-local; not E2E-synced.</summary>
public sealed class Plugin
{
    /// <summary>Reverse-DNS id from <c>plugin.yaml</c> (natural PK, e.g. <c>org.unifiedcalendar.google</c>).</summary>
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Version { get; set; } = default!;
    public string SdkVersion { get; set; } = default!;
    public PluginKind Kind { get; set; }
    public PluginStatus Status { get; set; }

    /// <summary>Capability ids (mapped as a JSON array primitive collection).</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>Full parsed manifest, stored as a JSON column (never queried relationally).</summary>
    public string Manifest { get; set; } = default!;

    public TrustTier TrustTier { get; set; }
    public DateTimeOffset InstalledAtUtc { get; set; }
}

/// <summary>Per-plugin non-secret settings rendered from the manifest <c>config</c> schema (DATA-SCHEMA §2.1).</summary>
public sealed class PluginConfig : ISyncEntity
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = default!;

    /// <summary>Config object validated against the manifest schema (JSON column). Secrets never go here.</summary>
    public string Values { get; set; } = default!;

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>A connected provider account (DATA-SCHEMA §2.1).</summary>
public sealed class Account : ISyncEntity
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;

    /// <summary>Vault handle for this account's token/credential (→ <see cref="SecretRef.Id"/>); null for <c>auth.scheme=none</c>.</summary>
    public Guid? AuthRef { get; set; }

    /// <summary>User-orderable; drives dedup canonical choice. Lower = higher priority.</summary>
    public int Priority { get; set; }

    public AccountStatus Status { get; set; }

    /// <summary>Denormalized convenience mirror of the latest <see cref="SyncState.LastSyncAtUtc"/>.</summary>
    public DateTimeOffset? LastSyncAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Encrypted credential vault entry (DATA-SCHEMA §2.2). The only place tokens/credentials/feed-URL secrets
/// live, as ciphertext + key id. Device-local by policy — never E2E-synced.
/// </summary>
public sealed class SecretRef
{
    public Guid Id { get; set; }
    public SecretKind Kind { get; set; }

    /// <summary>Identifier of the wrapping key (DPAPI/Keychain/KMS/master-key id). No key material stored here.</summary>
    public string KeyId { get; set; } = default!;
    public string Algorithm { get; set; } = default!;
    public byte[] Nonce { get; set; } = default!;
    public byte[] Ciphertext { get; set; } = default!;
    public byte[]? AuthTag { get; set; }

    /// <summary>Associated data bound into the AEAD (e.g. accountId + kind) to prevent ciphertext swapping.</summary>
    public string? Aad { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset RotatedAtUtc { get; set; }
    public string RowVersion { get; set; } = default!;
}

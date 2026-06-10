namespace Calendar.Domain.Entities;

/// <summary>
/// This device's cloud-sync enrollment (ROADMAP Phase 6; ADR-0003). Single row. The relay never sees
/// plaintext: the sync key is derived client-side from the user's passphrase and lives in the vault —
/// only its vault handle is stored here. Device-local; never synced.
/// </summary>
public sealed class CloudSyncConfig
{
    public Guid Id { get; set; }
    public string RelayUrl { get; set; } = default!;
    public Guid SpaceId { get; set; }

    /// <summary>Vault handle of the passphrase-derived AES key (base64). The passphrase itself is never stored.</summary>
    public Guid KeySecretRef { get; set; }

    /// <summary>Vault handle of the relay space token.</summary>
    public Guid TokenSecretRef { get; set; }

    /// <summary>Highest relay sequence already pulled and applied.</summary>
    public long LastPulledSeq { get; set; }

    /// <summary>Push cursor: local mutations after this instant still need pushing.</summary>
    public DateTimeOffset? LastPushedAtUtc { get; set; }

    public DateTimeOffset? LastSyncAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>A relay space: one shared encrypted change feed (server side of the relay).</summary>
public sealed class RelaySpace
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (hex) of the space token — the token itself is returned once at creation, never stored.</summary>
    public string TokenHash { get; set; } = default!;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// One encrypted change set in a relay space (server side). <see cref="Payload"/> is opaque ciphertext
/// (nonce ‖ tag ‖ data) — the relay can order and fan out blobs but never read them (E2E).
/// </summary>
public sealed class RelayBlob
{
    /// <summary>Monotone per-store sequence — the pull cursor.</summary>
    public long Seq { get; set; }

    public Guid SpaceId { get; set; }

    /// <summary>The authoring device, so a device can exclude its own blobs when pulling.</summary>
    public Guid DeviceId { get; set; }

    public byte[] Payload { get; set; } = default!;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

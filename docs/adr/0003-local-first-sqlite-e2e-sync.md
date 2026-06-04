# 0003 — Hybrid local-first: SQLite source of truth + optional zero-knowledge cloud sync

**Status:** Accepted (2026-06)

## Context

The app must work fully offline and protect highly personal data (calendars, locations, travel)
([ARCHITECTURE.md §1](../ARCHITECTURE.md#1-design-principles),
[§8 Storage & hybrid local-first](../ARCHITECTURE.md#8-storage--hybrid-local-first),
[§17 Security & privacy](../ARCHITECTURE.md#17-security--privacy)). Users also want multi-device views
and sharing, which need *some* server — but the design refuses to let any relay read event content.

## Decision

Make **on-device SQLite the source of truth** (EF Core, SQLCipher-capable;
[DATA-SCHEMA.md §5.5](../DATA-SCHEMA.md#5-ef-core-specifics)). Providers are fetched/cached locally; the
app runs offline. Cloud sync is **opt-in and zero-knowledge**: change sets are encrypted on-device
(key derived via Argon2id) before upload, and the relay stores only **ciphertext + version vectors
(Lamport clocks)**, never cleartext. Conflict resolution is **last-writer-wins per user-metadata
field**; provider-owned fields refresh from the provider. **Tokens stay on the authorizing device** and
are never synced ([DATA-SCHEMA.md §2.2, §2.8](../DATA-SCHEMA.md#22-secrets-vault)).

## Consequences

- **Positive:** instant/offline by construction; strong privacy posture (the relay cannot read events);
  clean separation between replicated user metadata and provider cache.
- **Negative / trade-off:** LWW-per-field can silently drop a concurrent edit (no rich CRDT merge);
  zero-knowledge sync means **no server-side search/recovery** and a lost passphrase = lost ciphertext;
  sharing needs a reachable endpoint, so pure-local mode limits sharing to own devices/LAN
  ([ARCHITECTURE.md §16](../ARCHITECTURE.md#16-sharing)).

## Alternatives considered

- **Server-authoritative cloud store** — easy multi-device/search, but the server reads all events;
  rejected on privacy.
- **Local-only, no sync** — maximal privacy but no multi-device and awkward sharing; rejected as too
  limiting. The chosen hybrid keeps local-only as a valid mode.
- **Full CRDT merge** — better concurrent-edit semantics but far heavier; LWW-per-field is sufficient
  for the metadata that actually syncs.

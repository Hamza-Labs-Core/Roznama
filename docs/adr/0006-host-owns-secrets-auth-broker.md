# 0006 — Host owns all secrets via an auth broker

**Status:** Accepted (2026-06)

## Context

.NET has no full code-access-security sandbox, so a loaded plugin could in principle read any in-process
secret. Plugins also span many auth schemes (none, API key, Basic, app-password, OAuth2 PKCE, OAuth2
client-credentials). Letting each plugin hold client secrets and tokens would be both the biggest
security risk and a barrier to being provider-agnostic
([PLUGINS.md §6 Authentication broker](../PLUGINS.md#6-authentication-broker),
[§8 Security & sandboxing](../PLUGINS.md#8-security--sandboxing),
[ARCHITECTURE.md §17](../ARCHITECTURE.md#17-security--privacy)).

## Decision

A single host component — the **auth broker** — runs **all** authentication. Plugins *declare* their
auth needs in the manifest; the host performs/refreshes the OAuth dance, stores refresh/access tokens in
an **encrypted vault** (`SecretRef`, [DATA-SCHEMA.md §2.2](../DATA-SCHEMA.md#22-secrets-vault)), and
hands the plugin only a **scoped, short-lived `AuthHandle`** at call time
([SDK-CONTRACT.md §3 Auth broker](../SDK-CONTRACT.md#3-auth-broker)). Plugins never see client secrets
or stored tokens; `IAuthBroker.ApplyAsync` even lets them apply a credential without reading it.

## Consequences

- **Positive:** removes the single biggest risk — secret exfiltration by a plugin; a new OAuth provider
  needs **no host code**, only manifest fields; tokens stay device-local and out of cloud sync
  ([0003](0003-local-first-sqlite-e2e-sync.md)); auth is uniform across plugin kinds.
- **Negative / trade-off:** the broker is a central must-build component and a single point of trust;
  handles are short-lived, so plugins must re-fetch per call rather than cache; truly exotic auth flows
  must be expressed within the supported scheme set.

## Alternatives considered

- **Plugins manage their own OAuth/secrets** — simpler host, but every plugin handles secret material
  (huge surface) and provider onboarding requires plugin code; rejected.
- **OS keystore only, no broker abstraction** — still leaves the OAuth dance and refresh in each plugin;
  rejected. (The OS keystore is used *under* the broker to protect the vault.)

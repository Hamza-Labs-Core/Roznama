# 0007 — AssemblyLoadContext isolation + optional out-of-process (gRPC)

**Status:** Accepted (2026-06)

## Context

Assembly plugins are arbitrary .NET code with their own dependency versions, and the host wants to
load/upgrade/remove them without restarting. Because .NET has no full in-process code-access-security
sandbox, isolation must be **structural**, and untrusted code needs a stronger boundary than a shared
process gives ([PLUGINS.md §7 Loading, isolation & lifecycle](../PLUGINS.md#7-loading-isolation--lifecycle),
[§8 Security & sandboxing](../PLUGINS.md#8-security--sandboxing),
[ARCHITECTURE.md §4](../ARCHITECTURE.md#4-plugin--capability-system)).

## Decision

Load each assembly plugin into its **own collectible `AssemblyLoadContext`** — independent dependency
versions and **hot load / unload / upgrade** with no app restart. Declarative connectors are data and
run in the shared connector engine (no ALC). **Untrusted code can run out-of-process behind gRPC**
(over stdio/named pipe), so a crash or hostile plugin can't read host memory or take the app down.
Trust tiers (in-box · signed · community · local-dev) drive the boundary; untrusted tiers default to
out-of-process. Signature + SDK-version checks run before load; per-plugin egress allowlist and resource
budgets are enforced at runtime.

## Consequences

- **Positive:** dependency-version isolation and zero-downtime upgrades; structural blast-radius
  containment without a CAS sandbox; a graduated boundary (in-process ALC for trusted, separate process
  for untrusted) instead of one-size-fits-all.
- **Negative / trade-off:** ALC unloadability is fragile — a leaked reference pins the context and
  prevents unload; out-of-process adds gRPC serialization latency and operational complexity; isolation
  is structural, not a hard memory sandbox, so in-process plugins are still trusted with process memory.

## Alternatives considered

- **All plugins in the default load context** — simplest, but no per-plugin dependency versions, no
  hot-unload, and no containment; rejected.
- **All plugins out-of-process** — strongest isolation but pays gRPC overhead for every trusted
  first-party plugin; reserved for untrusted tiers instead.
- **WASM/Wasmtime sandbox for plugins** — strong sandbox but a poor fit for the .NET SDK contract and
  the CalDAV/Graph libraries; rejected for now.

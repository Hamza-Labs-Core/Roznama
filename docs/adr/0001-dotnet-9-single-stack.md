# 0001 — .NET 9 / C# as the single stack (host + plugins + UI)

**Status:** Accepted (2026-06)

## Context

Unified Calendar spans an HTTP host, a background scheduler, a runtime plugin system, and a rich
front end ([ARCHITECTURE.md §2 Technology stack](../ARCHITECTURE.md#2-technology-stack),
[§18 Solution layout](../ARCHITECTURE.md#18-solution-layout)). A core requirement is that
third-party and first-party integrations compile against the *same* SDK contract
([PLUGINS.md §5](../PLUGINS.md#5-assembly-plugins--the-sdk-contract)). A polyglot stack (e.g. a
TypeScript front end + a separate plugin language) would fracture that contract, duplicate domain
models across a language boundary, and complicate the plugin ABI.

## Decision

Use **C# / .NET 9 as the single language and runtime across host, plugins, and UI**. ASP.NET Core
Minimal APIs for the host; the front end is Blazor WebAssembly (see [0002](0002-blazor-wasm-pwa-frontend.md));
assembly plugins are .NET DLLs compiled against `Calendar.Plugin.Abstractions`. Supporting libraries
are all in-ecosystem: EF Core + SQLite, Quartz.NET, Ical.Net, Microsoft.OpenApi, Polly.

## Consequences

- **Positive:** one set of domain types from DB to UI; assembly plugins share the exact SDK boundary
  the host uses; one toolchain, one test framework, one CI; `AssemblyLoadContext` hot-load (see
  [0007](0007-alc-isolation-out-of-process.md)) is a native-runtime capability.
- **Negative / trade-off:** ties the project to the .NET release cadence and to BCL/runtime sandbox
  limits (.NET has no full code-access-security sandbox — addressed structurally in
  [0006](0006-host-owns-secrets-auth-broker.md)/[0007](0007-alc-isolation-out-of-process.md)). WASM
  payload/startup cost is a known Blazor concern, mitigated in [0002](0002-blazor-wasm-pwa-frontend.md).

## Alternatives considered

- **Node/TypeScript end-to-end** — strong web story, but no in-process collectible assembly isolation
  for plugins and a weaker fit for CalDAV/Graph/recurrence libraries (Ical.Net).
- **Polyglot (C# host + JS/TS plugins or UI framework)** — would split the SDK contract across a
  language boundary and duplicate the domain model; rejected for the "same SDK for everyone" goal.

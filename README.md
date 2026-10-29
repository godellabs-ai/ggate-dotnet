# Gödel Gate .NET SDK

Version 0.2.1. Compatible with Gödel Gate Console 0.2.x. Targets .NET 8 and .NET 10.
The core package has no third-party runtime dependencies. The optional Microsoft 365 package
uses Microsoft's public middleware contract. See the example and validation instructions below.

## Use the SDK

These are local source packages; they have not been published to NuGet. Reference
`src/GodelGate/GodelGate.csproj` from your application, or build the local packages with `dotnet pack`.

```csharp
using GodelGate;

await using var gate = new GateClient(new GateOptions {
    AgentName = "Operations Assistant", Team = "Platform Engineering"
}); // GGATE_CONSOLE_URL and GGATE_API_KEY come from the environment.

var scan = new ScanOptions { Framework = "custom", SessionId = "conversation-123", Enforce = true };
await gate.ScanPromptAsync("Check the service status", scan);
await gate.ScanToolCallAsync("get_service_status", new { service = "orders" }, scan);
// Execute the tool only after the preflight returns.
await gate.ScanToolResultAsync("get_service_status", "healthy", scan);
await gate.ScanResponseAsync("The service is healthy", scan);
// Only now deliver the response.
```

`Enforce` throws `GateBlockedException` on a block. `BeforeDelivery` waits and returns the decision
without throwing. Both make response/result events `pre`, matching Gate's enforcement contract.
Without either option, response/result scans are audit events; `Wait` waits for classification
but does not claim delivery can still be prevented. Prompt, tool, shell, file, and web scans wait
by default. Explicit enforcement overrides audit mode.

`Decision` preserves the full JSON response, including detector evidence, policy source,
document intelligence, `PipelineStatus`, and `MediaGate`. These metadata fields do not replace
the security verdict. `hard_block` from older Consoles is treated as `block`.

## Microsoft 365 Agents

Reference `src/GodelGate.Microsoft365/GodelGate.Microsoft365.csproj`, then register:

```csharp
adapter.Use(new GodelGate.Microsoft365.GateMiddleware(gate));
```

Alternatively invoke the middleware around your `IAgent.OnTurnAsync`, as the
[Microsoft 365 example](examples/Microsoft365Agent/README.md) does. Register it once.
Use `GateMiddleware.Metadata(context)` for explicit tool/model scans inside the turn.

The middleware scans incoming activity content, outgoing sends, and updates, including inline
card and invoke data. A whole batch is checked before any item is sent. It does not automatically
inspect internal tool execution, remote attachments, managed Foundry/Copilot operations, or
streaming content that has already been delivered. Instrument those application boundaries separately.

## Configuration and transport

- Required identity: `AgentName` / `GGATE_AGENT_NAME` and `Team` / `GGATE_TEAM`.
- Console: `ConsoleUrl` / `GGATE_CONSOLE_URL`, `ApiKey` / `GGATE_API_KEY`.
- Optional identity: `GGATE_ORG_ID`, `GGATE_USER`, `GGATE_WORKSTATION_ID`, `GGATE_COLLECTOR_ID`.
- `GGATE_MODE=async` selects audit queuing; the default is synchronous preflight.
- `GGATE_TIMEOUT_MS` bounds the complete scan including token exchange (default 4000ms).
- `GGATE_REDACT=1` masks common secrets before upload. It is off by default so Gate can detect
  them. Redaction is pattern-based, not a complete sensitive-data classification system.
- `GGATE_DISABLED=1` bypasses scanning. Queue size and cooldown are configurable in `GateOptions`.

The API key is exchanged for short-lived bearer tokens via `/api/v1/detection-engine/token`;
refresh tokens rotate via `/api/v1/detection-engine/token/refresh`. Scans use `/api/v1/scan`.
TLS validation is enabled and redirects are disabled. To use a private CA, configure an
`HttpClient` with your organization's trust settings and inject it; never disable TLS validation
for production. A caller-supplied client remains owned by the caller.

Transport/authentication failures return an allowed decision with `FailOpen=true`, matching
the existing SDKs. Caller cancellation propagates. Applications requiring fail-closed behavior
must check `FailOpen` before executing or delivering content. Audit mode has a bounded queue;
a full queue returns a fail-open decision without accepting that event. `FlushAsync` waits for
pending work, and async disposal attempts a bounded drain. The .NET SDK does not retry failed
audit deliveries or promise durable telemetry. Use a stable collector ID for persistent workloads.

`Attachment.FromPathAsync` hashes the entire file and captures metadata by default. Explicit
content capture is bounded (65,536 bytes by default); `Truncated` records incomplete capture.
File scan content must be supplied explicitly. The SDK never fetches attachment URLs.

## Build and test

Use a .NET 10 SDK to build the solution and run its tests/example; the libraries also target .NET 8.

```sh
dotnet restore GodelGate.slnx --locked-mode
dotnet test GodelGate.slnx -c Release
dotnet pack src/GodelGate/GodelGate.csproj -c Release -o artifacts
dotnet pack src/GodelGate.Microsoft365/GodelGate.Microsoft365.csproj -c Release -o artifacts
```

Tests simulate the Console's HTTP contract and use Microsoft's real TurnContext for middleware
checks. They are not evidence of live Gate detector behavior or Microsoft tenant authentication.

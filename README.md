# Gödel Gate .NET SDK

Version 0.3.0, released with Gödel Gate Console 0.3.0. Targets .NET 8 and .NET 10.
The core package has no third-party runtime dependencies. The optional Microsoft 365 package
uses Microsoft's public middleware contract. See the example and validation instructions below.

## Use the SDK

Pin the release selected by your Console. The versioned GitHub release contains the source and
NuGet package files for a local feed; it does not follow changes on `main`.

```sh
git clone --branch v0.3.0 --depth 1 https://github.com/godellabs-ai/ggate-dotnet.git ../ggate-dotnet
```

These are local source packages; they have not been published to NuGet. Clone this repository beside
your project, not inside it, because a project compiles every `.cs` file beneath it. Reference
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

## Sessions and the Kill Switch

### AI access rules and application users

The Console's **Policies → Authorised AI** access rules apply to SDK scans. Select the named
application (`AgentName`), its framework, or all supported agents. The Console resolves users,
groups, and user tags from its endpoint directory. `Team` describes workload ownership; it does
not grant directory group membership.

In a shared application, bind the authenticated user's endpoint email or directory alias for the
whole request. Use a value verified by your server's authentication layer, never an identity
supplied directly in a prompt or request body:

```csharp
using (GateUser.Begin(authenticatedUserEmail))
using (GateSession.Begin(conversationId))
{
    var scan = new ScanOptions { Framework = "microsoft-extensions-ai", Enforce = true };
    await gate.ScanPromptAsync(prompt, scan); // A block throws GateBlockedException, including in Audit mode.
    var answer = await RunAgentAsync(prompt);
    await gate.ScanResponseAsync(answer, scan);
    // Deliver only after the final scan returns.
}
```

`GateUser.Begin` follows async calls and instrumented adapters and restores the previous identity
on disposal. An explicitly assigned `ScanOptions.User` overrides the scope; the scope overrides
`GateOptions.User` / `GGATE_USER`. Explicit `null` or blank means anonymous and clears the fallback.
Set it explicitly for unauthenticated requests. Without an override, existing single-user/service
deployments keep their configured identity.

Block rules prevent operations only when scans run before the operation with `Enforce = true`,
or the application checks the returned verdict. Adapter enforcement follows `ScanMode.Sync`.
Audit calls cannot prevent an operation already performed. Warn remains an allowed decision for
the application to present; `Decision.PolicySource` and `Decision.MatchedPolicies` retain policy
IDs and names, including Monitor matches. Ordinary access blocks are evaluated again at the next
scan. Cached Kill Switch locks are separate per user and framework; `GateClient.Contained` means
at least one such lock is known.

SDK API keys do not establish a human provider login or subscription plan. Approved **provider
account domains** and observed plan tags come from coding-agent/browser collectors. SDK access
rules use the endpoint user's Console identity and existing directory tags; the SDK does not
claim provider email, plan, groups, or tags from request metadata. Existing transport fail-open
behavior is unchanged; applications requiring fail-closed preflight must also check `Decision.FailOpen`.

### Conversation sessions

```csharp
using (GateSession.Begin(conversationId)) // one conversation, one Console session
    answer = await RunAgentAsync(text);
```

A Console lock throws `GateAgentContainedException` (a `GateBlockedException`) in `Sync` and `Audit`
mode, even without `Enforce`. In `Audit` mode the first queued verdict turns scanning synchronous, so an
unlock is seen at the next scan. A known lock stays in force while the Console cannot confirm its
release; an outage with no lock still fails open. `GateClient.Contained` reports the state.

Set `CaptureFileText` (or `GGATE_CAPTURE_FILE_TEXT=1`) to send content for scanning.
`Attachment.FromPathAsync` and `Attachment.FromBytes` cover files, images, audio, and video.

## Microsoft.Extensions.AI

Reference `src/GodelGate.AI/GodelGate.AI.csproj`. Keep the Gate client next to the model so each tool
step is seen:

```csharp
IChatClient chat = provider.AsBuilder().UseFunctionInvocation().UseGodelGate(gate).Build();
```

Each call scans the newest user message with its `DataContent` images and audio, tool calls before they
run, tool results, and the answer. Streamed text is scanned when the stream ends and cannot be recalled.

OpenAI's .NET client works the same way, and so does Microsoft Agent Framework, which accepts the built
`IChatClient`:

```csharp
IChatClient chat = new OpenAIClient(key).GetChatClient("gpt-4o").AsIChatClient()
    .AsBuilder().UseFunctionInvocation().UseGodelGate(gate, "openai-assistants", "openai").Build();
var agent = new ChatClientAgent(chat, "You are helpful.", "assistant");
```

## AutoGen

Reference `src/GodelGate.AutoGen/GodelGate.AutoGen.csproj`. Built on `AutoGen.Core` 0.2.3. Add Gate after the
message connector and before any `FunctionCallMiddleware`, so tool calls are scanned before they run:

```csharp
var agent = new OpenAIChatAgent(client, "assistant", "You are helpful.")
    .RegisterMessageConnector()
    .RegisterGodelGate(gate);
```

Register every agent in a group chat. Streamed text is scanned when the stream ends and cannot be recalled.

## Semantic Kernel

Reference `src/GodelGate.SemanticKernel/GodelGate.SemanticKernel.csproj`, then
`builder.AddGodelGate(gate)` on the `IKernelBuilder`. Filters cover calls through the `Kernel`. A direct
`IChatCompletionService` call bypasses them; wrap its `IChatClient` with `GodelGate.AI` instead.

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
dotnet pack src/GodelGate.AI/GodelGate.AI.csproj -c Release -o artifacts
dotnet pack src/GodelGate.AutoGen/GodelGate.AutoGen.csproj -c Release -o artifacts
dotnet pack src/GodelGate.SemanticKernel/GodelGate.SemanticKernel.csproj -c Release -o artifacts
```

Tests simulate the Console's HTTP contract and use Microsoft's real TurnContext for middleware
checks. They are not evidence of live Gate detector behavior or Microsoft tenant authentication.

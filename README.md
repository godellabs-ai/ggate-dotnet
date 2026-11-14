# Gödel Gate .NET SDK

Version 0.3.1, released with Gödel Gate Console 0.3.1. Targets .NET 8 and .NET 10.
The core package has no third-party runtime dependencies. The optional Microsoft 365 package
uses Microsoft's public middleware contract. See the example and validation instructions below.

## Use the SDK

Pin the release selected by your Console. The versioned GitHub release contains the source and
NuGet package files for a local feed; it does not follow changes on `main`.

```sh
git clone --branch v0.3.1 --depth 1 https://github.com/godellabs-ai/ggate-dotnet.git ../ggate-dotnet
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

Gate is responsible for masking the evaluated event before storage, including nested tool
arguments and detected personal data. Keep Gate updated with its storage-redaction fixes;
an SDK update cannot protect records written by an older server. A block prevents the guarded
action, but the content has already been sent to Gate for evaluation. SDK masking is an optional,
pattern-based privacy control, not a substitute for server-side storage masking.

The API key is exchanged for short-lived bearer tokens via `/api/v1/detection-engine/token`;
refresh tokens rotate via `/api/v1/detection-engine/token/refresh`. Scans use `/api/v1/scan`.
TLS validation is enabled and redirects are disabled. To use a private CA, configure an
`HttpClient` with your organization's trust settings and inject it; never disable TLS validation
for production. A caller-supplied client remains owned by the caller.

Transport/authentication failures return an allowed decision with `FailOpen=true`, matching
the existing SDKs. The exception is a Console outage that Console Settings → General → *Behaviour when
the Console is down* fails closed: a default (fail open or fail closed) plus exceptions that get the
opposite — endpoint users, teams, user tags, coding agents, provider domains, or SDK agent names such as
this client's `AgentName`. Every answered scan carries what applies to its user (`console_outage_action`)
and to this agent's other users (`console_outage_fallback`), remembered per user
(`GateClient.ConsoleOutageAction(user)`). When that answer is `block`, a waited-for scan that cannot reach the Console at all — no connection, or a gateway
answering 502/503/504 for it — returns a block with reason code `console_unreachable` (thrown as
`GateBlockedException` when `Enforce` is set), including during the cooldown. A Console that
answered with an error, a scan still running when `Timeout` elapsed, and a client that has not yet
heard from the Console keep failing open. Caller cancellation propagates. Applications requiring fail-closed behavior
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

## Credential rotation and workload identity

The SDK reads the optional collector config at `GGATE_CONFIG`, otherwise
`$GGATE_HOME/config.yaml` (default `~/.ggate/config.yaml`). Its `api_url` supplies the Console URL
when no explicit URL/environment override is set. The default API key comes from `api_key` in
that file; `GGATE_API_KEY` remains a fallback.

For an organization-managed environment secret, set `GGATE_API_KEY_ENV=COMPANY_GATE_KEY` or
`api_key_env: COMPANY_GATE_KEY` in the config. This selects the **name** of the environment
variable, not the secret. A selected source that is empty does not fall back to a stale config key.
All coding-agent hooks support the same config/environment selector. Environment updates must
reach the agent/service process (usually by relaunching it).

For live SDK rotation, use `GGATE_API_KEY_FILE=/mounted/secret` or a synchronous key-provider
callback. The SDK rereads the selected key source before authentication and discards cached
access/refresh tokens when it changes. Provider, named environment, and mounted file selectors
are authoritative in that order, followed by an explicit API key, config key and legacy environment
fallback. Update the selected source, verify a scan, then revoke the old key; this does not provide
a server-side rotation grace window or promise zero failed in-flight scans.

Enable `GGATE_CLOUD_INSTANCE_METADATA=1` and Console **Settings → General → Cloud instance
metadata** to include allowlisted AWS/GCP/Azure metadata plus pod/function identity. Kubernetes
uses `POD_NAMESPACE`, `POD_NAME`, `POD_UID`, `NODE_NAME`, `POD_SERVICE_ACCOUNT`; Lambda, Cloud Run
and Azure Functions use their service/function/instance variables. Metadata discovery never reads
role credentials or identity tokens. Explicit workload identity is also allowlisted. The discovered
identity is cached per client; recreate the client after a workload move.

Options are `ApiKeyEnv`, `ApiKeyFile`, `ApiKeyProvider`, `CloudInstanceMetadata` and
`WorkloadIdentity`. For example: `new GateOptions { AgentName = "Assistant", Team = "Platform",
ApiKeyProvider = () => secretCache.CurrentKey }`. Per-call `ScanOptions.AgentId` and `AgentType`
are carried on the event session, including tool calls and results.

## Memory protection

Construct `GateMemoryStore<T>(client, putDelegate, getDelegate, options)` around the store used by
your application. `PutAsync` scans before invoking the backing write; `GetAsync` scans before
returning a value. Both request enforcement and propagate `GateBlockedException`. Unwrapped
access and batch/delete APIs are not intercepted. LangChain retriever callback adapters are
available in the Python and Node SDKs; .NET applications can gate retrieved values through this
facade or `ScanToolResultAsync` with `BeforeDelivery = true` and `Enforce = true`.

`await client.ScanMemoryWriteAsync(content)` scans plaintext and metadata before persistence.
It always waits, even in Audit mode, and throws `GateBlockedException` on Block by default.
For manual decision handling use the named `enforce: false` parameter and inspect the result
before writing. Optional `store` and `operation` arguments label the persistence destination.

```csharp
var protectedVectors = new GateVectorStore<string>(client,
    (texts, ct) => PersistEmbeddingsAsync(texts, ct), // your provider write, optionally embedding first
    text => text,
    new ScanOptions { SessionId = "support-session" });
await protectedVectors.UpsertAsync(new[] { "The customer prefers concise answers." });
```

For record objects, supply a `contentOf` selector returning all plaintext and metadata. The
facade materializes and scans the whole batch before invoking the upsert delegate; a block
prevents every record in that batch from being written. Cancellation and provider exceptions
propagate. Use the delegate for your provider's add/update API if that is its persistence path.
Embedding vectors alone cannot expose poisoned instructions; retain the original text for scanning.

Enable **Block poisoned memory writes** in Console Policies and select **Block** (Monitor is the
shipped default). Requires SDK and Gate builds containing this control. The scan uses the normal
pre-action `mcp_tool` contract with tool `memory.write`, including rules, models, attribution and
storage redaction. Transport failures retain the SDK's outage/fail-open behavior. The facade
does not intercept unwrapped writes, provider-managed memory or delete APIs.

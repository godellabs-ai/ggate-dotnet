# Microsoft 365 Status Assistant (.NET)

This .NET 10 Microsoft 365 Agents SDK app uses Gate middleware plus an explicitly guarded
`get_service_status` tool. The tool returns demo data; it makes no model or Graph call.

Set `GGATE_CONSOLE_URL`, `GGATE_API_KEY`, and optionally `GGATE_TEAM`. Configure your Microsoft
agent registration using environment variables or .NET user secrets:

- `TokenValidation__Audiences__0` and `Connections__ServiceConnection__Settings__ClientId`: agent app ID.
- `TokenValidation__TenantId`: tenant ID.
- `Connections__ServiceConnection__Settings__AuthorityEndpoint`: tenant-specific Entra URL.
- `Connections__ServiceConnection__Settings__ClientSecret`: secret stored outside source control.

From this directory:

```sh
dotnet run --urls http://localhost:3978
```

To test locally without Microsoft channel credentials, set only the Gate URL/API key and run
`dotnet run -- --console`. Enter service names on standard input, then an empty line to exit.
This uses Microsoft's real TurnContext with a local channel adapter and sends scans to Gate.

Connect Microsoft's Agents Playground to `/api/messages`, or configure your authenticated HTTPS
deployment for Microsoft 365/Teams. Microsoft's JWT validation remains enabled. Configure the
appropriate Microsoft local-development authentication settings when using Playground; do not
disable authentication on a public deployment. `/health` is host liveness only.
See https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/quickstart for channel setup.

The HTTP example accepts public Azure Bot Service issuer tokens only, with the configured agent
audience. Sovereign-cloud and direct agent-to-agent issuers need their own explicit authentication
configuration. The terminal demo does not open an HTTP endpoint or disable the hosted app's auth.

The SDK scans the incoming message, tool input before execution, tool result before consumption,
and outgoing message before sending it. A block stops the turn; Gate Activity retains the decision.
Remote attachment bytes and internal actions in managed services are outside this example's coverage.
The SDK fails open on scan transport/authentication errors; check `Decision.FailOpen` if your
application requires a stricter availability policy.

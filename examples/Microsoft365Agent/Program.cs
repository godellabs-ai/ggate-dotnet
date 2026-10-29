using GodelGate;
using GodelGate.Microsoft365;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Agents.Core.Models;

var options = new GateOptions { AgentName = "Microsoft 365 Status Assistant",
    Team = Environment.GetEnvironmentVariable("GGATE_TEAM") ?? "Platform Engineering" };
if (string.IsNullOrWhiteSpace(options.ConsoleUrl) || string.IsNullOrWhiteSpace(options.ApiKey))
    throw new InvalidOperationException("Set GGATE_CONSOLE_URL and GGATE_API_KEY before starting this example.");
if (args.Contains("--console"))
{
    await using var gate = new GateClient(options);
    var agent = new StatusAgent(gate, new GateMiddleware(gate));
    while (Console.ReadLine() is { Length: > 0 } text)
    {
        using var context = new TurnContext(new ConsoleAdapter(), new Activity {
            Type = "message", Text = text, Id = Guid.NewGuid().ToString(), ChannelId = "console",
            ServiceUrl = "http://localhost", Conversation = new ConversationAccount { Id = "local-demo" },
            From = new ChannelAccount("local-user"), Recipient = new ChannelAccount("agent") });
        await agent.OnTurnAsync(context, CancellationToken.None);
    }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => new GateClient(options));
builder.Services.AddSingleton<GateMiddleware>();
var audiences = builder.Configuration.GetSection("TokenValidation:Audiences").Get<string[]>() ?? [];
if (audiences.Length == 0 || audiences.Any(a => !Guid.TryParse(a, out _)))
    throw new InvalidOperationException("Set TokenValidation__Audiences__0 to your Microsoft agent application ID.");
// Public Azure Bot Service channel authentication. Other clouds/A2A issuers need their own validation setup.
builder.AddAgentDefaults().AddAgent<StatusAgent>().AddAgentAuthorization(host => {
    host.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt => {
        jwt.MetadataAddress = "https://login.botframework.com/v1/.well-known/openidconfiguration";
        jwt.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true, ValidIssuer = "https://api.botframework.com",
            ValidateAudience = true, ValidAudiences = audiences,
            ValidateLifetime = true, ValidateIssuerSigningKey = true
        };
    });
});
var app = builder.Build();
app.UseAgents();
app.MapDefaultAgentEndpoints();
app.MapGet("/health", () => new { status = "ok" });
app.Run();

// A deterministic tool example: no model or Graph credentials required.
// Middleware is invoked once per turn using the public IMiddleware contract.
sealed class StatusAgent(GateClient gate, GateMiddleware middleware) : IAgent
{
    public async Task OnTurnAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            await middleware.OnTurnAsync(context, async ct => {
                if (context.Activity.Type != "message") return;
                var scan = GateMiddleware.Metadata(context) with { Enforce = true };
                var service = context.Activity.Text ?? "demo";
                await gate.ScanToolCallAsync("get_service_status", new { service }, scan, ct);
                // Replace this operation with your real tool after the preflight.
                var result = $"{service}: healthy (demo data, {DateTimeOffset.UtcNow:O})";
                await gate.ScanToolResultAsync("get_service_status", result, scan, ct);
                await context.SendActivityAsync(result, cancellationToken: ct);
            }, cancellationToken);
        }
        catch (GateBlockedException) { /* Stop delivery; the decision is audited in Gate Activity. */ }
    }
}

sealed class ConsoleAdapter : ChannelAdapter
{
    public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext context, IActivity[] activities, CancellationToken cancellationToken)
    {
        foreach (var activity in activities) Console.WriteLine(activity.Text);
        return Task.FromResult(activities.Select(_ => new ResourceResponse("local")).ToArray());
    }
}

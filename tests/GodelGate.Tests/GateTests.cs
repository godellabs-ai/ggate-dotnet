using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GodelGate.Microsoft365;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Xunit;

namespace GodelGate.Tests;

public sealed class GateTests
{
    private static GateOptions Options => new() { AgentName = "Test agent", Team = "Tests",
        ConsoleUrl = "https://gate.example.test", ApiKey = "test-key", Cooldown = TimeSpan.Zero };

    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonObject> Events { get; } = [];
        public List<string> Paths { get; } = [];
        public bool RejectFirstScan, Fail, Malformed;
        public int Exchanges, Refreshes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (Fail) throw new HttpRequestException("test outage");
            if (path.EndsWith("/refresh"))
            {
                Refreshes++;
                var refresh = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken);
                Assert.Equal("refresh", refresh!["refresh_token"]!.GetValue<string>());
                return Json(new JsonObject { ["access_token"] = "renewed", ["refresh_token"] = "rotated", ["expires_in"] = 3600 });
            }
            if (path.EndsWith("/token"))
            {
                Exchanges++;
                Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
                return Json(new JsonObject { ["access_token"] = "access", ["refresh_token"] = "refresh", ["expires_in"] = 3600 });
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var body = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var ev = (JsonObject)body!["event"]!;
            Events.Add((JsonObject)ev.DeepClone());
            if (RejectFirstScan) { RejectFirstScan = false; return new HttpResponseMessage(HttpStatusCode.Unauthorized); }
            if (Malformed) return Json(new JsonObject());
            bool blocked = ev["payload"]!.ToJsonString().Contains("DENY") && ev["phase"]!.GetValue<string>() == "pre";
            return Json(new JsonObject { ["verdict"] = blocked ? "block" : "pass", ["message"] = "policy decision",
                ["media_gate"] = new JsonObject { ["action"] = "allow" },
                ["pipeline_status"] = new JsonObject { ["warnings"] = new JsonArray() } });
        }
        private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    [Fact]
    public async Task AuthRefreshRetriesSameEventAndRetainsMetadata()
    {
        using var handler = new Handler { RejectFirstScan = true };
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var decision = await gate.ScanPromptAsync("hello", new ScanOptions { SessionId = "session", CorrelationId = "turn" });
        Assert.True(decision.Allowed);
        Assert.NotNull(decision.MediaGate);
        Assert.NotNull(decision.PipelineStatus);
        Assert.Equal(1, handler.Exchanges);
        Assert.Equal(1, handler.Refreshes);
        Assert.Equal(handler.Events[0]["id"]!.GetValue<string>(), handler.Events[1]["id"]!.GetValue<string>());
        Assert.Equal("agent-framework", handler.Events[0]["identity"]!["agent_source"]!.GetValue<string>());
        Assert.Equal("0.2.1", handler.Events[0]["collector"]!["version"]!.GetValue<string>());
        Assert.Equal("turn", handler.Events[0]["session"]!["correlation_id"]!.GetValue<string>());
        Assert.Null(handler.Events[0]["payload"]!["attachments"]);
    }

    [Fact]
    public async Task AllSurfacesUseCurrentWireContract()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var scan = new ScanOptions { Enforce = true, Framework = "microsoft-365-agents" };
        await gate.ScanPromptAsync("hello", scan);
        await gate.ScanResponseAsync("answer", scan);
        await gate.ScanToolCallAsync("lookup", new { query = "hello" }, scan);
        await gate.ScanToolResultAsync("lookup", "result", scan);
        await gate.ScanShellAsync("echo hello", options: scan);
        await gate.ScanFileAsync("demo.txt", content: "file content", options: scan);
        await gate.ScanWebAsync("https://example.com", options: scan);
        Assert.All(handler.Events, e => Assert.Equal("pre", e["phase"]!.GetValue<string>()));
        Assert.Equal(new[] { "prompt", "response", "mcp_tool", "tool_result", "shell", "file_read", "web" },
            handler.Events.Select(e => e["payload"]!["surface"]!.GetValue<string>()));
        // Optional contract export for validation with Gate's Rust RuntimeEvent deserializer.
        if (Environment.GetEnvironmentVariable("GGATE_TEST_EVENT_EXPORT") is { } path)
            await File.WriteAllLinesAsync(path, handler.Events.Select(e => e.ToJsonString()));
    }

    [Fact]
    public async Task EnforceWaitsInAuditModeAndAuditResponseStaysPost()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { Mode = ScanMode.Audit }, http);
        await Assert.ThrowsAsync<GateBlockedException>(() => gate.ScanResponseAsync("DENY", new ScanOptions { Enforce = true }));
        await Assert.ThrowsAsync<GateBlockedException>(() => gate.ScanToolResultAsync("tool", "DENY", new ScanOptions { Enforce = true }));
        await gate.ScanResponseAsync("audit");
        Assert.True(await gate.FlushAsync());
        Assert.Equal("post", handler.Events.Last()["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnavailableAndMalformedAreFailOpenButCancellationPropagates()
    {
        using var handler = new Handler { Fail = true }; using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        Assert.True((await gate.ScanPromptAsync("hello")).FailOpen);
        handler.Fail = false; handler.Malformed = true;
        Assert.True((await gate.ScanPromptAsync("hello")).FailOpen);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ScanPromptAsync("hello", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RedactionAndBoundedAttachmentCapture()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { Redact = true }, http);
        await gate.ScanToolCallAsync("lookup", new { nested = new { password = "password=abcdefghijk" } });
        Assert.DoesNotContain("abcdefghijk", handler.Events.Single().ToJsonString());
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllTextAsync(path, "abcdef");
            var attachment = await GodelGate.Attachment.FromPathAsync(path, true, 3);
            Assert.Equal(6, attachment.Size); Assert.True(attachment.Truncated);
            Assert.Equal("YWJj", attachment.ContentBase64); Assert.Equal(64, attachment.Sha256!.Length);
        } finally { File.Delete(path); }
    }

    private sealed class Adapter : ChannelAdapter
    {
        public List<IActivity> Sent { get; } = [];
        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext context, IActivity[] activities, CancellationToken cancellationToken)
        {
            Sent.AddRange(activities);
            return Task.FromResult(activities.Select(_ => new ResourceResponse("sent")).ToArray());
        }
    }

    [Fact]
    public void ProactiveTurnMetadataIsStableWithoutActivityId()
    {
        using var context = new TurnContext(new Adapter(), new Activity { Type = "message", ChannelId = "console" });
        Assert.Equal(GateMiddleware.Metadata(context).CorrelationId, GateMiddleware.Metadata(context).CorrelationId);
    }

    [Theory]
    [InlineData("incoming")]
    [InlineData("send")]
    [InlineData("batch")]
    [InlineData("update")]
    public async Task RealMicrosoftTurnContextStopsBlockedDelivery(string boundary)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var middleware = new GateMiddleware(gate);
        var adapter = new Adapter();
        using var context = new TurnContext(adapter, new Activity { Type = "message", Id = "turn-1", ChannelId = "msteams",
            ServiceUrl = "https://channel.example.test", Text = boundary == "incoming" ? "DENY" : "hello",
            From = new ChannelAccount("user-1"), Recipient = new ChannelAccount("agent-1"),
            Conversation = new ConversationAccount { Id = "conversation-1", TenantId = "tenant-1" } });
        bool entered = false;
        await Assert.ThrowsAsync<GateBlockedException>(() => middleware.OnTurnAsync(context, async ct => {
            entered = true;
            if (boundary == "update") await context.UpdateActivityAsync(new Activity { Type = "message", Id = "reply", Text = "DENY" }, ct);
            else if (boundary == "batch") await context.SendActivitiesAsync([
                new Activity { Type = "message", Text = "ok" },
                new Activity { Type = "message", Value = new { action = "DENY" } } ], ct);
            else await context.SendActivityAsync("DENY", cancellationToken: ct);
        }));
        Assert.Empty(adapter.Sent);
        Assert.Equal(boundary != "incoming", entered);
        Assert.All(handler.Events, e => Assert.Equal("m365:msteams:tenant-1:conversation-1", e["session"]!["session_id"]!.GetValue<string>()));
    }
}

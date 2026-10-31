using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoGen.Core;
using GodelGate.AI;
using GodelGate.AutoGen;
using GodelGate.Microsoft365;
using GodelGate.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
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
        public bool RejectFirstScan, Fail, Malformed, Locked;
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
            if (Locked) return Json(new JsonObject { ["verdict"] = ev["phase"]!.GetValue<string>() == "pre" ? "block" : "warn",
                ["message"] = "AI agent is contained", ["reason_codes"] = new JsonArray("agent_contained") });
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
        Assert.Equal("0.3.0", handler.Events[0]["collector"]!["version"]!.GetValue<string>());
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

    private sealed class EchoChat(string reply) : IChatClient
    {
        public List<int> Calls { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.Count());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in reply.Split(' ')) { await Task.Yield(); yield return new ChatResponseUpdate(ChatRole.Assistant, word + " "); }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeAgent(IMessage reply) : IStreamingAgent
    {
        public string Name => "fake";
        public int Calls;
        public Task<IMessage> GenerateReplyAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(reply);
        }
        public async IAsyncEnumerable<IMessage> GenerateStreamingReplyAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new TextMessageUpdate(Role.Assistant, "streamed ", "fake");
            yield return new TextMessageUpdate(Role.Assistant, "reply", "fake");
        }
    }

    private static string Text(JsonObject ev) => ev["payload"]!["text"]?.GetValue<string>() ?? "";
    private static string Surface(JsonObject ev) => ev["payload"]!["surface"]!.GetValue<string>();
    private static string Session(JsonObject ev) => ev["session"]!["session_id"]!.GetValue<string>();

    [Fact]
    public async Task ChatClientRecordsOnlyTheNewTurnInOneSession()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        using var chat = new EchoChat("the answer").AsBuilder().UseGodelGate(gate).Build();
        using var session = GateSession.Begin("chat-1");
        await chat.GetResponseAsync([new(ChatRole.System, "be brief"), new(ChatRole.User, "first"),
            new(ChatRole.Assistant, "old answer"), new(ChatRole.User, "second")]);
        Assert.Equal(["prompt", "response"], handler.Events.Select(Surface));
        Assert.Equal("second", Text(handler.Events[0]));
        Assert.Equal("the answer", Text(handler.Events[1]));
        Assert.All(handler.Events, ev => Assert.Equal("chat-1", Session(ev)));
    }

    [Fact]
    public async Task ChatClientRecordsAStreamedAnswerAndCarriesMedia()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { CaptureFileText = true }, http);
        using var chat = new EchoChat("streamed text").AsBuilder().UseGodelGate(gate).Build();
        var message = new ChatMessage(ChatRole.User, [new Microsoft.Extensions.AI.TextContent("listen"), new DataContent(new byte[] { 1, 2, 3 }, "audio/wav")]);
        await foreach (var _ in chat.GetStreamingResponseAsync([message])) { }
        var attachment = handler.Events[0]["payload"]!["attachments"]![0]!;
        Assert.Equal("audio/wav", attachment["mime_type"]!.GetValue<string>());
        Assert.Equal("AQID", attachment["content_base64"]!.GetValue<string>());
        Assert.EndsWith(".wav", attachment["filename"]!.GetValue<string>());
        Assert.Equal("streamed text", Text(handler.Events[1]).Trim());
    }

    [Fact]
    public async Task ChatClientBlockStopsTheModelCall()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var inner = new EchoChat("never");
        using var chat = inner.AsBuilder().UseGodelGate(gate).Build();
        await Assert.ThrowsAsync<GateBlockedException>(() => chat.GetResponseAsync([new(ChatRole.User, "DENY this")]));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task KillSwitchStopsAuditModeHoldsThroughOutageAndReleases()
    {
        using var handler = new Handler { Locked = true };
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { Mode = ScanMode.Audit }, http);
        await gate.ScanPromptAsync("queued");
        await gate.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.Contained);
        await Assert.ThrowsAsync<GateAgentContainedException>(() => gate.ScanPromptAsync("next"));
        handler.Fail = true;
        var held = await Assert.ThrowsAsync<GateAgentContainedException>(() => gate.ScanPromptAsync("during outage"));
        Assert.Contains("Kill Switch held", held.Message);
        handler.Fail = false; handler.Locked = false;
        Assert.False((await gate.ScanPromptAsync("after unlock")).Blocked);
        Assert.False(gate.Contained);
    }

    [Fact]
    public async Task KernelFilterScansPromptPluginAndAnswerAndBlocks()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var builder = Kernel.CreateBuilder().AddGodelGate(gate);
        builder.Services.AddSingleton<IChatCompletionService>(new EchoChat("model answer").AsChatCompletionService());
        var kernel = builder.Build();
        using var session = GateSession.Begin("sk-1");
        var answer = await kernel.InvokePromptAsync("hello there");
        Assert.Equal("model answer", answer.ToString());
        Assert.Equal(["prompt", "response"], handler.Events.Select(Surface));
        Assert.All(handler.Events, ev => Assert.Equal("sk-1", Session(ev)));
        var tool = kernel.CreateFunctionFromMethod((string q) => "ran " + q, "search");
        kernel.Plugins.AddFromFunctions("web", [tool]);
        await kernel.InvokeAsync(kernel.Plugins["web"]["search"], new() { ["q"] = "x" });
        Assert.Contains(handler.Events, ev => Surface(ev) == "mcp_tool");
        await Assert.ThrowsAsync<GateBlockedException>(() => kernel.InvokePromptAsync("DENY this"));
    }

    [Fact]
    public async Task AutoGenRecordsOnlyTheNewTurnInOneSession()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var agent = new FakeAgent(new TextMessage(Role.Assistant, "the answer", "fake")).RegisterGodelGate(gate);
        using var session = GateSession.Begin("autogen-1");
        await agent.GenerateReplyAsync([new TextMessage(Role.User, "first"), new TextMessage(Role.Assistant, "old answer"), new TextMessage(Role.User, "second")]);
        Assert.Equal(["prompt", "response"], handler.Events.Select(Surface));
        Assert.Equal("second", Text(handler.Events[0]));
        Assert.Equal("the answer", Text(handler.Events[1]));
        Assert.All(handler.Events, ev => Assert.Equal("autogen-1", Session(ev)));
    }

    [Fact]
    public async Task AutoGenBlockStopsTheAgentAndToolCallsAreScanned()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var inner = new FakeAgent(new ToolCallMessage([new ToolCall("search", "{}")], "fake"));
        var agent = inner.RegisterGodelGate(gate);
        await Assert.ThrowsAsync<GateBlockedException>(() => agent.GenerateReplyAsync([new TextMessage(Role.User, "DENY this")]));
        Assert.Equal(0, inner.Calls);
        await agent.GenerateReplyAsync([new TextMessage(Role.User, "fine")]);
        Assert.Contains(handler.Events, ev => Surface(ev) == "mcp_tool");
    }

    [Fact]
    public async Task AutoGenStreamIsRecordedAndImagesAttach()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { CaptureFileText = true }, http);
        var agent = new FakeAgent(new TextMessage(Role.Assistant, "x", "fake")).RegisterGodelGate(gate);
        var image = new ImageMessage(Role.User, BinaryData.FromBytes(new byte[] { 1, 2, 3 }, "image/png"));
        var message = new MultiModalMessage(Role.User, [new TextMessage(Role.User, "look"), image]);
        await foreach (var _ in agent.GenerateStreamingReplyAsync([message])) { }
        Assert.Equal("look", Text(handler.Events[0]));
        Assert.Equal("AQID", handler.Events[0]["payload"]!["attachments"]![0]!["content_base64"]!.GetValue<string>());
        Assert.Equal("streamed reply", Text(handler.Events[1]));
    }

    [Fact]
    public async Task AutoGenKillSwitchStopsTheAgent()
    {
        using var handler = new Handler { Locked = true };
        using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        var inner = new FakeAgent(new TextMessage(Role.Assistant, "never", "fake"));
        var agent = inner.RegisterGodelGate(gate);
        await Assert.ThrowsAsync<GateAgentContainedException>(() => agent.GenerateReplyAsync([new TextMessage(Role.User, "hello")]));
        Assert.Equal(0, inner.Calls);
    }
}

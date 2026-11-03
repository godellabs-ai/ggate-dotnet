using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GodelGate.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace GodelGate.Tests;

public sealed class AccessTests
{
    private static GateOptions Options => new() { AgentName = "Operations Assistant", Team = "Platform",
        User = "service@example.com", ConsoleUrl = "https://gate.example.test", ApiKey = "test-key", Cooldown = TimeSpan.Zero };

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<JsonObject> Events { get; } = new();
        public bool Locked, Down;
        public string Verdict = "pass";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("console down");
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Json(new JsonObject { ["access_token"] = "token", ["expires_in"] = 3600 });
            var body = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken);
            var ev = (JsonObject)body!["event"]!;
            Events.Enqueue(ev);
            if (Locked && User(ev) == "alice@example.com" && ev["source"]!["client"]!.GetValue<string>() == "openai")
                return Json(new JsonObject { ["verdict"] = ev["phase"]!.GetValue<string>() == "pre" ? "block" : "warn",
                    ["reason_codes"] = new JsonArray("agent_contained") });
            return Json(new JsonObject { ["verdict"] = Verdict, ["message"] = "Application access policy",
                ["reason_codes"] = new JsonArray("ai_access_restricted"), ["policy_source"] = Policy(),
                ["matched_policies"] = new JsonArray(Policy()) });
        }
        private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
    private static JsonObject Policy() => new() { ["id"] = "access-123", ["name"] = "Engineering only" };
    private static string? User(JsonObject ev) => ev["identity"]!["user"]?.GetValue<string>();

    [Fact]
    public async Task ConcurrentScopesCoverEverySurfaceAndKeepSdkTargets()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        async Task Run(string user)
        {
            using var identity = GateUser.Begin(user);
            using var session = GateSession.Begin(user);
            await Task.Yield();
            var options = new ScanOptions { Framework = "langgraph", Enforce = true };
            await gate.ScanPromptAsync("q", options);
            await gate.ScanResponseAsync("a", options);
            await gate.ScanToolCallAsync("search", new { }, options);
            await gate.ScanToolResultAsync("search", "found", options);
            await gate.ScanShellAsync("pwd", options: options);
            await gate.ScanFileAsync("/tmp/report.txt", options: options);
            await gate.ScanWebAsync("https://example.com", options: options);
        }
        await Task.WhenAll(Run("alice@example.com"), Run("bob@example.com"));
        Assert.Equal(14, handler.Events.Count);
        Assert.All(handler.Events, ev => {
            Assert.Equal(User(ev), ev["session"]!["session_id"]!.GetValue<string>());
            Assert.Equal("Operations Assistant", ev["identity"]!["agent_name"]!.GetValue<string>());
            Assert.Equal("agent-framework", ev["identity"]!["agent_source"]!.GetValue<string>());
            Assert.Equal("langgraph", ev["source"]!["client"]!.GetValue<string>());
            Assert.Equal("langgraph", ev["collector"]!["labels"]!["framework"]!.GetValue<string>());
        });
        Assert.Null(GateUser.Current);
        await gate.ScanPromptAsync("outside");
        Assert.Equal("service@example.com", User(handler.Events.Last()));
    }

    [Fact]
    public async Task ExplicitAnonymousOverridesFallbackAndNestedScopesRestoreAfterErrors()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        using (GateUser.Begin(" alice@example.com "))
        {
            await gate.ScanPromptAsync("override", new ScanOptions { User = "bob@example.com" });
            var anonymous = new ScanOptions { User = null };
            // Cloning options for adapters preserves the explicit anonymous override.
            await gate.ScanPromptAsync("anonymous", anonymous with { Framework = "openai" });
            await gate.ScanPromptAsync("anonymous", new ScanOptions { User = " " });
            await Assert.ThrowsAsync<InvalidOperationException>(async () => {
                using var identity = GateUser.Begin(null);
                await gate.ScanPromptAsync("anonymous");
                throw new InvalidOperationException("app error");
            });
            await gate.ScanPromptAsync("restored");
        }
        await gate.ScanPromptAsync("outside");
        Assert.Equal(new string?[] { "bob@example.com", null, null, null, "alice@example.com", "service@example.com" },
            handler.Events.Select(User));
        Assert.Null(GateUser.Current);
    }

    private sealed class EchoChat : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
        }
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SharedChatAdapterInheritsRequestUser()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options, http);
        using var chat = new EchoChat().AsBuilder().UseGodelGate(gate).Build();
        async Task Run(string user)
        {
            using var identity = GateUser.Begin(user);
            await chat.GetResponseAsync([new(ChatRole.User, "hello")]);
        }
        await Task.WhenAll(Run("alice@example.com"), Run("bob@example.com"));
        foreach (var user in new[] { "alice@example.com", "bob@example.com" })
            Assert.Equal(new[] { "prompt", "response" }, handler.Events.Where(ev => User(ev) == user)
                .Select(ev => ev["payload"]!["surface"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("warn")]
    [InlineData("block")]
    public async Task AccessDecisionsRetainAttributionWithoutLatching(string verdict)
    {
        using var handler = new Handler { Verdict = verdict }; using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { Mode = ScanMode.Audit }, http);
        var options = new ScanOptions { User = "alice@example.com", Enforce = true };
        Decision decision;
        if (verdict == "block")
        {
            var error = await Assert.ThrowsAsync<GateBlockedException>(() => gate.ScanPromptAsync("q", options));
            decision = error.Decision;
        }
        else decision = await gate.ScanPromptAsync("q", options);
        Assert.Equal("access-123", decision.PolicySource!["id"]!.GetValue<string>());
        Assert.Equal("access-123", decision.MatchedPolicies![0]!["id"]!.GetValue<string>());
        Assert.False(gate.Contained);
        handler.Verdict = "pass";
        Assert.True((await gate.ScanPromptAsync("retry", options)).Allowed);
    }

    [Theory]
    [InlineData(ScanMode.Sync)]
    [InlineData(ScanMode.Audit)]
    public async Task KillSwitchIsolatedByUserAndFrameworkIncludingQueuedResults(ScanMode mode)
    {
        using var handler = new Handler { Locked = true }; using var http = new HttpClient(handler);
        await using var gate = new GateClient(Options with { Mode = mode }, http);
        var alice = new ScanOptions { User = "alice@example.com", Framework = "openai" };
        if (mode == ScanMode.Sync)
            await Assert.ThrowsAsync<GateAgentContainedException>(() => gate.ScanPromptAsync("q", alice));
        else
        {
            using (GateUser.Begin(alice.User))
                await gate.ScanPromptAsync("q", new ScanOptions { Framework = "openai" });
            Assert.True(await gate.FlushAsync());
        }
        Assert.True(gate.Contained);
        await gate.ScanPromptAsync("other user", alice with { User = "bob@example.com", Enforce = true });
        await gate.ScanPromptAsync("other framework", alice with { Framework = "langchain", Enforce = true });
        Assert.True(gate.Contained);
        handler.Down = true;
        await Assert.ThrowsAsync<GateAgentContainedException>(() => gate.ScanPromptAsync("locked", alice with { User = "ALICE@example.com" }));
        Assert.True((await gate.ScanPromptAsync("unlocked", alice with { User = "bob@example.com", Enforce = true })).FailOpen);
        Assert.True((await gate.ScanPromptAsync("unlocked", alice with { Framework = "langchain", Enforce = true })).FailOpen);
        handler.Down = false; handler.Locked = false;
        await gate.ScanPromptAsync("released", alice);
        Assert.False(gate.Contained);
    }
}

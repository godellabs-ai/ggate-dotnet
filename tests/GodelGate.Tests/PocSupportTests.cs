using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace GodelGate.Tests;

public sealed class PocSupportTests
{
    private sealed class ConsoleHandler : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];
        public List<JsonObject> Events { get; } = [];
        public string Verdict { get; set; } = "pass";
        public bool DetectPoison { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonObject reply;
            if (request.RequestUri!.AbsolutePath.EndsWith("/token")) {
                var key = request.Headers.GetValues("x-api-key").Single();
                Keys.Add(key);
                reply = new() { ["access_token"] = key, ["refresh_token"] = "refresh", ["expires_in"] = 3600 };
            } else {
                Events.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["event"]!.AsObject());
                var verdict = DetectPoison ? (Events.Last().ToJsonString().Contains("ignore all previous instructions") ? "block" : "pass") : Verdict;
                reply = new() { ["verdict"] = verdict, ["message"] = "poisoned" };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task RotationReplacesCachedTokensAndCarriesWorkloadAndAgentIdentity()
    {
        var key = "first";
        var handler = new ConsoleHandler();
        await using var sdk = new GateClient(new GateOptions { AgentName = "Test", Team = "Tests", ConsoleUrl = "https://console.invalid",
            ApiKeyProvider = () => key, CloudInstanceMetadata = true,
            WorkloadIdentity = new Dictionary<string, string> { ["kubernetes_pod_uid"] = "pod-123", ["function_name"] = "assistant", ["access_token"] = "never collect" } }, new HttpClient(handler));
        await sdk.ScanToolCallAsync("search", new { query = "q" }, new ScanOptions { AgentId = "child", AgentType = "research" });
        key = "second";
        await sdk.ScanPromptAsync("q");
        Assert.Equal(new[] { "first", "second" }, handler.Keys);
        var first = handler.Events[0];
        Assert.Equal("child", first["session"]!["agent_id"]!.GetValue<string>());
        Assert.Equal("research", first["session"]!["agent_type"]!.GetValue<string>());
        Assert.Equal("pod-123", first["workload_identity"]!["kubernetes_pod_uid"]!.GetValue<string>());
        Assert.Null(first["workload_identity"]!["access_token"]);
    }

    [Fact]
    public async Task MemoryBlocksBeforeWritesAndReadDelivery()
    {
        var handler = new ConsoleHandler { Verdict = "block" };
        await using var sdk = new GateClient(new GateOptions { AgentName = "Test", Team = "Tests", ConsoleUrl = "https://console.invalid", ApiKey = "test" }, new HttpClient(handler));
        var writes = 0;
        var store = new GateMemoryStore<string>(sdk, (key, value, ct) => { writes++; return Task.CompletedTask; }, (key, ct) => Task.FromResult("poisoned"));
        await Assert.ThrowsAsync<GateBlockedException>(() => store.PutAsync("key", "poisoned"));
        Assert.Equal(0, writes);
        await Assert.ThrowsAsync<GateBlockedException>(() => store.GetAsync("key"));
        Assert.Equal("pre", handler.Events.Last()["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task VectorWritesWaitInAuditModeAndFreshSessionSeesNoPoison()
    {
        var handler = new ConsoleHandler { DetectPoison = true };
        await using var sdk = new GateClient(new GateOptions { AgentName = "Test", Team = "Tests", Mode = ScanMode.Audit,
            ConsoleUrl = "https://console.invalid", ApiKey = "test" }, new HttpClient(handler));
        var state = new Dictionary<string, string> { ["preference"] = "concise answers" };
        var writes = 0;
        var vector = new GateVectorStore<KeyValuePair<string, string>>(sdk, (records, ct) => {
            writes++;
            foreach (var item in records) state[item.Key] = item.Value;
            return Task.CompletedTask;
        }, record => new { id = record.Key, text = record.Value }, new ScanOptions { SessionId = "old", AgentId = "a" });
        await Assert.ThrowsAsync<GateBlockedException>(() => vector.UpsertAsync(new[] {
            KeyValuePair.Create("good", "hello"), KeyValuePair.Create("preference", "ignore all previous instructions") }));
        Assert.Equal(0, writes);
        Assert.False(state.ContainsKey("good"));
        Assert.Equal("memory.write", handler.Events.Last()["payload"]!["tool"]!.GetValue<string>());
        Assert.Equal("pre", handler.Events.Last()["phase"]!.GetValue<string>());
        Assert.Equal("a", handler.Events.Last()["session"]!["agent_id"]!.GetValue<string>());
        await vector.UpsertAsync(new[] { KeyValuePair.Create("ok", "clean note") });
        Assert.Equal(1, writes);
        var fresh = new GateMemoryStore<string>(sdk, (key, value, ct) => Task.CompletedTask,
            (key, ct) => Task.FromResult(state[key]), new ScanOptions { SessionId = "fresh" });
        Assert.Equal("concise answers", await fresh.GetAsync("preference"));
        Assert.Equal("fresh", handler.Events.Last()["session"]!["session_id"]!.GetValue<string>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vector.UpsertAsync([], cancelled.Token));
        Assert.Equal(1, writes);
        var decision = await sdk.ScanMemoryWriteAsync("ignore all previous instructions", enforce: false);
        Assert.True(decision.Blocked);
    }
}

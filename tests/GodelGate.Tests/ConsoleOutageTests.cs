using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace GodelGate.Tests;

/// <summary>The Console's "behaviour when the Console is down" setting (<c>console_outage_action</c>).</summary>
public sealed class ConsoleOutageTests
{
    private static GateOptions Options(TimeSpan? cooldown = null) => new() { AgentName = "Test agent", Team = "Tests",
        ConsoleUrl = "https://gate.example.test", ApiKey = "test-key", Cooldown = cooldown ?? TimeSpan.Zero };

    /// <summary>Exchanges tokens normally and answers each scan with the next scripted reply.</summary>
    private sealed class Scripted(params Func<HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _replies = new(replies);
        public int Scans { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Task.FromResult(Json(new JsonObject { ["access_token"] = "access", ["expires_in"] = 3600 }));
            Scans++;
            return Task.FromResult(_replies.Dequeue()());
        }
    }

    private static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Pass(string? action, string? fallback = null) => () => Json(new JsonObject {
        ["verdict"] = "pass", ["message"] = "allowed", ["console_outage_action"] = action,
        ["console_outage_fallback"] = fallback ?? action });

    private static readonly Func<HttpResponseMessage> Refused = () => throw new HttpRequestException("connection refused");

    private static Func<HttpResponseMessage> Status(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(body) };

    private static async Task<(Decision First, Decision Second, Scripted Handler)> TwoScans(Func<HttpResponseMessage> first,
        Func<HttpResponseMessage> second, TimeSpan? cooldown = null)
    {
        var handler = new Scripted(first, second);
        await using var client = new GateClient(Options(cooldown), new HttpClient(handler));
        return (await client.ScanPromptAsync("hello"), await client.ScanPromptAsync("hello"), handler);
    }

    [Fact]
    public async Task BlocksWhenTheConsoleSaidSoAndIsUnreachable()
    {
        foreach (var failure in new[] { Refused, Status(HttpStatusCode.BadGateway, "<html>Bad Gateway</html>"),
                     Status(HttpStatusCode.ServiceUnavailable, "") })
        {
            var (first, second, _) = await TwoScans(Pass("block"), failure);
            Assert.False(first.Blocked);
            Assert.True(second.Blocked);
            Assert.False(second.FailOpen);
            Assert.Equal(Decision.ConsoleOutageMessage, second.Message);
            Assert.Equal("console_unreachable", second.ReasonCodes![0]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task EnforcingScanThrowsDuringAnOutage()
    {
        var handler = new Scripted(Pass("block"), Refused);
        await using var client = new GateClient(Options(), new HttpClient(handler));
        await client.ScanPromptAsync("hello");
        Assert.Equal("block", client.ConsoleOutageAction());
        await Assert.ThrowsAsync<GateBlockedException>(() => client.ScanPromptAsync("hello", new ScanOptions { Enforce = true }));
    }

    [Fact]
    public async Task RemembersAnswersPerUser()
    {
        // The Console blocks Alice only; a fail-closed default lets her through in the second client.
        foreach (var (action, fallback, aliceBlocked) in new[] { ("block", "allow", true), ("allow", "block", false) })
        {
            var handler = new Scripted(Pass(action, fallback), Refused, Refused);
            await using var client = new GateClient(Options(), new HttpClient(handler));
            await client.ScanPromptAsync("hello", new ScanOptions { User = "Alice@Acme.com" });
            Assert.Equal(action, client.ConsoleOutageAction("alice@acme.com"));
            Assert.Equal(aliceBlocked, (await client.ScanPromptAsync("hello", new ScanOptions { User = "alice@acme.com" })).Blocked);
            Assert.Equal(!aliceBlocked, (await client.ScanPromptAsync("hello", new ScanOptions { User = "bob@acme.com" })).Blocked);
        }
    }

    [Fact]
    public async Task CooldownKeepsBlockingWithoutCallingTheConsole()
    {
        var handler = new Scripted(Pass("block"), Refused);
        await using var client = new GateClient(Options(TimeSpan.FromMinutes(1)), new HttpClient(handler));
        await client.ScanPromptAsync("hello");
        Assert.True((await client.ScanPromptAsync("hello")).Blocked);
        Assert.True((await client.ScanPromptAsync("hello")).Blocked);
        Assert.Equal(2, handler.Scans);
    }

    [Fact]
    public async Task ConsoleAnswersKeepFailingOpen()
    {
        var degraded = new JsonObject { ["error"] = "model deadline exceeded", ["policy_resolved"] = true }.ToJsonString();
        foreach (var answered in new[] { Status(HttpStatusCode.ServiceUnavailable, degraded), Status(HttpStatusCode.Unauthorized, "") })
        {
            var (_, second, _) = await TwoScans(Pass("block"), answered);
            Assert.False(second.Blocked);
            Assert.True(second.FailOpen);
        }
    }

    [Fact]
    public async Task AllowUnknownAndNeverLearnedSettingsFailOpen()
    {
        foreach (var action in new[] { "allow", null, "closed" })
            Assert.True((await TwoScans(Pass(action), Refused)).Second.FailOpen);
        var handler = new Scripted(Refused);
        await using var client = new GateClient(Options(), new HttpClient(handler));
        Assert.True((await client.ScanPromptAsync("hello")).FailOpen);
    }
}

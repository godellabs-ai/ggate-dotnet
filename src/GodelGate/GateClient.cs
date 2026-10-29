using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace GodelGate;

/// <summary>Scans against the Console; no endpoint agent is required. Transport failures fail open.</summary>
public sealed class GateClient : IAsyncDisposable
{
    public const string Version = "0.2.1";
    public GateOptions Options { get; }
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly Channel<JsonObject> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly string _session = "dotnet-sdk-" + Guid.NewGuid();
    private string? _accessToken, _refreshToken;
    private DateTimeOffset _expires;
    private long _retryAfter, _pending;
    private bool _disposed;

    public GateClient(GateOptions options, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(options.AgentName) || string.IsNullOrWhiteSpace(options.Team))
            throw new ArgumentException("AgentName and Team are required (or set GGATE_AGENT_NAME and GGATE_TEAM).");
        if (options.Timeout <= TimeSpan.Zero || options.Cooldown < TimeSpan.Zero || options.QueueCapacity < 1)
            throw new ArgumentException("Timeout and QueueCapacity must be positive; Cooldown cannot be negative.");
        if (options.ConsoleUrl is not null && (!Uri.TryCreate(options.ConsoleUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("ConsoleUrl must be an HTTP(S) URL without credentials.");
        Options = options;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = httpClient is null;
        _queue = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(options.QueueCapacity) {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = DrainAsync();
    }

    public Task<Decision> ScanPromptAsync(string text, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "prompt", ["text"] = text, ["attachments"] = Wire.Node(options?.Attachments) }, text, false, options, cancellationToken);

    public Task<Decision> ScanResponseAsync(string text, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "response", ["text"] = text, ["model"] = options?.Model,
            ["attachments"] = Wire.Node(options?.Attachments), ["thinking"] = options?.Thinking,
            ["stop_reason"] = options?.StopReason, ["usage"] = Wire.Node(options?.Usage), ["duration_ms"] = options?.DurationMs }, text, true, options, cancellationToken);

    public Task<Decision> ScanToolCallAsync(string tool, object? input, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "mcp_tool", ["tool"] = tool,
            ["server"] = options?.Server ?? options?.Framework ?? "generic", ["service"] = options?.Service,
            ["server_info"] = Wire.Node(options?.ServerInfo), ["input_summary"] = Wire.Node(input) },
            Wire.Node(input)?.ToJsonString() ?? tool, false, options, cancellationToken);

    public Task<Decision> ScanToolResultAsync(string tool, string output, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "tool_result", ["tool"] = tool, ["output"] = output,
            ["kind"] = options?.Kind ?? "other", ["server"] = options?.Server, ["service"] = options?.Service,
            ["ok"] = options?.Ok ?? true, ["truncated"] = options?.Truncated ?? false,
            ["output_len"] = options?.OutputLength, ["exit_code"] = options?.ExitCode, ["duration_ms"] = options?.DurationMs }, output, true, options, cancellationToken);

    public Task<Decision> ScanShellAsync(string command, string[]? argv = null, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "shell", ["command"] = command, ["argv"] = Wire.Node(argv ?? []) }, command, false, options, cancellationToken);

    public Task<Decision> ScanFileAsync(string path, string action = "read", string? content = null, ScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (action is not ("read" or "write" or "edit" or "delete")) throw new ArgumentException("Invalid file action", nameof(action));
        return ScanAsync(new JsonObject { ["surface"] = action == "read" ? "file_read" : "file_write", ["path"] = path,
            ["action"] = action, ["content"] = content }, content ?? path, false, options, cancellationToken);
    }

    public Task<Decision> ScanWebAsync(string url, string? method = null, ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        ScanAsync(new JsonObject { ["surface"] = "web", ["url"] = url, ["method"] = method,
            ["domain"] = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null }, url, false, options, cancellationToken);

    private async Task<Decision> ScanAsync(JsonObject payload, string primaryText, bool post, ScanOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        options ??= new ScanOptions();
        if (!Options.Enabled) return Decision.Pass();
        var pre = !post || options.BeforeDelivery || options.Enforce;
        var wait = options.Wait || options.Enforce || options.BeforeDelivery || (!post && Options.Mode == ScanMode.Sync);
        int redactions = 0;
        if (Options.Redact) Redact(payload, ref redactions);
        var labels = Options.Labels.ToDictionary(x => x.Key, x => x.Value);
        labels["language"] = "dotnet"; labels["framework"] = options.Framework;
        var request = new JsonObject {
            ["event"] = new JsonObject {
                ["id"] = Guid.NewGuid().ToString(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["phase"] = pre ? "pre" : "post",
                ["identity"] = Wire.Node(new { org_id = Options.OrgId, user = Options.User, host = Options.Host,
                    workstation_id = Options.WorkstationId, agent_source = "agent-framework", agent_name = Options.AgentName,
                    team = Options.Team, agent_version = Version, install_source = "manual" }),
                ["session"] = Wire.Node(new { session_id = options.SessionId ?? _session, correlation_id = options.CorrelationId,
                    parent_event_id = options.ParentEventId, repo = options.Repo, source_trust = options.SourceTrust, permission_mode = options.PermissionMode }),
                ["collector"] = Wire.Node(new { collector_id = Options.CollectorId ?? $"{Options.WorkstationId}:ggate-dotnet-sdk",
                    collector_type = "sdk", name = "ggate-dotnet-sdk", version = Version,
                    mode = wait ? "enforce" : "audit", labels,
                    platform = new { os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
                        arch = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "aarch64", Architecture.X64 => "x86_64", var a => a.ToString().ToLowerInvariant() } } }),
                ["source"] = Wire.Node(new { surface = "api", client = options.Framework, provider = options.Provider,
                    model = options.Model, conversation_id = options.ConversationId, request_id = options.RequestId, detail = options.SourceDetail }),
                ["payload"] = payload },
            ["redaction"] = Wire.Node(new { redaction_count = redactions, content_sha256 = Options.Redact ?
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(primaryText))).ToLowerInvariant() : null }) };
        RemoveNulls(request);
        Decision decision;
        if (wait) decision = await SendAsync(request, ct);
        else
        {
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(request))
            {
                Interlocked.Decrement(ref _pending);
                return Decision.Unavailable("Audit queue is full; event was not delivered");
            }
            decision = Decision.Pass();
        }
        if (options.Enforce && decision.Blocked) throw new GateBlockedException(decision);
        return decision;
    }

    private async Task<Decision> SendAsync(JsonObject request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Options.ConsoleUrl) || string.IsNullOrWhiteSpace(Options.ApiKey))
            return Decision.Unavailable("GGATE_CONSOLE_URL and GGATE_API_KEY are required to scan");
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _retryAfter))
            return Decision.Unavailable("Console unavailable (cooling down after failure)");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Options.Timeout);
        try
        {
            var token = await TokenAsync(null, budget.Token);
            using var first = await PostAsync("/api/v1/scan", request, token, budget.Token);
            JsonObject result;
            if (first.StatusCode == HttpStatusCode.Unauthorized)
            {
                token = await TokenAsync(token, budget.Token);
                using var retry = await PostAsync("/api/v1/scan", request, token, budget.Token);
                result = await ReadAsync(retry, budget.Token);
            }
            else result = await ReadAsync(first, budget.Token);
            var decision = Decision.FromJson(result);
            Interlocked.Exchange(ref _retryAfter, decision.FailOpen ? (DateTimeOffset.UtcNow + Options.Cooldown).UtcTicks : 0);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Interlocked.Exchange(ref _retryAfter, (DateTimeOffset.UtcNow + Options.Cooldown).UtcTicks);
            return Decision.Unavailable($"Console scan unavailable ({ex.GetType().Name})");
        }
    }

    private async Task<string> TokenAsync(string? rejectedToken, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _accessToken != rejectedToken && _expires > DateTimeOffset.UtcNow.AddSeconds(60)) return _accessToken;
            if (_refreshToken is not null)
            {
                using var response = await PostAsync("/api/v1/detection-engine/token/refresh", new JsonObject { ["refresh_token"] = _refreshToken }, null, ct);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    return AcceptToken(await ReadAsync(response, ct));
                _refreshToken = null;
            }
            using var exchange = new HttpRequestMessage(HttpMethod.Post, Options.ConsoleUrl!.TrimEnd('/') + "/api/v1/detection-engine/token");
            exchange.Headers.Add("x-api-key", Options.ApiKey);
            using var exchanged = await _http.SendAsync(exchange, ct);
            return AcceptToken(await ReadAsync(exchanged, ct));
        }
        finally { _authLock.Release(); }
    }

    private string AcceptToken(JsonObject data)
    {
        var token = data["access_token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Missing access token");
        _accessToken = token; _refreshToken = data["refresh_token"]?.GetValue<string>();
        _expires = DateTimeOffset.UtcNow.AddSeconds(data["expires_in"]?.GetValue<double>() ?? 3600);
        return token;
    }

    private Task<HttpResponseMessage> PostAsync(string path, JsonObject body, string? token, CancellationToken ct)
    {
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Options.ConsoleUrl!.TrimEnd('/') + path) { Content = JsonContent.Create(body) };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _http.SendAsync(request, ct);
        }
    }
    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidOperationException("Empty Console response");
    }
    private async Task DrainAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token))
                try { await SendAsync(request, _shutdown.Token); } finally { Interlocked.Decrement(ref _pending); }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    public async Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (Interlocked.Read(ref _pending) > 0)
        {
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(10, cancellationToken);
        }
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _queue.Writer.TryComplete();
        await FlushAsync(); _shutdown.Cancel(); await _worker;
        _shutdown.Dispose(); _authLock.Dispose(); if (_ownsHttp) _http.Dispose();
    }

    private static readonly Regex[] Secrets = [
        new(@"sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new("(api[_-]?key|token|secret|password)\\s*[:=]\\s*['\"]?[^'\"\\s]{8,}", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled, TimeSpan.FromSeconds(1)) ];
    private static void RemoveNulls(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToArray())
                if (value is null) obj.Remove(key); else RemoveNulls(value);
        else if (node is JsonArray array)
            foreach (var value in array) if (value is not null) RemoveNulls(value);
    }
    private static void Redact(JsonNode node, ref int count)
    {
        IEnumerable<(string? Key, int Index, JsonNode? Value)> children = node is JsonObject obj
            ? obj.Select(x => ((string?)x.Key, 0, x.Value)).ToArray()
            : node is JsonArray arr ? arr.Select((x, i) => ((string?)null, i, x)).ToArray() : [];
        foreach (var (key, index, value) in children)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                foreach (var regex in Secrets) { count += regex.Matches(text).Count; text = regex.Replace(text, "[REDACTED]"); }
                if (key is not null) node[key] = text; else node[index] = text;
            }
            else if (value is not null) Redact(value, ref count);
        }
    }
}

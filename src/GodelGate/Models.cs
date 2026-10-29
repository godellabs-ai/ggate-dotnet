using System.Text.Json;
using System.Text.Json.Nodes;

namespace GodelGate;

public enum ScanMode { Sync, Audit }

public sealed record GateOptions
{
    public string AgentName { get; init; } = Environment.GetEnvironmentVariable("GGATE_AGENT_NAME") ?? "";
    public string Team { get; init; } = Environment.GetEnvironmentVariable("GGATE_TEAM") ?? "";
    public string? ConsoleUrl { get; init; } = Environment.GetEnvironmentVariable("GGATE_CONSOLE_URL");
    public string? ApiKey { get; init; } = Environment.GetEnvironmentVariable("GGATE_API_KEY");
    public string OrgId { get; init; } = Environment.GetEnvironmentVariable("GGATE_ORG_ID") ?? "local";
    public string Host { get; init; } = Environment.MachineName;
    public string? User { get; init; } = Environment.GetEnvironmentVariable("GGATE_USER");
    public string WorkstationId { get; init; } = Environment.GetEnvironmentVariable("GGATE_WORKSTATION_ID") ?? Environment.MachineName;
    public string? CollectorId { get; init; } = Environment.GetEnvironmentVariable("GGATE_COLLECTOR_ID");
    public ScanMode Mode { get; init; } = Environment.GetEnvironmentVariable("GGATE_MODE") == "async" ? ScanMode.Audit : ScanMode.Sync;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("GGATE_TIMEOUT_MS"), out var value) ? value : 4000);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
    public int QueueCapacity { get; init; } = 1024;
    public bool Redact { get; init; } = Environment.GetEnvironmentVariable("GGATE_REDACT") == "1";
    public bool Enabled { get; init; } = Environment.GetEnvironmentVariable("GGATE_DISABLED") != "1";
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

public sealed record ScanOptions
{
    public string Framework { get; init; } = "generic";
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? SessionId { get; init; }
    public string? ConversationId { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public Guid? ParentEventId { get; init; }
    public string? Repo { get; init; }
    public string? SourceTrust { get; init; }
    public string? PermissionMode { get; init; }
    public JsonNode? SourceDetail { get; init; }
    public IReadOnlyList<Attachment>? Attachments { get; init; }
    public string? Server { get; init; }
    public string? Service { get; init; }
    public JsonNode? ServerInfo { get; init; }
    public string? Kind { get; init; }
    public string? Thinking { get; init; }
    public string? StopReason { get; init; }
    public IReadOnlyDictionary<string, long>? Usage { get; init; }
    public long? DurationMs { get; init; }
    public bool? Ok { get; init; }
    public bool Truncated { get; init; }
    public long? OutputLength { get; init; }
    public int? ExitCode { get; init; }
    public bool Wait { get; init; }
    /// <summary>Wait and throw GateBlockedException on a block; hold response/result delivery until it returns.</summary>
    public bool Enforce { get; init; }
    /// <summary>The application still controls delivery. Emits phase=pre and waits for a verdict.</summary>
    public bool BeforeDelivery { get; init; }
}

public sealed record Attachment
{
    public string? Id { get; init; }
    public string? Filename { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }
    public string? Sha256 { get; init; }
    public string Source { get; init; } = "upload";
    public string Capture { get; init; } = "metadata_only";
    public string? Text { get; init; }
    public string? ContentBase64 { get; init; }
    public bool Truncated { get; init; }

    public static async Task<Attachment> FromPathAsync(string path, bool captureContent = false,
        int maxBytes = 65536, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        await using var file = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellationToken);
        file.Position = 0;
        byte[]? content = null;
        if (captureContent)
        {
            content = new byte[(int)Math.Min(file.Length, maxBytes)];
            await file.ReadExactlyAsync(content, cancellationToken);
        }
        return new Attachment { Filename = Path.GetFileName(path), Size = file.Length,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(), Source = "file_ref",
            Capture = captureContent ? "content" : "metadata_only",
            ContentBase64 = content is null ? null : Convert.ToBase64String(content),
            Truncated = captureContent && file.Length > maxBytes };
    }
}

public sealed class Decision
{
    public JsonObject Raw { get; }
    public string Verdict { get; }
    public string Message => Raw["message"]?.GetValue<string>() ?? "allowed";
    public bool Blocked => Verdict == "block";
    public bool Allowed => !Blocked;
    public bool FailOpen => Raw["fail_open"]?.GetValue<bool>() ?? false;
    public JsonNode? ReasonCodes => Raw["reason_codes"];
    public JsonNode? MatchedDetectors => Raw["matched_detectors"];
    public JsonNode? PolicySource => Raw["policy_source"];
    public JsonNode? Detection => Raw["detection"];
    public JsonNode? DocumentIntelligence => Raw["document_intelligence"];
    public JsonNode? PipelineStatus => Raw["pipeline_status"];
    public JsonNode? MediaGate => Raw["media_gate"];

    private Decision(JsonObject raw, string verdict) { Raw = raw; Verdict = verdict; }
    public static Decision FromJson(JsonObject raw)
    {
        var verdict = raw["verdict"]?.GetValue<string>();
        if (verdict == "hard_block") verdict = "block";
        var kind = raw["kind"]?.GetValue<string>();
        if (kind is not (null or "verdict") || verdict is not ("pass" or "warn" or "block" or "system"))
            return Unavailable("Console returned an invalid scan decision");
        return new Decision((JsonObject)raw.DeepClone(), verdict);
    }
    public static Decision Pass() => new(new JsonObject { ["verdict"] = "pass", ["message"] = "allowed" }, "pass");
    public static Decision Unavailable(string message) => new(new JsonObject {
        ["verdict"] = "pass", ["message"] = message, ["fail_open"] = true,
        ["reason_codes"] = new JsonArray("engine_unreachable") }, "pass");
}

public sealed class GateBlockedException(Decision decision) : Exception(decision.Message)
{
    public Decision Decision { get; } = decision;
}

internal static class Wire
{
    public static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    public static JsonNode? Node(object? value) => value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Json);
}

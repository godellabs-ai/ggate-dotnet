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
    /// <summary>Seat identity, as the Python and Node SDKs resolve it. Kill Switch locks match on it.</summary>
    public string? User { get; init; } = Environment.GetEnvironmentVariable("GGATE_USER")
        ?? Environment.GetEnvironmentVariable("GGATE_USER_EMAIL") ?? $"{Environment.UserName}@{Environment.MachineName}";
    public string WorkstationId { get; init; } = Environment.GetEnvironmentVariable("GGATE_WORKSTATION_ID") ?? Environment.MachineName;
    public string? CollectorId { get; init; } = Environment.GetEnvironmentVariable("GGATE_COLLECTOR_ID");
    public ScanMode Mode { get; init; } = Environment.GetEnvironmentVariable("GGATE_MODE") == "async" ? ScanMode.Audit : ScanMode.Sync;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("GGATE_TIMEOUT_MS"), out var value) ? value : 4000);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
    public int QueueCapacity { get; init; } = 1024;
    public bool Redact { get; init; } = Environment.GetEnvironmentVariable("GGATE_REDACT") == "1";
    /// <summary>Send file and media content for scanning. Off, the Console only sees metadata.</summary>
    public bool CaptureFileText { get; init; } = Environment.GetEnvironmentVariable("GGATE_CAPTURE_FILE_TEXT") == "1";
    public int MaxFileBytes { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("GGATE_MAX_FILE_BYTES"), out var bytes) ? bytes : 65536;
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

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv", [".html"] = "text/html", [".json"] = "application/json",
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac", [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation" };

    public static async Task<Attachment> FromPathAsync(string path, bool captureContent = false,
        int maxBytes = 65536, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        await using var file = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellationToken);
        file.Position = 0;
        var mime = MimeTypes.GetValueOrDefault(Path.GetExtension(path));
        byte[]? content = null;
        if (captureContent)
        {
            content = new byte[(int)Math.Min(file.Length, maxBytes)];
            await file.ReadExactlyAsync(content, cancellationToken);
        }
        return WithContent(new Attachment { Filename = Path.GetFileName(path), MimeType = mime, Size = file.Length,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(), Source = "file_ref",
            Truncated = captureContent && file.Length > maxBytes }, content);
    }

    /// <summary>An attachment for in-memory content: an upload, or a framework's image or audio data.</summary>
    public static Attachment FromBytes(ReadOnlySpan<byte> data, string? mimeType = null, string? filename = null,
        bool captureContent = false, int maxBytes = 65536, string source = "upload")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        mimeType ??= filename is null ? null : MimeTypes.GetValueOrDefault(Path.GetExtension(filename));
        // Always name the file with an extension: Console file-type policies match on it.
        if (mimeType is not null && (filename is null || !Path.HasExtension(filename)))
            filename = (filename ?? "attachment") + (MimeTypes.FirstOrDefault(x => x.Value == mimeType).Key ?? "");
        var bounded = data[..Math.Min(data.Length, maxBytes)];
        return WithContent(new Attachment { Filename = filename, MimeType = mimeType, Size = data.Length, Source = source,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
            Truncated = captureContent && data.Length > maxBytes }, captureContent ? bounded.ToArray() : null);
    }

    private static Attachment WithContent(Attachment attachment, byte[]? content)
    {
        if (content is null) return attachment;
        return attachment.MimeType?.StartsWith("text/", StringComparison.Ordinal) == true
            ? attachment with { Capture = "text", Text = System.Text.Encoding.UTF8.GetString(content) }
            : attachment with { Capture = "content", ContentBase64 = Convert.ToBase64String(content) };
    }
}

/// <summary>One conversation: every scan in this scope, across adapters and awaits, joins one session.</summary>
public static class GateSession
{
    private static readonly AsyncLocal<string?> Bound = new();
    public static string? Current => Bound.Value;

    /// <summary>Pass the app's conversation id so it stays one session across requests.</summary>
    public static IDisposable Begin(string? sessionId = null)
    {
        var previous = Bound.Value;
        Bound.Value = sessionId ?? "dotnet-sdk-" + Guid.NewGuid();
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Bound.Value = previous;
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
    /// <summary>The Kill Switch has locked this agent. Also true on a warn for an already delivered event.</summary>
    public bool Contained => Raw["reason_codes"] is JsonArray codes && codes.Any(code => code?.GetValue<string>() == "agent_contained");
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
    /// <summary>Keep a known lock in force while the Console cannot confirm its release.</summary>
    public static Decision KillSwitchHeld(Decision last, string reason)
    {
        var raw = (JsonObject)last.Raw.DeepClone();
        raw["verdict"] = "block"; raw["fail_open"] = false; raw["message"] = $"{last.Message} (Kill Switch held: {reason})";
        ((JsonArray)raw["reason_codes"]!).Add("kill_switch_held");
        return new Decision(raw, "block");
    }
    public static Decision Unavailable(string message) => new(new JsonObject {
        ["verdict"] = "pass", ["message"] = message, ["fail_open"] = true,
        ["reason_codes"] = new JsonArray("engine_unreachable") }, "pass");
}

public class GateBlockedException(Decision decision) : Exception(decision.Message)
{
    public Decision Decision { get; } = decision;
}

/// <summary>The Kill Switch has locked this agent. Thrown even without Enforce, including in audit mode.</summary>
public sealed class GateAgentContainedException(Decision decision) : GateBlockedException(decision);

internal static class Wire
{
    public static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    public static JsonNode? Node(object? value) => value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Json);
}

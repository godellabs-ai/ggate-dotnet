using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace GodelGate.AI;

/// <summary>
/// Scans every model call of an IChatClient: the frameworks built on Microsoft.Extensions.AI (Microsoft
/// Agent Framework among them) reach the model through one. Put it inside function invocation so it sees
/// each step: <c>builder.UseFunctionInvocation().UseGodelGate(gate)</c>.
/// </summary>
public sealed class GateChatClient(IChatClient inner, GateClient gate, string framework = "microsoft-extensions-ai", string? provider = null)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var scan = Metadata(options);
        await ScanTurnAsync(history, scan, cancellationToken);
        var response = await base.GetResponseAsync(history, options, cancellationToken);
        await ScanToolCallsAsync(response.Messages.SelectMany(m => m.Contents), scan, cancellationToken);
        if (response.Text.Length > 0)
            await gate.ScanResponseAsync(response.Text, scan with { Model = response.ModelId ?? scan.Model }, cancellationToken);
        return response;
    }

    /// <summary>Chunks reach the caller as they arrive, so a verdict on the whole answer stops what follows
    /// but cannot recall them. Tool calls are still scanned before they run.</summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var scan = Metadata(options);
        await ScanTurnAsync(history, scan, cancellationToken);
        var answer = new StringBuilder();
        await foreach (var update in base.GetStreamingResponseAsync(history, options, cancellationToken))
        {
            answer.Append(update.Text);
            await ScanToolCallsAsync(update.Contents, scan, cancellationToken);
            yield return update;
        }
        if (answer.Length > 0) await gate.ScanResponseAsync(answer.ToString(), scan, cancellationToken);
    }

    private ScanOptions Metadata(ChatOptions? options) => new() {
        Framework = framework, Provider = provider, Model = options?.ModelId, SessionId = options?.ConversationId,
        ConversationId = options?.ConversationId, Enforce = gate.Options.Mode == ScanMode.Sync };

    /// <summary>Scan what this call adds to the conversation. A chat history is resent on every call: only
    /// its newest user message is this turn's prompt, or the tool results of a later step.</summary>
    private async Task ScanTurnAsync(IList<ChatMessage> history, ScanOptions scan, CancellationToken ct)
    {
        var last = history.LastOrDefault(m => m.Role != ChatRole.System);
        if (last is null) return;
        if (last.Role == ChatRole.Tool)
        {
            foreach (var result in last.Contents.OfType<FunctionResultContent>())
                await gate.ScanToolResultAsync(result.CallId, result.Result?.ToString() ?? "", scan, ct);
        }
        else if (last.Role == ChatRole.User)
        {
            var attachments = Attachments(last).ToList();
            if (last.Text.Length > 0 || attachments.Count > 0)
                await gate.ScanPromptAsync(last.Text, scan with { Attachments = attachments }, ct);
        }
    }

    private async Task ScanToolCallsAsync(IEnumerable<AIContent> contents, ScanOptions scan, CancellationToken ct)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
            await gate.ScanToolCallAsync(call.Name, call.Arguments, scan, ct);
    }

    private IEnumerable<Attachment> Attachments(ChatMessage message)
    {
        var (capture, max) = (gate.Options.CaptureFileText, gate.Options.MaxFileBytes);
        foreach (var content in message.Contents)
        {
            if (content is DataContent data)
                yield return Attachment.FromBytes(data.Data.Span, data.MediaType, data.Name, capture, max, "paste");
            else if (content is UriContent uri)
                yield return new Attachment { Id = uri.Uri.ToString(), Filename = Path.GetFileName(uri.Uri.LocalPath),
                    MimeType = uri.MediaType, Source = "file_ref" };
        }
    }
}

public static class GateChatClientExtensions
{
    public static ChatClientBuilder UseGodelGate(this ChatClientBuilder builder, GateClient gate,
        string framework = "microsoft-extensions-ai", string? provider = null) =>
        builder.Use(inner => new GateChatClient(inner, gate, framework, provider));
}

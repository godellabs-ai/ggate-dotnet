using System.Runtime.CompilerServices;
using System.Text;
using AutoGen.Core;

namespace GodelGate.AutoGen;

/// <summary>
/// Scans each reply of an AutoGen agent: the newest user turn or tool result before the agent runs, and
/// tool calls and the answer before they leave it. A block throws GateBlockedException, so the model or
/// tool is not called. Register it before FunctionCallMiddleware so tool calls are scanned before they run.
/// </summary>
public sealed class GateAutoGenMiddleware(GateClient gate, string framework = "autogen", string? provider = null) : IStreamingMiddleware
{
    public string? Name => "GodelGate";

    private ScanOptions Metadata => new() { Framework = framework, Provider = provider, Enforce = gate.Options.Mode == ScanMode.Sync };

    public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        await ScanTurnAsync(context.Messages, cancellationToken);
        var reply = await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
        await ScanToolCallsAsync(reply, cancellationToken);
        var text = AnswerText(reply);
        if (text.Length > 0) await gate.ScanResponseAsync(text, Metadata, cancellationToken);
        return reply;
    }

    /// <summary>Chunks reach the caller as they arrive, so a verdict on the whole answer stops what follows
    /// but cannot recall them. Tool calls are still scanned before they run.</summary>
    public async IAsyncEnumerable<IMessage> InvokeAsync(MiddlewareContext context, IStreamingAgent agent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ScanTurnAsync(context.Messages, cancellationToken);
        var answer = new StringBuilder();
        await foreach (var update in agent.GenerateStreamingReplyAsync(context.Messages, context.Options, cancellationToken))
        {
            if (update is TextMessageUpdate text) answer.Append(text.Content);
            await ScanToolCallsAsync(update, cancellationToken);
            yield return update;
        }
        if (answer.Length > 0) await gate.ScanResponseAsync(answer.ToString(), Metadata, cancellationToken);
    }

    /// <summary>Scan what this call adds to the conversation. The history is resent on every call: only its
    /// newest user message is this turn's prompt, or the tool results of a later step.</summary>
    private async Task ScanTurnAsync(IEnumerable<IMessage> messages, CancellationToken ct)
    {
        var last = messages.LastOrDefault(message => message.GetRole() != Role.System);
        switch (last)
        {
            case null:
                return;
            case ToolCallResultMessage results:
                await ScanToolResultsAsync(results, ct);
                return;
            case ToolCallAggregateMessage aggregate:
                await ScanToolResultsAsync(aggregate.Message2, ct);
                return;
        }
        if (last.GetRole() != Role.User) return;
        var attachments = Attachments(last).ToList();
        var text = PromptText(last);
        if (text.Length > 0 || attachments.Count > 0)
            await gate.ScanPromptAsync(text, Metadata with { Attachments = attachments }, ct);
    }

    private async Task ScanToolResultsAsync(ToolCallResultMessage results, CancellationToken ct)
    {
        foreach (var call in results.ToolCalls)
            await gate.ScanToolResultAsync(call.FunctionName, call.Result ?? "", Metadata, ct);
    }

    private async Task ScanToolCallsAsync(IMessage reply, CancellationToken ct)
    {
        if (reply is ToolCallMessage calls)
            foreach (var call in calls.ToolCalls)
                await gate.ScanToolCallAsync(call.FunctionName, call.FunctionArguments, Metadata, ct);
    }

    private static string AnswerText(IMessage reply) => reply is TextMessage or MultiModalMessage ? PromptText(reply) : reply.GetContent() ?? "";

    private static string PromptText(IMessage message) => message switch {
        TextMessage text => text.Content,
        MultiModalMessage multi => string.Join("\n", multi.Content.OfType<TextMessage>().Select(part => part.Content)),
        _ => message.GetContent() ?? "" };

    private IEnumerable<Attachment> Attachments(IMessage message)
    {
        var parts = message is MultiModalMessage multi ? multi.Content : [message];
        foreach (var image in parts.OfType<ImageMessage>())
        {
            if (image.Data is { } data)
                yield return Attachment.FromBytes(data.ToArray(), image.Data.MediaType ?? image.MimeType, null,
                    gate.Options.CaptureFileText, gate.Options.MaxFileBytes, "paste");
            else if (image.Url is { Length: > 0 } url)
                yield return new Attachment { Id = url, Filename = Path.GetFileName(new Uri(url).LocalPath), MimeType = image.MimeType, Source = "file_ref" };
        }
    }
}

public static class GateAutoGenExtensions
{
    /// <summary>Scan an agent's replies. Add this before RegisterMiddleware(functionCallMiddleware).</summary>
    public static MiddlewareStreamingAgent<TAgent> RegisterGodelGate<TAgent>(this TAgent agent, GateClient gate,
        string framework = "autogen", string? provider = null) where TAgent : IStreamingAgent =>
        agent.RegisterStreamingMiddleware(new GateAutoGenMiddleware(gate, framework, provider));
}

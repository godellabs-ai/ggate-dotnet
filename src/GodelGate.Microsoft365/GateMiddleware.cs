using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace GodelGate.Microsoft365;

/// <summary>Protects message sends and updates. Internal tools/model calls need their own scans.</summary>
public sealed class GateMiddleware(GateClient client) : IMiddleware
{
    private static readonly ConditionalWeakTable<ITurnContext, ScanOptions> Contexts = new();
    public static ScanOptions Metadata(ITurnContext context) => Contexts.GetValue(context, CreateMetadata);

    private static ScanOptions CreateMetadata(ITurnContext context)
    {
        var activity = context.Activity;
        var turn = activity.Id ?? Guid.NewGuid().ToString();
        return new ScanOptions {
            Framework = "microsoft-365-agents",
            SessionId = GateSession.Current ?? $"m365:{activity.ChannelId}:{activity.Conversation?.TenantId}:{activity.Conversation?.Id ?? turn}",
            ConversationId = activity.Conversation?.Id, CorrelationId = turn, RequestId = turn,
            SourceDetail = new JsonObject { ["channel_id"] = activity.ChannelId?.ToString(),
                ["tenant_id"] = activity.Conversation?.TenantId, ["activity_type"] = activity.Type,
                ["from_id"] = activity.From?.Id }
        };
    }

    public async Task OnTurnAsync(ITurnContext context, NextDelegate next, CancellationToken cancellationToken = default)
    {
        var metadata = Metadata(context) with { Enforce = client.Options.Mode == ScanMode.Sync };
        async Task Outbound(IActivity activity)
        {
            var (text, attachments) = Content(activity);
            if (text.Length > 0 || attachments.Count > 0)
                await client.ScanResponseAsync(text, metadata with { Attachments = attachments }, cancellationToken);
        }
        context.OnSendActivities(async (_, activities, send) => {
            foreach (var activity in activities) await Outbound(activity);
            return await send();
        });
        context.OnUpdateActivity(async (_, activity, update) => {
            await Outbound(activity);
            return await update();
        });
        var (text, attachments) = Content(context.Activity);
        if (text.Length > 0 || attachments.Count > 0)
            await client.ScanPromptAsync(text, metadata with { Attachments = attachments }, cancellationToken);
        await next(cancellationToken);
    }

    private static (string, List<GodelGate.Attachment>) Content(IActivity activity)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(activity.Text)) parts.Add(activity.Text);
        if (!string.IsNullOrEmpty(activity.Speak)) parts.Add(activity.Speak);
        if (activity.Value is not null) parts.Add(JsonSerializer.Serialize(activity.Value));
        var attachments = new List<GodelGate.Attachment>();
        foreach (var attachment in activity.Attachments ?? [])
        {
            if (attachment.Content is not null) parts.Add(JsonSerializer.Serialize(attachment.Content));
            attachments.Add(new GodelGate.Attachment { Filename = attachment.Name ?? "attachment", MimeType = attachment.ContentType });
        }
        return (string.Join("\n", parts), attachments);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace GodelGate.SemanticKernel;

/// <summary>
/// Scans prompts, plugin calls and answers that pass through a Kernel. A block throws
/// GateBlockedException, so the model or plugin is not called. Calling IChatCompletionService directly
/// bypasses Kernel filters: wrap that service's IChatClient with GodelGate.AI instead.
/// </summary>
public sealed class GateKernelFilter(GateClient gate, string framework = "semantic-kernel") : IPromptRenderFilter, IFunctionInvocationFilter
{
    private ScanOptions Metadata => new() { Framework = framework, Enforce = gate.Options.Mode == ScanMode.Sync };

    public async Task OnPromptRenderAsync(PromptRenderContext context, Func<PromptRenderContext, Task> next)
    {
        // The prompt exists only after rendering: render first, then scan what will be sent.
        await next(context);
        if (!string.IsNullOrEmpty(context.RenderedPrompt))
            await gate.ScanPromptAsync(context.RenderedPrompt, Metadata, context.CancellationToken);
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // A prompt function is the model call itself; only plugin functions are tool calls.
        var isPrompt = string.IsNullOrEmpty(context.Function.PluginName);
        if (!isPrompt)
            await gate.ScanToolCallAsync(context.Function.Name, context.Arguments.ToDictionary(x => x.Key, x => x.Value?.ToString()), Metadata, context.CancellationToken);
        await next(context);
        var output = context.Result.ValueType is null ? null : context.Result.ToString();
        if (string.IsNullOrEmpty(output)) return;
        if (isPrompt) await gate.ScanResponseAsync(output, Metadata, context.CancellationToken);
        else await gate.ScanToolResultAsync(context.Function.Name, output, Metadata, context.CancellationToken);
    }
}

public static class GateKernelExtensions
{
    public static IKernelBuilder AddGodelGate(this IKernelBuilder builder, GateClient gate, string framework = "semantic-kernel")
    {
        var filter = new GateKernelFilter(gate, framework);
        builder.Services.AddSingleton<IPromptRenderFilter>(filter);
        builder.Services.AddSingleton<IFunctionInvocationFilter>(filter);
        return builder;
    }
}

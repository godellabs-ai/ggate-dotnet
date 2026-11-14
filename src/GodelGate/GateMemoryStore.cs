using System.Text.Json;

namespace GodelGate;

/// <summary>Gate a backing store's reads and writes. The write delegate is never called after a block.</summary>
public sealed class GateMemoryStore<T>(GateClient gate,
    Func<string, T, CancellationToken, Task> put,
    Func<string, CancellationToken, Task<T>> get,
    ScanOptions? options = null)
{
    public async Task PutAsync(string key, T value, CancellationToken cancellationToken = default)
    {
        await gate.ScanMemoryWriteAsync(new { key, value },
            (options ?? new ScanOptions { Framework = "memory" }) with { Enforce = true }, cancellationToken);
        await put(key, value, cancellationToken);
    }

    public async Task<T> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await get(key, cancellationToken);
        await gate.ScanToolResultAsync("memory.get", JsonSerializer.Serialize(value),
            (options ?? new ScanOptions { Framework = "memory" }) with { Enforce = true, BeforeDelivery = true }, cancellationToken);
        return value;
    }
}

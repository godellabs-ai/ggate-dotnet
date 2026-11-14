namespace GodelGate;

/// <summary>Gate a complete vector-store upsert before calling the provider. contentOf must return
/// the plaintext and metadata for each record: embedding vectors do not expose instruction text.
/// Use the same delegate for provider add/update APIs if those are the application's write path.</summary>
public sealed class GateVectorStore<T>(GateClient gate,
    Func<IReadOnlyList<T>, CancellationToken, Task> upsert,
    Func<T, object?> contentOf,
    ScanOptions? options = null)
{
    public async Task UpsertAsync(IEnumerable<T> records, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = records.ToArray();
        var content = batch.Select(contentOf).ToArray();
        await gate.ScanMemoryWriteAsync(content,
            (options ?? new ScanOptions { Framework = "memory" }) with { Enforce = true },
            cancellationToken, store: "vector", operation: "upsert");
        cancellationToken.ThrowIfCancellationRequested();
        await upsert(batch, cancellationToken);
    }
}

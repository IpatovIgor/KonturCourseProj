using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class MongoTransactions(IMongoClient client)
{
    // Порядок аргументов в MongoDB.Driver 3.x: readConcern, readPreference, writeConcern.
    private static readonly TransactionOptions Options = new(
        ReadConcern.Snapshot,
        ReadPreference.Primary,
        new WriteConcern("majority"));

    public async Task<T> RunAsync<T>(Func<IClientSessionHandle, Task<T>> action, CancellationToken ct)
    {
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        T result = default!;
        await session.WithTransactionAsync(async (s, token) =>
        {
            result = await action(s);
            return true;
        }, Options, ct);
        return result;
    }
}
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class MongoTransactions(IMongoClient client)
{
    private static readonly TransactionOptions Options = new(
        ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority);

    public async Task<T> RunAsync<T>(Func<IClientSessionHandle, Task<T>> action, CancellationToken ct)
    {
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await session.WithTransactionAsync((s, _) => action(s), Options, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && attempt < 2)
            {
                // A duplicate aborts the transaction. The next snapshot can read the winner.
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}

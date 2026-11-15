using MongoDB.Driver;

namespace BackEnd.Data;

internal static class MongoTransactions
{
    public static async Task<T> InSnapshotAsync<T>(this IMongoDatabase database,
        Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var session = await database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(operation,
            new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
    }
}

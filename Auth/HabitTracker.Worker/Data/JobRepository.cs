using HabitTracker.Worker.Models;
using MongoDB.Driver;

namespace HabitTracker.Worker.Data;

public sealed class JobRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<Job> _jobs = database.GetCollection<Job>("jobs");

    // Создаёт job или обновляет существующий pending. Терминальные и running не трогает.
    // Возвращает true, если job записан (создан или обновлён pending).
    public async Task<bool> UpsertPendingAsync(IClientSessionHandle session, Job job, CancellationToken ct)
    {
        var filter = Builders<Job>.Filter.Eq(x => x.Id, job.Id);
        var existing = await _jobs.Find(session, filter).FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            try
            {
                await _jobs.InsertOneAsync(session, job, cancellationToken: ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                return false; // кто-то другой создал job за этот день
            }
            return true;
        }

        // Терминальные и running не переоткрываем.
        if (existing.Status != JobStatus.Pending)
            return false;

        // Обновляем pending, не сбрасывая счётчик HTTP-сбоев и не сокращая backoff.
        var update = Builders<Job>.Update
            .Set(x => x.DueAtUtc, job.DueAtUtc)
            .Set(x => x.TimeZoneId, job.TimeZoneId)
            .Set(x => x.LastUpdatedAtUtc, job.LastUpdatedAtUtc);

        // Если у существующего job уже есть более поздняя задержка (backoff), сохраняем её.
        if (existing.DueAtUtc > job.DueAtUtc && existing.HttpFailureCount > 0)
            update = Builders<Job>.Update
                .Set(x => x.LastUpdatedAtUtc, job.LastUpdatedAtUtc);

        await _jobs.UpdateOneAsync(session, filter, update, cancellationToken: ct);
        return true;
    }
}
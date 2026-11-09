using BackEnd.Models;
using MongoDB.Bson;

namespace BackEnd.Data;

public interface IHabitRepository
{
    Task<Habit> CreateAsync(ObjectId owner, Func<User?, Habit> build, CancellationToken ct);
    Task<Habit?> GetAsync(ObjectId owner, ObjectId id, CancellationToken ct);
    Task<(List<Habit> Items, long Total)> GetPageAsync(
        ObjectId owner, string status, int page, int pageSize, CancellationToken ct);
    Task<List<Habit>> GetActiveAsync(ObjectId owner, CancellationToken ct);
    Task<bool> TrySaveAsync(Habit habit, CancellationToken ct);
    Task<List<HabitCompletion>> GetCompletionsAsync(
        ObjectId owner, ObjectId? habitId, string from, string to, CancellationToken ct);
    Task SetCompletionAsync(ObjectId owner, ObjectId id, string date, bool completed,
        Func<Habit?, DateTime> validate, CancellationToken ct);
}

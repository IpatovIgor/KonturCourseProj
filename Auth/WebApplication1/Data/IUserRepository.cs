using BackEnd.Models;

namespace BackEnd.Data;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken);
    Task<User?> SaveSummaryAsync(string id, NotificationSettings settings, CancellationToken ct);
    Task CreateAsync(User user);
    Task UpdateAsync(User user);
}

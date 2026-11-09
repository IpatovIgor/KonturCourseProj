using BackEnd.Models;

namespace BackEnd.Data;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken);
    Task CreateAsync(User user);
    Task UpdateAsync(User user);
}

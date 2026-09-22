using fyserver.Models;

namespace fyserver.Services;

public interface IUserStoreBackend : IAsyncDisposable
{
    string Provider { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default);
    Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);
    Task SaveAsync(User user, CancellationToken cancellationToken = default);
    Task DeleteAsync(User user, CancellationToken cancellationToken = default);
    Task<List<User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task CheckpointAsync(CancellationToken cancellationToken = default);
}

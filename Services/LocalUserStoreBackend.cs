using fyserver.Models;

namespace fyserver.Services;

public sealed class LocalUserStoreBackend(FasterKvService database) : IUserStoreBackend
{
    public string Provider => "local";
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default) =>
        Task.FromResult(database.Get<User>($"user:username:{userName}"));
    public Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(database.Get<User>($"user:id:{userId}"));
    public Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        database.Batch(new Dictionary<string, User>
        {
            [$"user:username:{user.UserName}"] = user,
            [$"user:id:{user.Id}"] = user
        });
        return Task.CompletedTask;
    }
    public Task DeleteAsync(User user, CancellationToken cancellationToken = default)
    {
        database.Batch<User>(null, [$"user:username:{user.UserName}", $"user:id:{user.Id}"]);
        return Task.CompletedTask;
    }
    public Task<List<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(database.GetAllByPrefix<User>("user:id:"));
    public Task ClearAsync(CancellationToken cancellationToken = default) { database.Clear(); return Task.CompletedTask; }
    public Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        database.Checkpoint(FASTER.core.CheckpointType.FoldOver);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

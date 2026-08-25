using fyserver.Models;

namespace fyserver.Services;

/// <summary>
/// 用户存储服务（FASTER KV，双索引：user:username:* 与 user:id:*）。
/// 生命周期由 DI 容器统一管理（共享单例，见 Program.cs）。
/// </summary>
public class UserStoreService
{
    private readonly FasterKvService _db;
    // 保护"检查存在 -> 分配 ID -> 保存"的原子性
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public UserStoreService(FasterKvService dbService)
    {
        _db = dbService;
    }

    public void RecordFull() => _db.Checkpoint();

    public void RecordIncremental() => _db.Checkpoint(FASTER.core.CheckpointType.FoldOver);

    public Task<User?> GetByUserNameAsync(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return Task.FromResult<User?>(null);
        var a = _db.Get<User>($"user:username:{userName}");
        return Task.FromResult<User?>(a);
    }

    public Task<User?> GetByIdAsync(int userId)
    {
        if (userId <= 0)
            return Task.FromResult<User?>(null);
        var u = _db.Get<User>($"user:id:{userId}");
        return Task.FromResult<User?>(u);
    }

    // 核心保存逻辑
    public Task SaveUserAsync(User user)
    {
        if (string.IsNullOrEmpty(user.UserName) || user.Id == 0)
            throw new ArgumentException("Invalid user data: Missing UserName or ID");

        // 更新修改时间
        user.UpdatedAt = DateTime.UtcNow;

        // 双重索引（冗余存储），Batch 操作保证原子性
        var puts = new Dictionary<string, User>
        {
            [$"user:username:{user.UserName}"] = user,
            [$"user:id:{user.Id}"] = user
        };

        _db.Batch(puts);
        return Task.CompletedTask;
    }

    // 创建用户：处理 ID 生成和冲突
    public async Task<User> CreateUserAsync(string userName)
    {
        // 串行化"检查存在 -> 分配 ID -> 保存"，避免并发重复创建
        await _createLock.WaitAsync();
        try
        {
            // 1. 检查用户名是否存在
            var existingUser = await GetByUserNameAsync(userName);
            if (existingUser != null)
            {
                // 幂等设计：重复调用时返回已存在的用户
                return existingUser;
            }

            var user = new User(userName);
            // 2. 生成唯一 ID（带冲突重试）
            int newId;
            do
            {
                newId = Random.Shared.Next(100000, 1000000);
            } while (await GetByIdAsync(newId) != null);
            user.Id = newId;

            // 3. 保存
            await SaveUserAsync(user);
            return user;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public async Task DeleteUserAsync(int userId)
    {
        var user = await GetByIdAsync(userId);
        if (user == null) return;

        var deletes = new List<string>
        {
            $"user:username:{user.UserName}",
            $"user:id:{userId}"
        };

        _db.Batch<User>(null, deletes);
    }

    public Task<List<User>> GetAllUsersAsync()
    {
        // 只取一种 Key 前缀，防止数据重复
        var list = _db.GetAllByPrefix<User>("user:id:");
        return Task.FromResult(list);
    }

    public void ClearAll()
    {
        _db.Clear();
    }
}

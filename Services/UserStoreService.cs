using System.Collections.Concurrent;
using fyserver.Models;

namespace fyserver.Services;

/// <summary>可切换的玩家存储门面：本地 FASTER、MySQL 或 PostgreSQL。</summary>
public sealed class UserStoreService
{
    private readonly Func<FasterKvService> _localDatabaseFactory;
    private readonly UserDatabaseConfigurationService _configuration;
    private readonly AppDataStoreService _appData;
    private readonly CardCatalogService _cardCatalog;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly SemaphoreSlim _identityLock = new(1, 1);
    private readonly SemaphoreSlim _backendLock = new(1, 1);
    private IUserStoreBackend? _backend;

    public bool IsReady => _backend != null;
    public string? Provider => _backend?.Provider;
    public string? LastInitializationError { get; private set; }

    public UserStoreService(Func<FasterKvService> localDatabaseFactory, UserDatabaseConfigurationService configuration, AppDataStoreService appData, CardCatalogService cardCatalog)
    {
        _localDatabaseFactory = localDatabaseFactory;
        _configuration = configuration;
        _appData = appData;
        _cardCatalog = cardCatalog;
    }

    public async Task TryInitializeConfiguredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configuration.Load();
            if (settings == null) return;
            await ActivateAsync(settings, save: false, cancellationToken);
        }
        catch (Exception ex)
        {
            LastInitializationError = SafeError(ex);
            Console.WriteLine($"用户数据库初始化失败：{LastInitializationError}");
        }
    }

    public async Task<(bool Ok, string Message)> ConfigureAsync(UserDatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            await ActivateAsync(settings, save: true, cancellationToken);
            return (true, $"{ProviderName(settings.Provider)} 已连接并完成数据表初始化");
        }
        catch (Exception ex)
        {
            LastInitializationError = SafeError(ex);
            return (false, "数据库连接或初始化失败：" + LastInitializationError);
        }
    }

    public void RecordFull() => RequireBackend().CheckpointAsync().GetAwaiter().GetResult();
    public void RecordIncremental() => RequireBackend().CheckpointAsync().GetAwaiter().GetResult();

    public Task<User?> GetByUserNameAsync(string userName) => string.IsNullOrWhiteSpace(userName)
        ? Task.FromResult<User?>(null) : RequireBackend().GetByUserNameAsync(userName);
    public Task<User?> GetByIdAsync(int userId) => userId <= 0
        ? Task.FromResult<User?>(null) : RequireBackend().GetByIdAsync(userId);

    public async Task<TResult?> WithUserLockAsync<TResult>(int userId, Func<User, Task<TResult>> action) where TResult : class
    {
        var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var user = await GetByIdAsync(userId);
            return user == null ? null : await action(user);
        }
        finally { gate.Release(); }
    }

    public async Task SaveUserAsync(User user)
    {
        if (string.IsNullOrEmpty(user.UserName) || user.Id == 0) throw new ArgumentException("Invalid user data: Missing UserName or ID");
        user.UpdatedAt = DateTime.UtcNow;
        await RequireBackend().SaveAsync(user);
    }

    public async Task<User> CreateUserAsync(string userName)
    {
        await _createLock.WaitAsync();
        try
        {
            var existing = await GetByUserNameAsync(userName);
            if (existing != null) return existing;
            var user = new User(userName);
            do { user.Id = Random.Shared.Next(100000, 1000000); }
            while (await GetByIdAsync(user.Id) != null);
            _cardCatalog.ApplyInitialCollection(user);
            await SavePlayerIdentityAsync(user, user.Name);
            return user;
        }
        finally { _createLock.Release(); }
    }

    /// <summary>保存公开昵称和四位 Tag；新 Tag 在同昵称用户中尽量避免重复。</summary>
    public async Task<bool> SavePlayerIdentityAsync(User user, string name, int? requestedTag = null)
    {
        await _identityLock.WaitAsync();
        try
        {
            var peers = (await GetAllUsersAsync())
                .Where(other => other.Id != user.Id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.Tag)
                .Where(tag => tag is >= 0 and <= 9999)
                .ToHashSet();

            int tag;
            if (requestedTag is { } requested)
            {
                var unchangedLegacyIdentity = string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase) && user.Tag == requested;
                if (requested is < 0 or > 9999 || peers.Contains(requested) && !unchangedLegacyIdentity) return false;
                tag = requested;
            }
            else
            {
                var start = Random.Shared.Next(0, 10000);
                var found = -1;
                for (var offset = 0; offset < 10000; offset++)
                {
                    var candidate = (start + offset) % 10000;
                    if (peers.Contains(candidate)) continue;
                    found = candidate;
                    break;
                }
                if (found < 0) throw new InvalidOperationException("该昵称的四位玩家 Tag 已用完");
                tag = found;
            }

            user.Name = name;
            user.Tag = tag;
            await SaveUserAsync(user);
            return true;
        }
        finally { _identityLock.Release(); }
    }

    public async Task DeleteUserAsync(int userId)
    {
        var user = await GetByIdAsync(userId);
        if (user != null) await RequireBackend().DeleteAsync(user);
    }

    public Task<List<User>> GetAllUsersAsync() => RequireBackend().GetAllAsync();
    public void ClearAll() => RequireBackend().ClearAsync().GetAwaiter().GetResult();

    private async Task ActivateAsync(UserDatabaseSettings settings, bool save, CancellationToken cancellationToken)
    {
        await _backendLock.WaitAsync(cancellationToken);
        try
        {
            IUserStoreBackend candidate = settings.Provider == "local"
                ? new LocalUserStoreBackend(_localDatabaseFactory())
                : new RelationalUserStoreBackend(settings);
            await candidate.InitializeAsync(cancellationToken);
            _appData.Initialize(settings);
            if (save) _configuration.Save(settings);
            var previous = _backend;
            _backend = candidate;
            LastInitializationError = null;
            if (previous != null && !ReferenceEquals(previous, candidate)) await previous.DisposeAsync();
        }
        finally { _backendLock.Release(); }
    }

    private IUserStoreBackend RequireBackend() => _backend ?? throw new InvalidOperationException("用户数据库尚未完成初始化");
    private static string ProviderName(string provider) => provider switch { "mysql" => "MySQL", "postgresql" => "PostgreSQL", _ => "本地 FASTER" };
    private static string SafeError(Exception ex)
    {
        var message = ex.GetBaseException().Message.Replace("\r", " ").Replace("\n", " ");
        return message.Length > 300 ? message[..300] : message;
    }
}

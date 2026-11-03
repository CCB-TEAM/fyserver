using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Serialization;
using MySqlConnector;
using Npgsql;

namespace fyserver.Services;

/// <summary>
/// 好友关系与好友私聊的持久化存储。
/// PostgreSQL / MySQL 使用 fy_friendships 与 fy_chat_messages 两张表；
/// local 模式写入 data/social/social.json（内存为准，定时落盘）。
/// 与对局历史一样，每次调用按当前数据库配置选择后端，后台切换数据库后无需重启。
/// </summary>
public sealed class SocialService(UserDatabaseConfigurationService databaseConfiguration) : IDisposable
{
    private const string SettingsPath = "./data/social-settings.json";
    private const string LocalPath = "./data/social/social.json";

    private readonly object _settingsLock = new();
    private SocialSettings? _settings;

    // 好友关系的变更（申请/同意/删除）统一串行化，避免 A→B 与 B→A 同时申请产生两条记录。
    private readonly SemaphoreSlim _friendshipLock = new(1, 1);

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private string? _schemaKey;

    private readonly object _localLock = new();
    private SocialLocalSnapshot? _local;
    private bool _localDirty;
    private Timer? _flushTimer;

    // ==================== 设置 ====================

    public SocialSettings GetSettings()
    {
        lock (_settingsLock)
        {
            if (_settings != null) return _settings;
            var settings = SocialSettings.Default;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var root = JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject();
                    var d = SocialSettings.Default;
                    settings = new SocialSettings(
                        root?["assistantName"]?.GetValue<string>() ?? d.AssistantName,
                        root?["welcomeMessage"]?.GetValue<string>() ?? d.WelcomeMessage,
                        root?["maxFriends"]?.GetValue<int>() ?? d.MaxFriends,
                        root?["maxPendingRequests"]?.GetValue<int>() ?? d.MaxPendingRequests,
                        root?["historyDays"]?.GetValue<int>() ?? d.HistoryDays,
                        root?["historyPerFriend"]?.GetValue<int>() ?? d.HistoryPerFriend,
                        root?["maxMessageLength"]?.GetValue<int>() ?? d.MaxMessageLength,
                        root?["replayOwnMessages"]?.GetValue<bool>() ?? d.ReplayOwnMessages);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取好友系统设置失败，使用默认值：{ex.Message}");
            }
            _settings = settings.Normalize();
            return _settings;
        }
    }

    public SocialSettings SaveSettings(SocialSettings settings)
    {
        var normalized = settings.Normalize();
        lock (_settingsLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = SettingsToJson(normalized);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json.ToJsonString(new() { WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
            _settings = normalized;
        }
        return normalized;
    }

    public static JsonObject SettingsToJson(SocialSettings s) => new()
    {
        ["assistantName"] = s.AssistantName,
        ["welcomeMessage"] = s.WelcomeMessage,
        ["maxFriends"] = s.MaxFriends,
        ["maxPendingRequests"] = s.MaxPendingRequests,
        ["historyDays"] = s.HistoryDays,
        ["historyPerFriend"] = s.HistoryPerFriend,
        ["maxMessageLength"] = s.MaxMessageLength,
        ["replayOwnMessages"] = s.ReplayOwnMessages
    };

    // ==================== 好友关系 ====================

    /// <summary>与该玩家相关的所有好友关系（含双方向的申请）。</summary>
    public async Task<List<FriendshipRecord>> GetFriendshipsAsync(int userId, CancellationToken cancellationToken = default)
    {
        var settings = RequireDatabase();
        if (settings.Provider == "local")
            return WithLocal(local => local.Friendships.Where(f => f.Involves(userId)).ToList());

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT requester_id, addressee_id, status, created_at, updated_at FROM fy_friendships WHERE requester_id = @u OR addressee_id = @u";
        AddParameter(command, "@u", userId);
        return await ReadFriendshipsAsync(command, cancellationToken);
    }

    public async Task<FriendshipRecord?> GetFriendshipAsync(int a, int b, CancellationToken cancellationToken = default) =>
        (await GetFriendshipsAsync(a, cancellationToken)).FirstOrDefault(f => f.Other(a) == b);

    /// <summary>
    /// 在好友关系锁内执行一次读-改-写，回调拿到双方当前的关系后决定动作。
    /// 返回回调的结果。所有写入（申请/同意/删除）都应该走这里。
    /// </summary>
    public async Task<T> MutateFriendshipAsync<T>(int a, int b, Func<FriendshipRecord?, Task<(FriendshipMutation Mutation, T Result)>> decide,
        CancellationToken cancellationToken = default)
    {
        await _friendshipLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetFriendshipAsync(a, b, cancellationToken);
            var (mutation, result) = await decide(existing);
            await ApplyMutationAsync(mutation, cancellationToken);
            return result;
        }
        finally { _friendshipLock.Release(); }
    }

    private async Task ApplyMutationAsync(FriendshipMutation mutation, CancellationToken cancellationToken)
    {
        if (mutation.Kind == FriendshipMutationKind.None) return;
        var settings = RequireDatabase();
        var now = DateTime.UtcNow;
        if (settings.Provider == "local")
        {
            WithLocal(local =>
            {
                local.Friendships.RemoveAll(f => f.Involves(mutation.A) && f.Other(mutation.A) == mutation.B);
                if (mutation.Kind == FriendshipMutationKind.CreatePending)
                    local.Friendships.Add(new FriendshipRecord(mutation.A, mutation.B, FriendshipRecord.Pending, now, now));
                else if (mutation.Kind == FriendshipMutationKind.Accept)
                    local.Friendships.Add(new FriendshipRecord(mutation.A, mutation.B, FriendshipRecord.Accepted, mutation.CreatedAt ?? now, now));
                _localDirty = true;
                return true;
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        switch (mutation.Kind)
        {
            case FriendshipMutationKind.CreatePending:
                command.CommandText = "INSERT INTO fy_friendships (requester_id, addressee_id, status, created_at, updated_at) VALUES (@a, @b, 'pending', @now, @now)";
                break;
            case FriendshipMutationKind.Accept:
                command.CommandText = "UPDATE fy_friendships SET status = 'accepted', updated_at = @now WHERE requester_id = @a AND addressee_id = @b";
                break;
            case FriendshipMutationKind.Delete:
                command.CommandText = "DELETE FROM fy_friendships WHERE (requester_id = @a AND addressee_id = @b) OR (requester_id = @b AND addressee_id = @a)";
                break;
        }
        AddParameter(command, "@a", mutation.A);
        AddParameter(command, "@b", mutation.B);
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== 聊天记录 ====================

    /// <summary>写入一条消息并返回分配的 id。</summary>
    public async Task<long> AddMessageAsync(int senderId, int receiverId, string body, string context, DateTime createdAt, bool delivered,
        CancellationToken cancellationToken = default)
    {
        var settings = RequireDatabase();
        if (settings.Provider == "local")
        {
            return WithLocal(local =>
            {
                var id = local.NextMessageId++;
                local.Messages.Add(new ChatMessageRecord(id, senderId, receiverId, body, context, createdAt, delivered));
                _localDirty = true;
                return id;
            });
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.Provider == "postgresql"
            ? "INSERT INTO fy_chat_messages (sender_id, receiver_id, body, context, created_at, delivered) VALUES (@s, @r, @body, @ctx, @at, @d) RETURNING id"
            : "INSERT INTO fy_chat_messages (sender_id, receiver_id, body, context, created_at, delivered) VALUES (@s, @r, @body, @ctx, @at, @d); SELECT LAST_INSERT_ID();";
        AddParameter(command, "@s", senderId);
        AddParameter(command, "@r", receiverId);
        AddParameter(command, "@body", body);
        AddParameter(command, "@ctx", context);
        AddParameter(command, "@at", createdAt);
        AddParameter(command, "@d", delivered);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>把单条消息标记为已送达（实时推送成功后调用）。</summary>
    public async Task MarkMessageDeliveredAsync(long id, CancellationToken cancellationToken = default)
    {
        var settings = RequireDatabase();
        if (settings.Provider == "local")
        {
            WithLocal(local =>
            {
                var index = local.Messages.FindLastIndex(m => m.Id == id);
                if (index >= 0 && !local.Messages[index].Delivered)
                {
                    local.Messages[index] = local.Messages[index] with { Delivered = true };
                    _localDirty = true;
                }
                return true;
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE fy_chat_messages SET delivered = @t WHERE id = @id";
        AddParameter(command, "@t", true);
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>把发给该玩家、id 不超过 maxId 的未送达消息标记为已送达。</summary>
    public async Task MarkDeliveredAsync(int receiverId, long maxId, CancellationToken cancellationToken = default)
    {
        var settings = RequireDatabase();
        if (settings.Provider == "local")
        {
            WithLocal(local =>
            {
                for (var i = 0; i < local.Messages.Count; i++)
                {
                    var m = local.Messages[i];
                    if (m.ReceiverId == receiverId && !m.Delivered && m.Id <= maxId)
                    {
                        local.Messages[i] = m with { Delivered = true };
                        _localDirty = true;
                    }
                }
                return true;
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE fy_chat_messages SET delivered = @t WHERE receiver_id = @r AND delivered = @f AND id <= @max";
        AddParameter(command, "@t", true);
        AddParameter(command, "@f", false);
        AddParameter(command, "@r", receiverId);
        AddParameter(command, "@max", maxId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 与该玩家相关的消息：发送时间不早于 since 的，加上所有尚未送达给他的。按 id 倒序，最多 max 条。
    /// </summary>
    public async Task<List<ChatMessageRecord>> GetRecentMessagesAsync(int userId, DateTime since, int max, CancellationToken cancellationToken = default)
    {
        var settings = RequireDatabase();
        if (settings.Provider == "local")
        {
            return WithLocal(local => local.Messages
                .Where(m => (m.SenderId == userId || m.ReceiverId == userId) && (m.CreatedAt >= since || (m.ReceiverId == userId && !m.Delivered)))
                .OrderByDescending(m => m.Id)
                .Take(max)
                .ToList());
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sender_id, receiver_id, body, context, created_at, delivered FROM fy_chat_messages " +
                              "WHERE (sender_id = @u OR receiver_id = @u) AND (created_at >= @since OR (receiver_id = @u AND delivered = @f)) " +
                              "ORDER BY id DESC LIMIT @max";
        AddParameter(command, "@u", userId);
        AddParameter(command, "@since", since);
        AddParameter(command, "@f", false);
        AddParameter(command, "@max", max);
        var rows = new List<ChatMessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ChatMessageRecord(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), AsUtc(reader.GetDateTime(5)), reader.GetBoolean(6)));
        }
        return rows;
    }

    /// <summary>删除早于 before 的消息，返回删除条数。</summary>
    public async Task<int> CleanupMessagesAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return 0;
        if (settings.Provider == "local")
        {
            return WithLocal(local =>
            {
                var removed = local.Messages.RemoveAll(m => m.CreatedAt < before);
                if (removed > 0) _localDirty = true;
                return removed;
            });
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fy_chat_messages WHERE created_at < @before";
        AddParameter(command, "@before", before);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>后台概览：好友对数、待处理申请数、消息总数。</summary>
    public async Task<(long Friendships, long Pending, long Messages)> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return (0, 0, 0);
        if (settings.Provider == "local")
        {
            return WithLocal(local => (
                (long)local.Friendships.Count(f => f.Status == FriendshipRecord.Accepted),
                (long)local.Friendships.Count(f => f.Status == FriendshipRecord.Pending),
                (long)local.Messages.Count));
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        async Task<long> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        return (await Scalar("SELECT COUNT(*) FROM fy_friendships WHERE status = 'accepted'"),
            await Scalar("SELECT COUNT(*) FROM fy_friendships WHERE status = 'pending'"),
            await Scalar("SELECT COUNT(*) FROM fy_chat_messages"));
    }

    // ==================== 后端实现细节 ====================

    private UserDatabaseSettings RequireDatabase() =>
        databaseConfiguration.Load() ?? throw new InvalidOperationException("玩家数据库尚未配置");

    private T WithLocal<T>(Func<SocialLocalSnapshot, T> action)
    {
        lock (_localLock)
        {
            if (_local == null)
            {
                _local = LoadLocal();
                _flushTimer ??= new Timer(_ => FlushLocal(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
            return action(_local);
        }
    }

    private static SocialLocalSnapshot LoadLocal()
    {
        try
        {
            if (File.Exists(LocalPath))
                return JsonSerializer.Deserialize(File.ReadAllText(LocalPath), FyJsonContext.Default.SocialLocalSnapshot) ?? new SocialLocalSnapshot();
        }
        catch (Exception ex)
        {
            // 不覆盖损坏的文件：先备份再从空数据开始，方便人工恢复。
            Console.WriteLine($"读取 {LocalPath} 失败：{ex.Message}，已备份并重新开始");
            try { File.Copy(LocalPath, LocalPath + $".broken-{DateTime.UtcNow:yyyyMMddHHmmss}", true); } catch { }
        }
        return new SocialLocalSnapshot();
    }

    /// <summary>local 模式：把内存中的好友与聊天数据写回磁盘（有改动才写）。</summary>
    public void FlushLocal()
    {
        string? json = null;
        lock (_localLock)
        {
            if (_local == null || !_localDirty) return;
            json = JsonSerializer.Serialize(_local, FyJsonContext.Default.SocialLocalSnapshot);
            _localDirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
            var temp = LocalPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, LocalPath, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写入 {LocalPath} 失败：{ex.Message}");
            lock (_localLock) _localDirty = true;
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        FlushLocal();
    }

    private async Task EnsureSchemaAsync(UserDatabaseSettings settings, CancellationToken cancellationToken)
    {
        var schemaKey = $"{settings.Provider}|{settings.Host}|{settings.Port}|{settings.Database}|{settings.Username}";
        if (_schemaKey == schemaKey) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaKey == schemaKey) return;
            await using var connection = await OpenAsync(settings, cancellationToken);
            var statements = settings.Provider == "postgresql"
                ? new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_friendships (requester_id INTEGER NOT NULL, addressee_id INTEGER NOT NULL, status VARCHAR(16) NOT NULL, created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, PRIMARY KEY (requester_id, addressee_id))",
                    "CREATE INDEX IF NOT EXISTS ix_fy_friendships_addressee ON fy_friendships (addressee_id)",
                    "CREATE TABLE IF NOT EXISTS fy_chat_messages (id BIGSERIAL PRIMARY KEY, sender_id INTEGER NOT NULL, receiver_id INTEGER NOT NULL, body TEXT NOT NULL, context VARCHAR(128) NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL, delivered BOOLEAN NOT NULL DEFAULT FALSE)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_chat_messages_receiver ON fy_chat_messages (receiver_id, id)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_chat_messages_sender ON fy_chat_messages (sender_id, id)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_chat_messages_created ON fy_chat_messages (created_at)"
                }
                : new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_friendships (requester_id INT NOT NULL, addressee_id INT NOT NULL, status VARCHAR(16) NOT NULL, created_at DATETIME(6) NOT NULL, updated_at DATETIME(6) NOT NULL, PRIMARY KEY (requester_id, addressee_id), INDEX ix_fy_friendships_addressee (addressee_id)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin",
                    "CREATE TABLE IF NOT EXISTS fy_chat_messages (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, sender_id INT NOT NULL, receiver_id INT NOT NULL, body TEXT NOT NULL, context VARCHAR(128) NOT NULL DEFAULT '', created_at DATETIME(6) NOT NULL, delivered TINYINT(1) NOT NULL DEFAULT 0, INDEX ix_fy_chat_messages_receiver (receiver_id, id), INDEX ix_fy_chat_messages_sender (sender_id, id), INDEX ix_fy_chat_messages_created (created_at)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin"
                };
            foreach (var sql in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _schemaKey = schemaKey;
        }
        finally { _schemaLock.Release(); }
    }

    private static async Task<DbConnection> OpenAsync(UserDatabaseSettings settings, CancellationToken cancellationToken)
    {
        DbConnection connection;
        if (settings.Provider == "postgresql")
        {
            var builder = new NpgsqlConnectionStringBuilder { Host = settings.Host, Port = settings.Port, Database = settings.Database, Username = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? SslMode.Require : SslMode.Prefer, Timeout = 8, CommandTimeout = 15, Pooling = true };
            connection = new NpgsqlConnection(builder.ConnectionString);
        }
        else if (settings.Provider == "mysql")
        {
            var builder = new MySqlConnectionStringBuilder { Server = settings.Host, Port = (uint)settings.Port, Database = settings.Database, UserID = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred, ConnectionTimeout = 8, DefaultCommandTimeout = 15, Pooling = true };
            connection = new MySqlConnection(builder.ConnectionString);
        }
        else throw new InvalidOperationException("不支持的好友数据库类型");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<List<FriendshipRecord>> ReadFriendshipsAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<FriendshipRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new FriendshipRecord(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), AsUtc(reader.GetDateTime(3)), AsUtc(reader.GetDateTime(4))));
        return rows;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public enum FriendshipMutationKind { None, CreatePending, Accept, Delete }

/// <summary>好友关系写操作。Accept 时 A 为原申请方、B 为接收方。</summary>
public readonly record struct FriendshipMutation(FriendshipMutationKind Kind, int A, int B, DateTime? CreatedAt = null)
{
    public static FriendshipMutation None => new(FriendshipMutationKind.None, 0, 0);
}

/// <summary>每 6 小时删除超过保留天数的好友聊天记录。</summary>
public sealed class SocialCleanupWorker(SocialService social) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = social.GetSettings().HistoryDays;
                var removed = await social.CleanupMessagesAsync(DateTime.UtcNow.AddDays(-days), stoppingToken);
                if (removed > 0) Console.WriteLine($"已清理 {removed} 条超过 {days} 天的好友聊天记录");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Console.WriteLine($"清理好友聊天记录失败：{ex.GetBaseException().Message}"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

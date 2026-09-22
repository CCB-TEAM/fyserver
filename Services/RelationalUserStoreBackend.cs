using System.Data.Common;
using System.Text.Json;
using fyserver.Models;
using fyserver.Serialization;
using MySqlConnector;
using Npgsql;

namespace fyserver.Services;

/// <summary>MySQL/PostgreSQL 用户存储。完整玩家对象保存在 JSON 文本中，ID/用户名建立关系型索引。</summary>
public sealed class RelationalUserStoreBackend(UserDatabaseSettings settings) : IUserStoreBackend
{
    public string Provider => settings.Provider;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.Provider == "postgresql"
            ? "CREATE TABLE IF NOT EXISTS fy_users (id INTEGER PRIMARY KEY, username VARCHAR(255) NOT NULL UNIQUE, payload TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP)"
            : "CREATE TABLE IF NOT EXISTS fy_users (id INT PRIMARY KEY, username VARCHAR(255) NOT NULL UNIQUE, payload LONGTEXT NOT NULL, updated_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default) =>
        ReadOneAsync("username", userName, cancellationToken);
    public Task<User?> GetByIdAsync(int userId, CancellationToken cancellationToken = default) =>
        ReadOneAsync("id", userId, cancellationToken);

    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(user, StoreJsonContext.Default.User);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.Provider == "postgresql"
            ? "INSERT INTO fy_users (id, username, payload, updated_at) VALUES (@id, @username, @payload, CURRENT_TIMESTAMP) ON CONFLICT (id) DO UPDATE SET username = EXCLUDED.username, payload = EXCLUDED.payload, updated_at = CURRENT_TIMESTAMP"
            : "INSERT INTO fy_users (id, username, payload) VALUES (@id, @username, @payload) ON DUPLICATE KEY UPDATE username = VALUES(username), payload = VALUES(payload), updated_at = CURRENT_TIMESTAMP(6)";
        AddParameter(command, "@id", user.Id);
        AddParameter(command, "@username", user.UserName);
        AddParameter(command, "@payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fy_users WHERE id = @id";
        AddParameter(command, "@id", user.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<User>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM fy_users ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var user = JsonSerializer.Deserialize(reader.GetString(0), StoreJsonContext.Default.User);
            if (user != null) result.Add(user);
        }
        return result;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fy_users";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task CheckpointAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<User?> ReadOneAsync(string column, object value, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM fy_users WHERE {column} = @value";
        AddParameter(command, "@value", value);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrEmpty(payload) ? null : JsonSerializer.Deserialize(payload, StoreJsonContext.Default.User);
    }

    private DbConnection CreateConnection()
    {
        if (settings.Provider == "postgresql")
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = settings.Host, Port = settings.Port, Database = settings.Database,
                Username = settings.Username, Password = settings.Password,
                SslMode = settings.RequireSsl ? SslMode.Require : SslMode.Prefer,
                Timeout = 8, CommandTimeout = 15, Pooling = true
            };
            return new NpgsqlConnection(builder.ConnectionString);
        }
        if (settings.Provider == "mysql")
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = settings.Host, Port = (uint)settings.Port, Database = settings.Database,
                UserID = settings.Username, Password = settings.Password,
                SslMode = settings.RequireSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred,
                ConnectionTimeout = 8, DefaultCommandTimeout = 15, Pooling = true,
                AllowUserVariables = false
            };
            return new MySqlConnection(builder.ConnectionString);
        }
        throw new InvalidOperationException($"不支持的用户数据库类型：{settings.Provider}");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

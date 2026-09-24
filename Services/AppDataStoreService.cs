using System.Data.Common;
using System.Text.Json;
using fyserver.Serialization;
using MySqlConnector;
using Npgsql;

namespace fyserver.Services;

/// <summary>
/// Shared durable key/value store for server-managed application data. The selected
/// player database is the source of truth; local mode uses the existing FASTER KV.
/// </summary>
public sealed class AppDataStoreService(Func<FasterKvService> localDatabaseFactory)
{
    private readonly object _gate = new();
    private UserDatabaseSettings? _settings;
    private FasterKvService? _local;
    private bool _localCheckpointInitialized;

    public bool IsReady { get { lock (_gate) return _settings != null; } }

    public void Initialize(UserDatabaseSettings settings)
    {
        lock (_gate)
        {
            if (settings.Provider == "local")
            {
                _local = localDatabaseFactory();
                _localCheckpointInitialized = File.Exists("./YCDR");
            }
            else
            {
                using var connection = CreateConnection(settings);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = settings.Provider == "postgresql"
                    ? "CREATE TABLE IF NOT EXISTS fy_app_data (data_key VARCHAR(512) PRIMARY KEY, payload TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP)"
                    : "CREATE TABLE IF NOT EXISTS fy_app_data (data_key VARCHAR(512) PRIMARY KEY, payload LONGTEXT NOT NULL, updated_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin";
                command.ExecuteNonQuery();
            }

            _settings = settings;
            ImportLegacyFiles();
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var settings = RequireSettings();
            if (settings.Provider == "local") return _local!.Get<string>(LocalKey(key));
            using var connection = CreateConnection(settings);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM fy_app_data WHERE data_key = @key";
            AddParameter(command, "@key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var settings = RequireSettings();
            if (settings.Provider == "local")
            {
                _local!.Put(LocalKey(key), value);
                EnsureLocalCheckpoint();
                return;
            }
            using var connection = CreateConnection(settings);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = settings.Provider == "postgresql"
                ? "INSERT INTO fy_app_data (data_key, payload, updated_at) VALUES (@key, @payload, CURRENT_TIMESTAMP) ON CONFLICT (data_key) DO UPDATE SET payload = EXCLUDED.payload, updated_at = CURRENT_TIMESTAMP"
                : "INSERT INTO fy_app_data (data_key, payload) VALUES (@key, @payload) ON DUPLICATE KEY UPDATE payload = VALUES(payload), updated_at = CURRENT_TIMESTAMP(6)";
            AddParameter(command, "@key", key);
            AddParameter(command, "@payload", value);
            command.ExecuteNonQuery();
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            var settings = RequireSettings();
            if (settings.Provider == "local")
            {
                var fullKey = LocalKey(key);
                var existed = _local!.Exists(fullKey);
                _local.Delete(fullKey);
                if (existed) EnsureLocalCheckpoint();
                return existed;
            }
            using var connection = CreateConnection(settings);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM fy_app_data WHERE data_key = @key";
            AddParameter(command, "@key", key);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public List<KeyValuePair<string, string>> List(string prefix)
    {
        lock (_gate)
        {
            var settings = RequireSettings();
            if (settings.Provider == "local")
            {
                return _local!.GetKeysByPrefix(LocalKey(prefix))
                    .Select(key => new KeyValuePair<string, string>(key["app-data:".Length..], _local.Get<string>(key) ?? ""))
                    .ToList();
            }
            using var connection = CreateConnection(settings);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data_key, payload FROM fy_app_data WHERE data_key LIKE @prefix";
            AddParameter(command, "@prefix", EscapeLike(prefix) + "%");
            var result = new List<KeyValuePair<string, string>>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
            return result;
        }
    }

    public void ImportLegacyFile(string key, string path)
    {
        if (Get(key) != null || !File.Exists(path)) return;
        Set(key, File.ReadAllText(path));
    }

    private void ImportLegacyFiles()
    {
        if (Get("meta:legacy-import:v1") == "complete") return;
        (string Key, string Path)[] files =
        [
            ("server:settings", "./setting.json"),
            ("store:config", "./config/store.json"),
            ("content:frontpage", "./config/frontpage.json"),
            ("content:skirmish", "./config/skirmish.json"),
            ("content:knockout", "./config/knockout.json"),
            ("client:server-options", "./config/serverOptions.json"),
            ("client:flags", "./config/serverOptions.flags.json"),
            ("client:comments", "./config/serverOptions.comments.json"),
            ("match:retention", "./data/match-retention.json"),
            ("session:mini-sit-n-go", "./config/current_mini_sit_n_go.json"),
            ("redeem:codes", "./data/redeem-codes.json"),
            ("admin:accounts", "./data/admin-auth.json")
        ];
        foreach (var (key, path) in files) ImportLegacyFile(key, path);

        ImportJsonLines("admin:audit:", "./data/admin-audit.jsonl", "time");
        ImportJsonLines("admin:login:", "./data/admin-logins.jsonl", "time");
        ImportMatchDocuments("./data/match-history");
        ImportAssets("./wwwroot/admin-ui/uploads", "asset:admin:");
        ImportAssets("./wwwroot/admin-ui/uploads/frontpage", "asset:frontpage:");
        Set("meta:legacy-import:v1", "complete");
    }

    private void ImportJsonLines(string prefix, string path, string timeField)
    {
        if (!File.Exists(path) || List(prefix).Count != 0) return;
        var index = 0;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var timestamp = doc.RootElement.TryGetProperty(timeField, out var value) ? value.GetString() : null;
                Set(prefix + (timestamp ?? "") + ":" + (index++).ToString("D8"), line);
            }
            catch (JsonException) { }
        }
    }

    private void ImportAssets(string directory, string prefix)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var key = prefix + name;
            if (Get(key) != null) continue;
            Set(key, Convert.ToBase64String(File.ReadAllBytes(file)));
        }
    }

    private void ImportMatchDocuments(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(id, out var matchId)) continue;
            var key = "match:document:" + matchId;
            if (Get(key) != null) continue;
            Set(key, File.ReadAllText(file));
        }
    }

    private UserDatabaseSettings RequireSettings() => _settings ?? throw new InvalidOperationException("应用数据数据库尚未初始化");
    private void EnsureLocalCheckpoint()
    {
        if (_localCheckpointInitialized) return;
        _local!.Checkpoint(FASTER.core.CheckpointType.FoldOver);
        _localCheckpointInitialized = true;
    }
    private static string LocalKey(string key) => "app-data:" + key;
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static DbConnection CreateConnection(UserDatabaseSettings settings)
    {
        if (settings.Provider == "postgresql")
        {
            var builder = new NpgsqlConnectionStringBuilder { Host = settings.Host, Port = settings.Port, Database = settings.Database,
                Username = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? SslMode.Require : SslMode.Prefer,
                Timeout = 8, CommandTimeout = 15, Pooling = true };
            return new NpgsqlConnection(builder.ConnectionString);
        }
        var mysql = new MySqlConnectionStringBuilder { Server = settings.Host, Port = (uint)settings.Port, Database = settings.Database,
            UserID = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred,
            ConnectionTimeout = 8, DefaultCommandTimeout = 15, Pooling = true };
        return new MySqlConnection(mysql.ConnectionString);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

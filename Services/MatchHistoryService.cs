using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Serialization;
using MySqlConnector;
using Npgsql;

namespace fyserver.Services;

/// <summary>
/// 对局历史持久化。关系型模式与玩家数据共用已配置数据库；本地模式使用独立原子 JSON 文件，
/// 以便未迁移到 PostgreSQL 的开发实例仍可验证回放功能。
/// </summary>
public sealed class MatchHistoryService(UserDatabaseConfigurationService databaseConfiguration)
{
    private const string LocalPath = "./data/match-history";
    private const string RetentionPath = "./data/match-retention.json";
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly object _retentionLock = new();
    private bool _schemaReady;
    private string? _schemaKey;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings != null && settings.Provider != "local") await EnsureSchemaAsync(settings, cancellationToken);
    }

    public MatchRetentionSettings GetRetentionSettings()
    {
        lock (_retentionLock)
        {
            if (!File.Exists(RetentionPath)) return DefaultRetention;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(RetentionPath))?.AsObject();
                return NormalizeRetention(new MatchRetentionSettings(
                    root?["mode"]?.GetValue<string>() ?? DefaultRetention.Mode,
                    root?["keepCount"]?.GetValue<int>() ?? DefaultRetention.KeepCount,
                    root?["keepDays"]?.GetValue<int>() ?? DefaultRetention.KeepDays,
                    root?["cleanupDayUtc"]?.GetValue<int>() ?? DefaultRetention.CleanupDayUtc,
                    root?["cleanupHourUtc"]?.GetValue<int>() ?? DefaultRetention.CleanupHourUtc));
            }
            catch { return DefaultRetention; }
        }
    }

    public void SaveRetentionSettings(MatchRetentionSettings settings)
    {
        var normalized = NormalizeRetention(settings);
        lock (_retentionLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RetentionPath)!);
            var temp = RetentionPath + ".tmp";
            var json = new JsonObject
            {
                ["mode"] = normalized.Mode,
                ["keepCount"] = normalized.KeepCount,
                ["keepDays"] = normalized.KeepDays,
                ["cleanupDayUtc"] = normalized.CleanupDayUtc,
                ["cleanupHourUtc"] = normalized.CleanupHourUtc
            };
            File.WriteAllText(temp, json.ToJsonString(new() { WriteIndented = true }));
            File.Move(temp, RetentionPath, true);
        }
    }

    public async Task SaveSnapshotAsync(MatchInfo match, MatchStartingInfo startingInfo, CancellationToken cancellationToken = default)
    {
        match.StartedAtUtc ??= DateTime.UtcNow;
        match.MatchStartingInfo = startingInfo;
        var document = BuildDocument(match, startingInfo);
        await WriteDocumentAsync(document, cancellationToken);
    }

    public async Task AppendActionsAsync(MatchInfo match, IReadOnlyCollection<MatchAction> actions, CancellationToken cancellationToken = default)
    {
        if (actions.Count == 0) return;
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            var path = LocalFile(match.MatchId);
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(path, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                var known = document.Actions.Select(x => x.ActionId).ToHashSet();
                document.Actions.AddRange(actions.Where(x => known.Add(x.ActionId)).OrderBy(x => x.ActionId));
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(path, document, cancellationToken);
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
            AddParameter(exists, "@match", match.MatchId);
            if (await exists.ExecuteScalarAsync(cancellationToken) == null)
                await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
        }
        var inserted = 0;
        foreach (var action in actions.DistinctBy(x => x.ActionId))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = settings.Provider == "postgresql"
                ? "INSERT INTO fy_match_events (match_id, action_id, payload) VALUES (@match, @action, @payload) ON CONFLICT (match_id, action_id) DO NOTHING"
                : "INSERT IGNORE INTO fy_match_events (match_id, action_id, payload) VALUES (@match, @action, @payload)";
            AddParameter(command, "@match", match.MatchId);
            AddParameter(command, "@action", action.ActionId);
            AddParameter(command, "@payload", JsonSerializer.Serialize(action, FyJsonContext.Default.MatchAction));
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE fy_match_history SET action_count = action_count + @inserted, turns = @turns WHERE match_id = @match";
            AddParameter(update, "@match", match.MatchId);
            AddParameter(update, "@inserted", inserted);
            AddParameter(update, "@turns", match.Turns);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(MatchInfo match, CancellationToken cancellationToken = default)
    {
        match.CompletedAtUtc ??= DateTime.UtcNow;
        match.IsCompleted = true;
        match.IsAborted = false;
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            var path = LocalFile(match.MatchId);
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(path, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(path, document, cancellationToken);
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
    }

    public async Task MarkAbortedAsync(MatchInfo match, CancellationToken cancellationToken = default)
    {
        match.CompletedAtUtc ??= DateTime.UtcNow;
        match.IsAborted = true;
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            var path = LocalFile(match.MatchId);
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(path, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(path, document, cancellationToken);
            });
            return;
        }
        await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
    }

    public async Task<IReadOnlyList<MatchHistorySummary>> ListRecentAsync(int limit, bool completedOnly = true, int? playerId = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var settings = databaseConfiguration.Load();
        if (settings == null) return [];
        if (settings.Provider == "local")
        {
            Directory.CreateDirectory(LocalPath);
            var result = new List<MatchHistorySummary>();
            foreach (var path in Directory.EnumerateFiles(LocalPath, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var document = await ReadLocalAsync(path, cancellationToken);
                if (document != null && (!completedOnly || document.Summary.Status == "completed") &&
                    (playerId == null || document.Summary.LeftPlayerId == playerId || document.Summary.RightPlayerId == playerId)) result.Add(document.Summary);
                if (result.Count >= limit) break;
            }
            return result.OrderByDescending(x => x.CompletedAt ?? x.StartedAt).ToList();
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload, action_count, turns, status, completed_at FROM fy_match_history {(completedOnly ? "WHERE status = 'completed'" : "WHERE 1 = 1")} AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) ORDER BY COALESCE(completed_at, started_at) DESC LIMIT @limit";
        AddParameter(command, "@limit", limit);
        AddParameter(command, "@player", playerId);
        var rows = new List<MatchHistorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            if (document != null)
            {
                DateTime? completedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                rows.Add(document.Summary with
                {
                    ActionCount = reader.GetInt32(1),
                    Turns = reader.GetInt32(2),
                    Status = reader.GetString(3),
                    CompletedAt = completedAt
                });
            }
        }
        return rows;
    }

    public async Task<(IReadOnlyList<MatchHistorySummary> Matches, int Total)> ListAdminAsync(
        int page, int pageSize, string? status = null, int? playerId = null, string? playerName = null, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        status = string.IsNullOrWhiteSpace(status) || status == "all" ? null : status;
        playerName = string.IsNullOrWhiteSpace(playerName) ? null : playerName.Trim();
        var settings = databaseConfiguration.Load();
        if (settings == null) return ([], 0);
        if (settings.Provider == "local")
        {
            if (!Directory.Exists(LocalPath)) return ([], 0);
            var matches = new List<MatchHistorySummary>();
            foreach (var path in Directory.EnumerateFiles(LocalPath, "*.json"))
            {
                var document = await ReadLocalAsync(path, cancellationToken);
                var summary = document?.Summary;
                if (summary == null || status != null && summary.Status != status ||
                    playerId != null && summary.LeftPlayerId != playerId && summary.RightPlayerId != playerId ||
                    playerName != null && !summary.LeftPlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase) &&
                    !summary.RightPlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(summary with { ActionCount = document!.Actions.Count });
            }
            var ordered = matches.OrderByDescending(x => x.CompletedAt ?? x.StartedAt).ThenByDescending(x => x.MatchId).ToList();
            return (ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count);
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM fy_match_history WHERE (@status IS NULL OR status = @status) AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) AND (@playerName IS NULL OR LOWER(left_player_name) LIKE @playerName OR LOWER(right_player_name) LIKE @playerName)";
        AddParameter(count, "@status", status);
        AddParameter(count, "@player", playerId);
        AddParameter(count, "@playerName", playerName == null ? null : "%" + playerName.ToLowerInvariant() + "%");
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload, action_count, turns, status, completed_at FROM fy_match_history WHERE (@status IS NULL OR status = @status) AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) AND (@playerName IS NULL OR LOWER(left_player_name) LIKE @playerName OR LOWER(right_player_name) LIKE @playerName) ORDER BY COALESCE(completed_at, started_at) DESC, match_id DESC LIMIT @limit OFFSET @offset";
        AddParameter(command, "@status", status);
        AddParameter(command, "@player", playerId);
        AddParameter(command, "@playerName", playerName == null ? null : "%" + playerName.ToLowerInvariant() + "%");
        AddParameter(command, "@limit", pageSize);
        AddParameter(command, "@offset", (page - 1) * pageSize);
        var rows = new List<MatchHistorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            if (document == null) continue;
            rows.Add(document.Summary with
            {
                ActionCount = reader.GetInt32(1),
                Turns = reader.GetInt32(2),
                Status = reader.GetString(3),
                CompletedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
            });
        }
        return (rows, total);
    }

    public async Task<JsonObject> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return new JsonObject { ["configured"] = false, ["provider"] = "unconfigured", ["matchCount"] = 0, ["actionCount"] = 0, ["dataBytes"] = 0 };
        if (settings.Provider == "local")
        {
            long bytes = 0;
            var matchCount = 0;
            var actionCount = 0;
            if (Directory.Exists(LocalPath))
            {
                foreach (var path in Directory.EnumerateFiles(LocalPath, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes += new FileInfo(path).Length;
                    var document = await ReadLocalAsync(path, cancellationToken);
                    if (document == null) continue;
                    matchCount++;
                    actionCount += document.Actions.Count;
                }
            }
            return new JsonObject { ["configured"] = true, ["provider"] = "本地模式（玩家库：FASTER）", ["location"] = Path.GetFullPath(LocalPath), ["matchCount"] = matchCount, ["actionCount"] = actionCount, ["dataBytes"] = bytes };
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(action_count), 0), COALESCE((SELECT SUM(LENGTH(payload)) FROM fy_match_history), 0) + COALESCE((SELECT SUM(LENGTH(payload)) FROM fy_match_events), 0) FROM fy_match_history";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new JsonObject
        {
            ["configured"] = true,
            ["provider"] = settings.Provider,
            ["location"] = settings.Database + ".fy_match_history / fy_match_events",
            ["matchCount"] = Convert.ToInt64(reader.GetValue(0)),
            ["actionCount"] = Convert.ToInt64(reader.GetValue(1)),
            ["dataBytes"] = Convert.ToInt64(reader.GetValue(2))
        };
    }

    public async Task<bool> DeleteAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return false;
        if (settings.Provider == "local")
        {
            var path = LocalFile(matchId);
            if (!File.Exists(path)) return false;
            await WithFileLockAsync(matchId, () =>
            {
                if (File.Exists(path)) File.Delete(path);
                return Task.CompletedTask;
            });
            return true;
        }
        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fy_match_history WHERE match_id = @match";
        AddParameter(command, "@match", matchId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<MatchHistoryDocument?> GetAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return null;
        MatchHistoryDocument? document;
        if (settings.Provider == "local")
        {
            document = await ReadLocalAsync(LocalFile(matchId), cancellationToken);
            if (document == null) return null;
            return document;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        var actionCount = 0;
        var turns = 0;
        string? status = null;
        DateTime? completedAt = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload, action_count, turns, status, completed_at FROM fy_match_history WHERE match_id = @match";
            AddParameter(command, "@match", matchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            actionCount = reader.GetInt32(1);
            turns = reader.GetInt32(2);
            status = reader.GetString(3);
            completedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
        }
        if (document == null) return null;
        document.Summary = document.Summary with { ActionCount = actionCount, Turns = turns, Status = status!, CompletedAt = completedAt };
        return document;
    }

    public async Task<bool> ExistsAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return false;
        if (settings.Provider == "local") return File.Exists(LocalFile(matchId));
        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
        AddParameter(command, "@match", matchId);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task<MatchHistoryActionPage?> GetActionsAsync(int matchId, int afterActionId, int limit = 500, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var settings = databaseConfiguration.Load();
        if (settings == null) return null;
        List<MatchAction> actions;
        if (settings.Provider == "local")
        {
            var document = await ReadLocalAsync(LocalFile(matchId), cancellationToken);
            if (document == null) return null;
            actions = document.Actions.Where(x => x.ActionId > afterActionId).OrderBy(x => x.ActionId).Take(limit + 1).ToList();
        }
        else
        {
            await EnsureSchemaAsync(settings, cancellationToken);
            await using var connection = CreateConnection(settings);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM fy_match_events WHERE match_id = @match AND action_id > @after ORDER BY action_id LIMIT @limit";
            AddParameter(command, "@match", matchId);
            AddParameter(command, "@after", afterActionId);
            AddParameter(command, "@limit", limit + 1);
            actions = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchAction);
                if (action != null) actions.Add(action);
            }
            if (actions.Count == 0)
            {
                await using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
                AddParameter(exists, "@match", matchId);
                if (await exists.ExecuteScalarAsync(cancellationToken) == null) return null;
            }
        }
        var hasMore = actions.Count > limit;
        if (hasMore) actions.RemoveAt(actions.Count - 1);
        var next = actions.Count == 0 ? afterActionId : actions[^1].ActionId;
        return new MatchHistoryActionPage(matchId, next, hasMore, actions);
    }

    public async Task<int> CleanupAsync(MatchRetentionSettings? options = null, CancellationToken cancellationToken = default)
    {
        options = NormalizeRetention(options ?? GetRetentionSettings());
        var settings = databaseConfiguration.Load();
        if (settings == null) return 0;
        if (settings.Provider == "local")
        {
            if (!Directory.Exists(LocalPath)) return 0;
            var docs = new List<(string Path, MatchHistorySummary Summary)>();
            foreach (var path in Directory.EnumerateFiles(LocalPath, "*.json"))
            {
                var doc = await ReadLocalAsync(path, cancellationToken);
                if (doc?.Summary.Status is "completed" or "aborted") docs.Add((path, doc.Summary));
            }
            var expired = SelectExpired(docs.Select(x => (x.Path, x.Summary)).ToList(), options);
            foreach (var item in expired) File.Delete(item.Path);
            return expired.Count;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        List<int> ids;
        await using (var command = connection.CreateCommand())
        {
            if (options.Mode == "count")
            {
                command.CommandText = settings.Provider == "postgresql"
                    ? "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') ORDER BY completed_at DESC, match_id DESC OFFSET @keep"
                    : "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') ORDER BY completed_at DESC, match_id DESC LIMIT 18446744073709551615 OFFSET @keep";
                AddParameter(command, "@keep", options.KeepCount);
            }
            else
            {
                command.CommandText = "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') AND completed_at < @cutoff ORDER BY completed_at";
                AddParameter(command, "@cutoff", DateTime.UtcNow.AddDays(-options.KeepDays));
            }
            ids = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        }
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM fy_match_history WHERE match_id = @match";
            AddParameter(command, "@match", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return ids.Count;
    }

    public static MatchRetentionSettings DefaultRetention { get; } = new("age", 1000, 30, 0, 3);

    public static MatchRetentionSettings NormalizeRetention(MatchRetentionSettings value) => new(
        value.Mode == "count" ? "count" : "age",
        Math.Clamp(value.KeepCount, 1, 1_000_000),
        Math.Clamp(value.KeepDays, 1, 3650),
        Math.Clamp(value.CleanupDayUtc, 0, 6),
        Math.Clamp(value.CleanupHourUtc, 0, 23));

    private async Task WriteDocumentAsync(MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            var path = LocalFile(document.Summary.MatchId);
            await WithFileLockAsync(document.Summary.MatchId, async () =>
            {
                var existing = await ReadLocalAsync(path, cancellationToken);
                if (existing != null) document.Actions = existing.Actions;
                await WriteLocalAsync(path, document, cancellationToken);
            });
            return;
        }
        await UpsertHeaderAsync(settings, document, cancellationToken);
    }

    private async Task UpsertHeaderAsync(UserDatabaseSettings settings, MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(settings, cancellationToken);
        var summary = document.Summary;
        var payload = JsonSerializer.Serialize(new MatchHistoryDocument
        {
            Summary = document.Summary,
            StartingInfo = document.StartingInfo,
            Actions = []
        }, FyJsonContext.Default.MatchHistoryDocument);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.Provider == "postgresql"
            ? "INSERT INTO fy_match_history (match_id, status, match_type, started_at, completed_at, left_player_id, left_player_name, left_player_tag, right_player_id, right_player_name, right_player_tag, turns, winner_side, action_count, payload) VALUES (@match, @status, @type, @started, @completed, @left_id, @left_name, @left_tag, @right_id, @right_name, @right_tag, @turns, @winner, (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), @payload) ON CONFLICT (match_id) DO UPDATE SET status = EXCLUDED.status, match_type = EXCLUDED.match_type, started_at = EXCLUDED.started_at, completed_at = EXCLUDED.completed_at, left_player_id = EXCLUDED.left_player_id, left_player_name = EXCLUDED.left_player_name, left_player_tag = EXCLUDED.left_player_tag, right_player_id = EXCLUDED.right_player_id, right_player_name = EXCLUDED.right_player_name, right_player_tag = EXCLUDED.right_player_tag, turns = EXCLUDED.turns, winner_side = EXCLUDED.winner_side, action_count = (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), payload = EXCLUDED.payload"
            : "INSERT INTO fy_match_history (match_id, status, match_type, started_at, completed_at, left_player_id, left_player_name, left_player_tag, right_player_id, right_player_name, right_player_tag, turns, winner_side, action_count, payload) VALUES (@match, @status, @type, @started, @completed, @left_id, @left_name, @left_tag, @right_id, @right_name, @right_tag, @turns, @winner, (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), @payload) ON DUPLICATE KEY UPDATE status = VALUES(status), match_type = VALUES(match_type), started_at = VALUES(started_at), completed_at = VALUES(completed_at), left_player_id = VALUES(left_player_id), left_player_name = VALUES(left_player_name), left_player_tag = VALUES(left_player_tag), right_player_id = VALUES(right_player_id), right_player_name = VALUES(right_player_name), right_player_tag = VALUES(right_player_tag), turns = VALUES(turns), winner_side = VALUES(winner_side), action_count = (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), payload = VALUES(payload)";
        AddParameter(command, "@match", summary.MatchId);
        AddParameter(command, "@status", summary.Status);
        AddParameter(command, "@type", summary.MatchType);
        AddParameter(command, "@started", summary.StartedAt);
        AddParameter(command, "@completed", summary.CompletedAt is null ? DBNull.Value : summary.CompletedAt.Value);
        AddParameter(command, "@left_id", summary.LeftPlayerId);
        AddParameter(command, "@left_name", summary.LeftPlayerName);
        AddParameter(command, "@left_tag", summary.LeftPlayerTag);
        AddParameter(command, "@right_id", summary.RightPlayerId);
        AddParameter(command, "@right_name", summary.RightPlayerName);
        AddParameter(command, "@right_tag", summary.RightPlayerTag);
        AddParameter(command, "@turns", summary.Turns);
        AddParameter(command, "@winner", summary.WinnerSide is null ? DBNull.Value : summary.WinnerSide);
        AddParameter(command, "@payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(UserDatabaseSettings settings, CancellationToken cancellationToken)
    {
        var schemaKey = $"{settings.Provider}|{settings.Host}|{settings.Port}|{settings.Database}|{settings.Username}";
        if (_schemaReady && _schemaKey == schemaKey) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady && _schemaKey == schemaKey) return;
            await using var connection = CreateConnection(settings);
            await connection.OpenAsync(cancellationToken);
            var statements = settings.Provider == "postgresql"
                ? new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_match_history (match_id INTEGER PRIMARY KEY, status VARCHAR(16) NOT NULL, match_type VARCHAR(32) NOT NULL, started_at TIMESTAMPTZ NOT NULL, completed_at TIMESTAMPTZ NULL, left_player_id INTEGER NOT NULL, left_player_name VARCHAR(255) NOT NULL, left_player_tag VARCHAR(16) NOT NULL, right_player_id INTEGER NOT NULL, right_player_name VARCHAR(255) NOT NULL, right_player_tag VARCHAR(16) NOT NULL, turns INTEGER NOT NULL DEFAULT 0, winner_side VARCHAR(8) NULL, action_count INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_recent ON fy_match_history (status, completed_at DESC)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_left_player ON fy_match_history (left_player_id, completed_at DESC)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_right_player ON fy_match_history (right_player_id, completed_at DESC)",
                    "CREATE TABLE IF NOT EXISTS fy_match_events (match_id INTEGER NOT NULL REFERENCES fy_match_history(match_id) ON DELETE CASCADE, action_id INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (match_id, action_id))"
                }
                : new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_match_history (match_id INT PRIMARY KEY, status VARCHAR(16) NOT NULL, match_type VARCHAR(32) NOT NULL, started_at DATETIME(6) NOT NULL, completed_at DATETIME(6) NULL, left_player_id INT NOT NULL, left_player_name VARCHAR(255) NOT NULL, left_player_tag VARCHAR(16) NOT NULL, right_player_id INT NOT NULL, right_player_name VARCHAR(255) NOT NULL, right_player_tag VARCHAR(16) NOT NULL, turns INT NOT NULL DEFAULT 0, winner_side VARCHAR(8) NULL, action_count INT NOT NULL DEFAULT 0, payload LONGTEXT NOT NULL, INDEX ix_fy_match_history_recent (status, completed_at), INDEX ix_fy_match_history_left_player (left_player_id, completed_at), INDEX ix_fy_match_history_right_player (right_player_id, completed_at)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin",
                    "CREATE TABLE IF NOT EXISTS fy_match_events (match_id INT NOT NULL, action_id INT NOT NULL, payload LONGTEXT NOT NULL, PRIMARY KEY (match_id, action_id), CONSTRAINT fk_fy_match_events_history FOREIGN KEY (match_id) REFERENCES fy_match_history(match_id) ON DELETE CASCADE) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin"
                };
            foreach (var sql in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _schemaReady = true;
            _schemaKey = schemaKey;
        }
        finally { _schemaLock.Release(); }
    }

    private static MatchHistoryDocument BuildDocument(MatchInfo match, MatchStartingInfo? startingInfo) => new()
    {
        Summary = ToSummary(match, match.StartedAtUtc ?? DateTime.UtcNow),
        StartingInfo = startingInfo,
        Actions = match.SnapshotActions()
    };

    public static MatchHistorySummary CreateSummary(MatchInfo match) => ToSummary(match, match.StartedAtUtc ?? DateTime.UtcNow);

    private static MatchHistorySummary ToSummary(MatchInfo match, DateTime startedAt)
    {
        var data = match.MatchStartingInfo?.MatchAndStartingData.StartingData;
        var leftId = match.Left?.PlayerId ?? data?.PlayerIdLeft ?? 0;
        var rightId = match.Right?.PlayerId ?? data?.PlayerIdRight ?? 0;
        var mode = match.Ex.StartsWith("battle_code", StringComparison.Ordinal) ? "code" : string.IsNullOrWhiteSpace(match.Ex) ? "classic" : match.Ex;
        return new MatchHistorySummary(match.MatchId, mode, match.IsAborted ? "aborted" : match.IsCompleted ? "completed" : "active", startedAt,
            match.CompletedAtUtc, leftId, data?.LeftPlayerName ?? $"#{leftId}", data?.LeftPlayerTag ?? "0000",
            rightId, data?.RightPlayerName ?? $"#{rightId}", data?.RightPlayerTag ?? "0000", match.Turns,
            match.MatchActions.Count, match.WinnerSide);
    }

    private static List<(string Path, MatchHistorySummary Summary)> SelectExpired(List<(string Path, MatchHistorySummary Summary)> items, MatchRetentionSettings options)
    {
        var completed = items.Where(x => x.Summary.Status is "completed" or "aborted").OrderByDescending(x => x.Summary.CompletedAt).ToList();
        return options.Mode == "count"
            ? completed.Skip(options.KeepCount).ToList()
            : completed.Where(x => x.Summary.CompletedAt < DateTime.UtcNow.AddDays(-options.KeepDays)).ToList();
    }

    private static string LocalFile(int matchId) => Path.Combine(LocalPath, matchId + ".json");
    private static async Task<MatchHistoryDocument?> ReadLocalAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), FyJsonContext.Default.MatchHistoryDocument); }
        catch { return null; }
    }
    private static async Task WriteLocalAsync(string path, MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, FyJsonContext.Default.MatchHistoryDocument), cancellationToken);
        File.Move(temp, path, true);
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> LocalLocks = new();
    private static async Task WithFileLockAsync(int id, Func<Task> action)
    {
        var gate = LocalLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await action(); } finally { gate.Release(); }
    }

    private static DbConnection CreateConnection(UserDatabaseSettings settings)
    {
        if (settings.Provider == "postgresql")
        {
            var builder = new NpgsqlConnectionStringBuilder { Host = settings.Host, Port = settings.Port, Database = settings.Database, Username = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? SslMode.Require : SslMode.Prefer, Timeout = 8, CommandTimeout = 15, Pooling = true };
            return new NpgsqlConnection(builder.ConnectionString);
        }
        if (settings.Provider == "mysql")
        {
            var builder = new MySqlConnectionStringBuilder { Server = settings.Host, Port = (uint)settings.Port, Database = settings.Database, UserID = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred, ConnectionTimeout = 8, DefaultCommandTimeout = 15, Pooling = true };
            return new MySqlConnection(builder.ConnectionString);
        }
        throw new InvalidOperationException("不支持的对局历史数据库类型");
    }
    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed class MatchHistoryCleanupWorker(MatchHistoryService history, ILogger<MatchHistoryCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastRunForSchedule = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = history.GetRetentionSettings();
            var now = DateTime.UtcNow;
            var daysSince = ((int)now.DayOfWeek - settings.CleanupDayUtc + 7) % 7;
            var scheduled = now.Date.AddDays(-daysSince).AddHours(settings.CleanupHourUtc);
            if (now >= scheduled && (lastRunForSchedule == null || scheduled > lastRunForSchedule))
            {
                try
                {
                    var removed = await history.CleanupAsync(settings, stoppingToken);
                    logger.LogInformation("Weekly match-history cleanup removed {Count} matches", removed);
                    lastRunForSchedule = scheduled;
                }
                catch (Exception ex) { logger.LogError(ex, "Weekly match-history cleanup failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

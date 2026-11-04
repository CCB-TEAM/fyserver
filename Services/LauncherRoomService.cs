using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 启动器房间大厅。房间只存在内存里：对局跑在房主电脑上的嵌入版 fyserver，云端只记“谁开了房、谁在里面”。
/// 房主 45 秒没有心跳即关闭房间；成员 90 秒没有心跳即移出。每个账号同一时间只能在一个房间里。
/// 房间事件通过启动器控制 WebSocket 推给房内成员（room_updated / room_closed / kicked）。
/// </summary>
public sealed class LauncherRoomService : IDisposable
{
    public const int MaxRoomSize = 10;
    public static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MemberTimeout = TimeSpan.FromSeconds(90);
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly string[] Visibilities = ["public", "password", "friends"];

    public sealed class Member
    {
        public int Id;
        public string Name = "";
        public string Role = "member";
        public DateTime JoinedAt;
        public DateTime SeenAt;
    }

    public sealed class Room
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public int HostId;
        public string HostName = "";
        public string Visibility = "public";
        public byte[]? PasswordSalt;
        public byte[]? PasswordHash;
        public int MaxPlayers = MaxRoomSize;
        public JsonArray Mods = [];
        public string? ModpackId;
        public string GameVersion = "";
        public DateTime CreatedAt;
        public readonly Dictionary<int, Member> Members = new();
        public readonly HashSet<int> Kicked = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly LauncherHubService _hub;
    private readonly Timer _sweeper;

    /// <summary>这些账号所在的房间变了（进房、退房、房间关闭），用于给他们的好友推送在线状态。</summary>
    public Func<int[], Task>? PresenceChanged { get; set; }

    public LauncherRoomService(LauncherHubService hub)
    {
        _hub = hub;
        _sweeper = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public readonly record struct Result(bool Ok, int Status, string Message, Room? Room = null);

    // ==================== 查询 ====================

    public Room? Get(string id)
    {
        lock (_gate) return _rooms.GetValueOrDefault(id.Trim().ToUpperInvariant());
    }

    /// <summary>该账号当前所在的房间（房主或成员）。</summary>
    public Room? RoomOf(int accountId)
    {
        lock (_gate) return _rooms.Values.FirstOrDefault(r => r.Members.ContainsKey(accountId));
    }

    public List<Room> All()
    {
        lock (_gate) return _rooms.Values.ToList();
    }

    /// <summary>大厅列表：公开房、密码房，以及房主是自己好友的“仅好友”房。</summary>
    public List<Room> ListVisible(int viewerId, ISet<int> friendIds)
    {
        lock (_gate)
            return _rooms.Values
                .Where(r => r.Visibility != "friends" || r.HostId == viewerId || friendIds.Contains(r.HostId) || r.Members.ContainsKey(viewerId))
                .OrderByDescending(r => r.Members.Count < r.MaxPlayers)
                .ThenByDescending(r => r.CreatedAt)
                .ToList();
    }

    public JsonObject ToJson(Room room, int? viewerId = null)
    {
        lock (_gate)
        {
            var json = new JsonObject
            {
                ["id"] = room.Id,
                ["name"] = room.Name,
                ["description"] = room.Description,
                ["host"] = new JsonObject { ["id"] = room.HostId, ["username"] = room.HostName },
                ["visibility"] = room.Visibility,
                ["hasPassword"] = room.PasswordHash != null,
                ["maxPlayers"] = room.MaxPlayers,
                ["playerCount"] = room.Members.Count,
                ["members"] = new JsonArray(room.Members.Values.OrderBy(m => m.JoinedAt)
                    .Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id, ["username"] = m.Name, ["role"] = m.Role, ["joinedAt"] = m.JoinedAt.ToString("O") })
                    .ToArray()),
                ["mods"] = room.Mods.DeepClone(),
                ["modpackId"] = room.ModpackId,
                ["gameVersion"] = room.GameVersion,
                ["createdAt"] = room.CreatedAt.ToString("O")
            };
            if (viewerId is { } v) json["joined"] = room.Members.ContainsKey(v);
            return json;
        }
    }

    // ==================== 房主操作 ====================

    public Result Create(int hostId, string hostName, JsonObject body)
    {
        var (fields, error) = ReadFields(body, null);
        if (error != null) return new(false, 400, error);
        if ((fields.Visibility ?? "public") == "password" && fields.Password == null) return new(false, 400, "密码房需要设置密码");
        Room room;
        Room? previous;
        lock (_gate)
        {
            previous = _rooms.Values.FirstOrDefault(r => r.Members.ContainsKey(hostId));
            if (previous != null)
            {
                if (previous.HostId == hostId) _rooms.Remove(previous.Id);
                else previous.Members.Remove(hostId);
            }
            room = new Room
            {
                Id = NewCode(),
                HostId = hostId,
                HostName = hostName,
                CreatedAt = DateTime.UtcNow
            };
            Apply(room, fields);
            var now = DateTime.UtcNow;
            room.Members[hostId] = new Member { Id = hostId, Name = hostName, Role = "host", JoinedAt = now, SeenAt = now };
            _rooms[room.Id] = room;
        }
        if (previous != null)
        {
            if (previous.HostId == hostId) _ = NotifyClosedAsync(previous, "房主开了新房间");
            else _ = NotifyChangedAsync(previous, hostId);
        }
        Console.WriteLine($"启动器房间 {room.Id} 已创建：房主 {hostName}（#{hostId}）");
        return new(true, 201, "房间已创建", room);
    }

    public Result Update(string id, int hostId, JsonObject body)
    {
        Room? room;
        lock (_gate)
        {
            room = _rooms.GetValueOrDefault(id);
            if (room == null) return new(false, 404, "房间不存在或已关闭");
            if (room.HostId != hostId) return new(false, 403, "只有房主可以修改房间");
            var (fields, error) = ReadFields(body, room);
            if (error != null) return new(false, 400, error);
            if (fields.MaxPlayers is { } max && max < room.Members.Count) return new(false, 400, "人数上限不能小于当前人数");
            var becomesPassword = (fields.Visibility ?? room.Visibility) == "password";
            if (becomesPassword && fields.Password == null && (room.PasswordHash == null || fields.ClearPassword))
                return new(false, 400, "密码房需要设置密码");
            Apply(room, fields);
        }
        _ = NotifyChangedAsync(room);
        return new(true, 200, "房间已更新", room);
    }

    public Result Kick(string id, int hostId, int targetId)
    {
        Room? room;
        lock (_gate)
        {
            room = _rooms.GetValueOrDefault(id);
            if (room == null) return new(false, 404, "房间不存在或已关闭");
            if (room.HostId != hostId) return new(false, 403, "只有房主可以踢人");
            if (targetId == hostId) return new(false, 400, "不能踢出自己");
            if (!room.Members.Remove(targetId)) return new(false, 404, "该玩家不在房间里");
            room.Kicked.Add(targetId);
        }
        _ = _hub.SendAsync(targetId, new JsonObject { ["type"] = "kicked", ["roomId"] = room.Id, ["message"] = "你已被房主移出房间" });
        _ = NotifyChangedAsync(room, targetId);
        return new(true, 200, "已移出房间", room);
    }

    public Result Close(string id, int? hostId, string reason)
    {
        Room? room;
        lock (_gate)
        {
            room = _rooms.GetValueOrDefault(id);
            if (room == null) return new(false, 404, "房间不存在或已关闭");
            if (hostId != null && room.HostId != hostId) return new(false, 403, "只有房主可以关闭房间");
            _rooms.Remove(id);
        }
        _ = NotifyClosedAsync(room, reason);
        Console.WriteLine($"启动器房间 {room.Id} 已关闭：{reason}");
        return new(true, 200, "房间已关闭", room);
    }

    // ==================== 成员操作 ====================

    public Result Join(string id, int accountId, string name, string? password, bool isFriendOfHost)
    {
        Room? room;
        Room? previous = null;
        lock (_gate)
        {
            room = _rooms.GetValueOrDefault(id.Trim().ToUpperInvariant());
            if (room == null) return new(false, 404, "房间不存在或已关闭");
            if (room.Members.TryGetValue(accountId, out var existing))
            {
                existing.SeenAt = DateTime.UtcNow;
                return new(true, 200, "已在房间中", room);
            }
            if (room.Kicked.Contains(accountId)) return new(false, 403, "你已被房主移出这个房间");
            if (room.Visibility == "friends" && !isFriendOfHost) return new(false, 403, "这是仅好友可进的房间");
            if (room.PasswordHash != null && !CheckPassword(room, password)) return new(false, 403, "房间密码错误");
            if (room.Members.Count >= room.MaxPlayers) return new(false, 409, "房间已满");
            if (_rooms.Values.FirstOrDefault(r => r.HostId == accountId) is { } hosted)
                return new(false, 409, $"你正在当房主（房间 {hosted.Id}），请先关闭自己的房间");
            previous = _rooms.Values.FirstOrDefault(r => r.Members.ContainsKey(accountId));
            previous?.Members.Remove(accountId);
            var now = DateTime.UtcNow;
            room.Members[accountId] = new Member { Id = accountId, Name = name, Role = "member", JoinedAt = now, SeenAt = now };
        }
        if (previous != null) _ = NotifyChangedAsync(previous, accountId);
        _ = NotifyChangedAsync(room);
        return new(true, 200, "已加入房间", room);
    }

    public Result Leave(string id, int accountId)
    {
        Room? room;
        lock (_gate)
        {
            room = _rooms.GetValueOrDefault(id);
            if (room == null || !room.Members.ContainsKey(accountId)) return new(false, 404, "你不在这个房间里");
        }
        if (room.HostId == accountId) return Close(id, accountId, "房主关闭了房间");
        lock (_gate) room.Members.Remove(accountId);
        _ = NotifyChangedAsync(room, accountId);
        return new(true, 200, "已离开房间", room);
    }

    /// <summary>心跳：刷新在线时间。返回房间（用于换发票据），不在房间里返回 404。</summary>
    public Result Heartbeat(string id, int accountId)
    {
        lock (_gate)
        {
            var room = _rooms.GetValueOrDefault(id);
            if (room == null) return new(false, 404, "房间不存在或已关闭");
            if (!room.Members.TryGetValue(accountId, out var member)) return new(false, 404, "你不在这个房间里");
            member.SeenAt = DateTime.UtcNow;
            return new(true, 200, "ok", room);
        }
    }

    public bool IsMember(string roomId, int accountId, out string role)
    {
        lock (_gate)
        {
            role = "";
            if (!_rooms.TryGetValue(roomId, out var room) || !room.Members.TryGetValue(accountId, out var member)) return false;
            role = member.Role;
            return true;
        }
    }

    // ==================== 内部 ====================

    private async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        List<Room> closed = [];
        List<(Room Room, int[] Departed)> changed = [];
        lock (_gate)
        {
            foreach (var room in _rooms.Values.ToList())
            {
                if (room.Members.TryGetValue(room.HostId, out var host) && now - host.SeenAt > HostTimeout)
                {
                    _rooms.Remove(room.Id);
                    closed.Add(room);
                    continue;
                }
                var stale = room.Members.Values.Where(m => m.Role != "host" && now - m.SeenAt > MemberTimeout).Select(m => m.Id).ToList();
                foreach (var memberId in stale) room.Members.Remove(memberId);
                if (stale.Count > 0) changed.Add((room, stale.ToArray()));
            }
        }
        foreach (var room in closed)
        {
            Console.WriteLine($"启动器房间 {room.Id} 房主心跳超时，已关闭");
            await NotifyClosedAsync(room, "房主已离线");
        }
        foreach (var (room, departed) in changed) await NotifyChangedAsync(room, departed);
    }

    private async Task NotifyChangedAsync(Room room, params int[] departed)
    {
        int[] members;
        lock (_gate) members = room.Members.Keys.ToArray();
        foreach (var id in members)
            await _hub.SendAsync(id, new JsonObject { ["type"] = "room_updated", ["room"] = ToJson(room, id) });
        await RaisePresenceAsync(members.Concat(departed).ToArray());
    }

    private async Task NotifyClosedAsync(Room room, string reason)
    {
        int[] members;
        lock (_gate) members = room.Members.Keys.ToArray();
        foreach (var id in members)
            await _hub.SendAsync(id, new JsonObject { ["type"] = "room_closed", ["roomId"] = room.Id, ["reason"] = reason });
        await RaisePresenceAsync(members);
    }

    private async Task RaisePresenceAsync(int[] ids)
    {
        try { if (PresenceChanged != null) await PresenceChanged(ids); }
        catch (Exception ex) { Console.WriteLine($"推送房间在线状态失败：{ex.GetBaseException().Message}"); }
    }

    private sealed record Fields(string? Name, string? Description, string? Visibility, string? Password, bool ClearPassword,
        int? MaxPlayers, JsonArray? Mods, string? ModpackId, bool ClearModpack, string? GameVersion);

    private static (Fields Fields, string? Error) ReadFields(JsonObject body, Room? existing)
    {
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var name = Str(body["name"])?.Trim();
        if (existing == null && string.IsNullOrEmpty(name)) return (null!, "请填写房间名");
        if (name != null && (name.Length is 0 or > 40 || name.Any(char.IsControl))) return (null!, "房间名需要 1 到 40 个字");
        var description = Str(body["description"])?.Trim();
        if (description is { Length: > 200 }) return (null!, "房间简介最多 200 个字");
        var visibility = Str(body["visibility"])?.Trim().ToLowerInvariant();
        if (visibility != null && !Visibilities.Contains(visibility)) return (null!, "visibility 只能是 public、password 或 friends");
        var password = Str(body["password"]);
        if (password is { Length: > 64 }) return (null!, "房间密码最多 64 个字符");
        int? maxPlayers = null;
        if (body["maxPlayers"] is JsonValue mp)
        {
            if (!mp.TryGetValue<int>(out var max) || max is < 2 or > MaxRoomSize) return (null!, $"人数上限需要在 2 到 {MaxRoomSize} 之间");
            maxPlayers = max;
        }
        JsonArray? mods = null;
        if (body["mods"] is JsonArray list)
        {
            if (list.Count > 200) return (null!, "房间模组清单最多 200 项");
            if (list.ToJsonString().Length > 64 * 1024) return (null!, "房间模组清单太大");
            if (list.Any(x => x is not JsonObject)) return (null!, "mods 的每一项都必须是对象");
            mods = (JsonArray)list.DeepClone();
        }
        var modpackId = Str(body["modpackId"])?.Trim();
        if (modpackId is { Length: > 64 }) return (null!, "modpackId 太长");
        var gameVersion = Str(body["gameVersion"])?.Trim();
        if (gameVersion is { Length: > 64 }) return (null!, "gameVersion 太长");
        return (new Fields(name, description, visibility, string.IsNullOrEmpty(password) ? null : password,
            body["password"] is JsonValue pv && pv.TryGetValue<string>(out var p) && p.Length == 0,
            maxPlayers, mods, string.IsNullOrEmpty(modpackId) ? null : modpackId,
            body.ContainsKey("modpackId") && string.IsNullOrEmpty(modpackId), gameVersion), null);
    }

    private static void Apply(Room room, Fields f)
    {
        if (f.Name != null) room.Name = f.Name;
        if (f.Description != null) room.Description = f.Description;
        if (f.Visibility != null) room.Visibility = f.Visibility;
        if (f.MaxPlayers != null) room.MaxPlayers = f.MaxPlayers.Value;
        if (f.Mods != null) room.Mods = f.Mods;
        if (f.ModpackId != null) room.ModpackId = f.ModpackId;
        else if (f.ClearModpack) room.ModpackId = null;
        if (f.GameVersion != null) room.GameVersion = f.GameVersion;
        if (f.Password != null)
        {
            room.PasswordSalt = RandomNumberGenerator.GetBytes(16);
            room.PasswordHash = Hash(room.PasswordSalt, f.Password);
        }
        else if (f.ClearPassword)
        {
            room.PasswordSalt = null;
            room.PasswordHash = null;
        }
        // 只有密码房才校验密码；改成其他可见性时清掉旧密码。
        if (room.Visibility != "password")
        {
            room.PasswordSalt = null;
            room.PasswordHash = null;
        }
    }

    private static byte[] Hash(byte[] salt, string password) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 10_000, HashAlgorithmName.SHA256, 32);

    private static bool CheckPassword(Room room, string? password) =>
        password != null && room.PasswordSalt != null &&
        CryptographicOperations.FixedTimeEquals(Hash(room.PasswordSalt, password), room.PasswordHash);

    private string NewCode()
    {
        while (true)
        {
            var code = new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray());
            if (!_rooms.ContainsKey(code)) return code;
        }
    }

    public void Dispose() => _sweeper.Dispose();
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace fyserver.Services;

/// <summary>
/// 模组索引：保存模组 manifest（名称、简介、内部 id、图标、Pak 直链）并校验直链。
/// 服务端不托管 Pak 文件，只保存上传者提供的下载直链；每个模组一个目录：
/// ./data/mods/&lt;id&gt;/manifest.json 与 icon.&lt;ext&gt;。全部使用 JsonNode，NativeAOT 安全。
/// </summary>
public sealed partial class ModIndexService
{
    private const string Root = "./data/mods";
    private const int MaxIconBytes = 512 * 1024;
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 2000;
    private const uint PakMagic = 0x5A6F12E1;
    private static readonly string[] AllowedIconTypes = ["image/png", "image/jpeg", "image/webp"];

    private readonly object _gate = new();
    private readonly HttpClient _http;

    public ModIndexService()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            // 在建立连接时检查解析后的地址，阻止访问内网/回环地址（SSRF），重定向同样会经过这里。
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var safe = addresses.Where(a => !IsPrivate(a)).ToArray();
                if (safe.Length == 0) throw new HttpRequestException("下载链接指向内网或保留地址，已被拒绝");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(safe, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FYServer-ModIndex/1.0");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{2,63}$")]
    private static partial Regex ModIdRegex();

    public static bool IsValidId(string? id) => id != null && ModIdRegex().IsMatch(id);

    // ------------------------------------------------------------------ 读取
    public List<JsonObject> List(bool includeHidden)
    {
        lock (_gate)
        {
            var result = new List<JsonObject>();
            if (!Directory.Exists(Root)) return result;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var manifest = ReadManifestLocked(Path.GetFileName(dir));
                if (manifest == null) continue;
                if (!includeHidden && manifest["hidden"]?.GetValue<bool>() == true) continue;
                result.Add(manifest);
            }
            return result.OrderByDescending(m => m["updatedAt"]?.GetValue<string>() ?? "").ToList();
        }
    }

    public JsonObject? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate) return ReadManifestLocked(id);
    }

    public (byte[] Data, string ContentType)? GetIcon(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate)
        {
            var manifest = ReadManifestLocked(id);
            var file = manifest?["iconFile"]?.GetValue<string>();
            if (file == null) return null;
            var path = Path.Combine(Root, id, file);
            if (!File.Exists(path)) return null;
            return (File.ReadAllBytes(path), manifest!["iconContentType"]?.GetValue<string>() ?? "image/png");
        }
    }

    public void RecordDownload(string id)
    {
        lock (_gate)
        {
            var manifest = ReadManifestLocked(id);
            if (manifest == null) return;
            manifest["downloads"] = (manifest["downloads"]?.GetValue<long>() ?? 0) + 1;
            WriteManifestLocked(id, manifest);
        }
    }

    // ------------------------------------------------------------------ 写入
    /// <summary>创建或更新模组。updater 为 null 表示管理员操作；非 null 时只能修改自己上传的模组。</summary>
    public async Task<(bool Ok, int Status, string Message, JsonObject? Manifest)> UpsertAsync(
        int uploaderId, string uploaderName, JsonObject body, CancellationToken token)
    {
        var id = body["id"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var name = body["name"]?.GetValue<string>()?.Trim();
        var description = body["description"]?.GetValue<string>()?.Trim() ?? "";
        var url = body["downloadUrl"]?.GetValue<string>()?.Trim();
        var iconBase64 = body["icon"]?.GetValue<string>()?.Trim();

        if (!IsValidId(id)) return (false, 400, "模组 id 只能包含小写字母、数字、下划线、点和连字符，长度 3-64", null);
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return (false, 400, $"模组名称不能为空且不超过 {MaxNameLength} 个字符", null);
        if (description.Length > MaxDescriptionLength) return (false, 400, $"模组简介不能超过 {MaxDescriptionLength} 个字符", null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, 400, "下载直链必须是 http 或 https 地址", null);

        JsonObject? existing;
        lock (_gate) existing = ReadManifestLocked(id!);
        if (existing != null && existing["uploaderId"]?.GetValue<int>() != uploaderId)
            return (false, 403, "该模组 id 已被其他用户占用", null);

        byte[]? iconBytes = null;
        string? iconType = null;
        if (!string.IsNullOrEmpty(iconBase64))
        {
            var parsed = ParseIcon(iconBase64);
            if (parsed.Error != null) return (false, 400, parsed.Error, null);
            iconBytes = parsed.Data;
            iconType = parsed.ContentType;
        }
        else if (existing == null)
        {
            return (false, 400, "新模组必须提供图标（icon，base64 或 data URL）", null);
        }

        var check = await CheckPakUrlAsync(uri!, token);
        if (!check.Ok) return (false, 422, "下载链接检查未通过：" + check.Message, null);

        lock (_gate)
        {
            // 加锁后再次确认归属，避免并发占用同一 id。
            existing = ReadManifestLocked(id!);
            if (existing != null && existing["uploaderId"]?.GetValue<int>() != uploaderId)
                return (false, 403, "该模组 id 已被其他用户占用", null);

            var now = DateTime.UtcNow.ToString("O");
            var manifest = existing ?? new JsonObject
            {
                ["id"] = id,
                ["uploaderId"] = uploaderId,
                ["createdAt"] = now,
                ["downloads"] = 0,
                ["hidden"] = false
            };
            manifest["uploaderName"] = uploaderName;
            manifest["name"] = name;
            manifest["description"] = description;
            manifest["downloadUrl"] = uri!.ToString();
            manifest["fileSize"] = check.Size;
            manifest["pakVersion"] = check.PakVersion;
            manifest["linkCheckedAt"] = now;
            manifest["linkValid"] = true;
            manifest["updatedAt"] = now;

            Directory.CreateDirectory(Path.Combine(Root, id!));
            if (iconBytes != null)
            {
                var ext = iconType == "image/jpeg" ? "jpg" : iconType == "image/webp" ? "webp" : "png";
                var fileName = "icon." + ext;
                foreach (var old in Directory.GetFiles(Path.Combine(Root, id!), "icon.*")) File.Delete(old);
                File.WriteAllBytes(Path.Combine(Root, id!, fileName), iconBytes);
                manifest["iconFile"] = fileName;
                manifest["iconContentType"] = iconType;
            }
            WriteManifestLocked(id!, manifest);
            return (true, existing == null ? 201 : 200, existing == null ? "模组已上传" : "模组已更新", manifest);
        }
    }

    public (bool Ok, string Message) Delete(string id, int? requesterId)
    {
        if (!IsValidId(id)) return (false, "模组不存在");
        lock (_gate)
        {
            var manifest = ReadManifestLocked(id);
            if (manifest == null) return (false, "模组不存在");
            if (requesterId != null && manifest["uploaderId"]?.GetValue<int>() != requesterId)
                return (false, "只能删除自己上传的模组");
            Directory.Delete(Path.Combine(Root, id), true);
            return (true, "模组已删除");
        }
    }

    public (bool Ok, string Message) SetHidden(string id, bool hidden)
    {
        lock (_gate)
        {
            var manifest = id != null && IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, "模组不存在");
            manifest["hidden"] = hidden;
            manifest["updatedAt"] = DateTime.UtcNow.ToString("O");
            WriteManifestLocked(id!, manifest);
            return (true, hidden ? "模组已隐藏" : "模组已显示");
        }
    }

    /// <summary>重新检查已登记模组的下载直链，并写回检查结果。</summary>
    public async Task<(bool Ok, string Message, JsonObject? Manifest)> RecheckAsync(string id, CancellationToken token)
    {
        var manifest = Get(id);
        if (manifest == null) return (false, "模组不存在", null);
        var check = Uri.TryCreate(manifest["downloadUrl"]?.GetValue<string>(), UriKind.Absolute, out var uri)
            ? await CheckPakUrlAsync(uri, token)
            : (Ok: false, Message: "下载链接无效", Size: 0L, PakVersion: 0);
        lock (_gate)
        {
            manifest = ReadManifestLocked(id);
            if (manifest == null) return (false, "模组不存在", null);
            manifest["linkValid"] = check.Ok;
            manifest["linkCheckedAt"] = DateTime.UtcNow.ToString("O");
            manifest["linkMessage"] = check.Ok ? "" : check.Message;
            if (check.Ok) { manifest["fileSize"] = check.Size; manifest["pakVersion"] = check.PakVersion; }
            WriteManifestLocked(id, manifest);
            return (check.Ok, check.Ok ? "下载链接有效且文件为 Pak" : "下载链接检查未通过：" + check.Message, manifest);
        }
    }

    // ------------------------------------------------------------------ 链接与 Pak 检查
    /// <summary>
    /// 只用 Range 请求读取文件末尾，不下载整个文件：确认链接可访问、服务器支持 Range，
    /// 且文件尾部带有 Unreal Pak 页脚魔数（0x5A6F12E1）。zip 等压缩包不会通过。
    /// </summary>
    public async Task<(bool Ok, string Message, long Size, int PakVersion)> CheckPakUrlAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 1024);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return (false, "文件为空或太小，不是有效的 Pak", 0, 0);
            if (!response.IsSuccessStatusCode)
                return (false, $"链接无法访问（HTTP {(int)response.StatusCode}）", 0, 0);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                return (false, "下载服务器不支持 Range 请求，无法在不下载整个文件的情况下检查 Pak", 0, 0);
            var total = response.Content.Headers.ContentRange?.Length;
            if (total is null or < 44) return (false, "无法确定文件大小，或文件太小不是有效的 Pak", 0, 0);

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var tail = new byte[1024];
            var read = 0;
            while (read < tail.Length)
            {
                var n = await stream.ReadAsync(tail.AsMemory(read), token);
                if (n == 0) break;
                read += n;
            }
            if (read < 44) return (false, "文件太小，不是有效的 Pak", 0, 0);

            // 页脚内 Magic 后紧跟 4 字节版本号；从尾部向前查找，取最靠后的有效魔数。
            for (var i = read - 8; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(tail, i) != PakMagic) continue;
                var version = BitConverter.ToInt32(tail, i + 4);
                if (version is < 1 or > 32) continue;
                return (true, "ok", total.Value, version);
            }
            return (false, "文件不是纯 Pak（未找到 Pak 页脚魔数）。压缩包或其他类型的文件不被接受", total.Value, 0);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, "访问下载链接超时", 0, 0);
        }
        catch (HttpRequestException ex)
        {
            return (false, "无法访问下载链接：" + ex.Message, 0, 0);
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                   (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 唯一本地地址
        }
        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) ||
               (b[0] == 100 && b[1] is >= 64 and <= 127) ||
               b[0] == 0 || b[0] >= 224;
    }

    // ------------------------------------------------------------------ 图标与存储
    private static (byte[]? Data, string? ContentType, string? Error) ParseIcon(string input)
    {
        var text = input;
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma < 0) return (null, null, "图标 data URL 格式错误");
            text = text[(comma + 1)..];
        }
        byte[] data;
        try { data = Convert.FromBase64String(text); }
        catch (FormatException) { return (null, null, "图标不是有效的 base64"); }
        if (data.Length == 0 || data.Length > MaxIconBytes) return (null, null, $"图标大小必须在 1 字节到 {MaxIconBytes / 1024} KB 之间");
        string? type = null;
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) type = "image/png";
        else if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) type = "image/jpeg";
        else if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                 data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') type = "image/webp";
        if (type == null || !AllowedIconTypes.Contains(type)) return (null, null, "图标必须是 PNG、JPEG 或 WebP 图片");
        return (data, type, null);
    }

    private static JsonObject? ReadManifestLocked(string id)
    {
        var path = Path.Combine(Root, id, "manifest.json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static void WriteManifestLocked(string id, JsonObject manifest)
    {
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, manifest.ToJsonString());
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>公开给游戏侧的 manifest（去掉内部字段）。withIcon 时附带 base64 图标。</summary>
    public (JsonObject Json, bool Found) ToPublic(string id, bool withIcon, string iconUrl)
    {
        var manifest = Get(id);
        if (manifest == null) return (new JsonObject(), false);
        var json = new JsonObject
        {
            ["id"] = manifest["id"]?.DeepClone(),
            ["name"] = manifest["name"]?.DeepClone(),
            ["description"] = manifest["description"]?.DeepClone(),
            ["downloadUrl"] = manifest["downloadUrl"]?.DeepClone(),
            ["fileSize"] = manifest["fileSize"]?.DeepClone(),
            ["uploaderName"] = manifest["uploaderName"]?.DeepClone(),
            ["downloads"] = manifest["downloads"]?.DeepClone(),
            ["createdAt"] = manifest["createdAt"]?.DeepClone(),
            ["updatedAt"] = manifest["updatedAt"]?.DeepClone(),
            ["iconUrl"] = iconUrl,
            ["iconContentType"] = manifest["iconContentType"]?.DeepClone()
        };
        if (withIcon && GetIcon(id) is { } icon) json["icon"] = Convert.ToBase64String(icon.Data);
        return (json, true);
    }
}

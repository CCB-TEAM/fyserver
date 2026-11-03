using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    /// <summary>服务端下载校验 sha256 的最大文件大小；超过的 Pak 拒绝登记。</summary>
    public const long MaxPakBytes = 1024L * 1024 * 1024;
    private static readonly string[] AllowedIconTypes = ["image/png", "image/jpeg", "image/webp"];

    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly HttpClient _hashHttp;
    // 同一时间只校验一个 Pak，避免占满服务器下行带宽。
    private readonly SemaphoreSlim _hashGate = new(1, 1);

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
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FYServer-ModIndex/1.0");
        // 完整下载 Pak 计算 sha256 用的客户端：超时放宽到 2 小时（1 GB 在 30 Mbps 下约 5 分钟）。
        _hashHttp = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromHours(2) };
        _hashHttp.DefaultRequestHeaders.UserAgent.ParseAdd("FYServer-ModIndex/1.0");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{2,63}$")]
    private static partial Regex ModIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

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
                if (!includeHidden && !IsPublic(manifest)) continue;
                result.Add(manifest);
            }
            return result.OrderByDescending(m => m["updatedAt"]?.GetValue<string>() ?? "").ToList();
        }
    }

    /// <summary>未被管理员隐藏、且 sha256 校验未发现不一致的模组才对玩家可见。</summary>
    public static bool IsPublic(JsonObject manifest) =>
        manifest["hidden"]?.GetValue<bool>() != true && manifest["hashStatus"]?.GetValue<string>() != "mismatch";

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
        var sha256 = body["sha256"]?.GetValue<string>()?.Trim().ToLowerInvariant();

        if (!IsValidId(id)) return (false, 400, "模组 id 只能包含小写字母、数字、下划线、点和连字符，长度 3-64", null);
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return (false, 400, $"模组名称不能为空且不超过 {MaxNameLength} 个字符", null);
        if (description.Length > MaxDescriptionLength) return (false, 400, $"模组简介不能超过 {MaxDescriptionLength} 个字符", null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, 400, "下载直链必须是 http 或 https 地址", null);
        if (sha256 == null || !Sha256Regex().IsMatch(sha256))
            return (false, 400, "sha256 必须是 64 位十六进制字符串（Pak 文件的 SHA-256）", null);

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

        var check = await CheckLinkAsync(uri!, token);
        if (!check.Ok) return (false, 422, "下载链接检查未通过：" + check.Message, null);
        if (check.Size > MaxPakBytes)
            return (false, 422, $"Pak 文件超过 {MaxPakBytes / 1024 / 1024} MB 上限", null);

        lock (_gate)
        {
            // 加锁后再次确认归属，避免并发占用同一 id。
            existing = ReadManifestLocked(id!);
            if (existing != null && existing["uploaderId"]?.GetValue<int>() != uploaderId)
                return (false, 403, "该模组 id 已被其他用户占用", null);

            var now = DateTime.UtcNow.ToString("O");
            var previousUrl = existing?["downloadUrl"]?.GetValue<string>();
            var previousSha = existing?["sha256"]?.GetValue<string>();
            var previousHashStatus = existing?["hashStatus"]?.GetValue<string>();
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
            manifest.Remove("pakVersion");
            manifest["linkCheckedAt"] = now;
            manifest["linkValid"] = true;
            manifest["linkMessage"] = "";
            manifest["updatedAt"] = now;
            var hashChanged = previousSha != sha256 || previousHashStatus != "verified" || previousUrl != uri!.ToString();
            manifest["sha256"] = sha256;
            if (hashChanged)
            {
                manifest["hashStatus"] = "pending";
                manifest["hashMessage"] = "";
            }

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
            if (hashChanged) StartHashVerification(id!);
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
            ? await CheckLinkAsync(uri, token)
            : (Ok: false, Message: "下载链接无效", Size: 0L);
        lock (_gate)
        {
            manifest = ReadManifestLocked(id);
            if (manifest == null) return (false, "模组不存在", null);
            manifest["linkValid"] = check.Ok;
            manifest["linkCheckedAt"] = DateTime.UtcNow.ToString("O");
            manifest["linkMessage"] = check.Ok ? "" : check.Message;
            if (check.Ok) manifest["fileSize"] = check.Size;
            if (check.Ok)
            {
                manifest["hashStatus"] = "pending";
                manifest["hashMessage"] = "";
            }
            WriteManifestLocked(id, manifest);
            if (check.Ok) StartHashVerification(id);
            return (check.Ok, check.Ok ? "下载链接有效，已开始重新校验 sha256" : "下载链接检查未通过：" + check.Message, manifest);
        }
    }

    // ------------------------------------------------------------------ sha256 校验
    /// <summary>在后台完整下载 Pak 并计算 sha256，与上传者声明的值比对，结果写回 manifest。</summary>
    private void StartHashVerification(string id) => _ = Task.Run(() => VerifyHashAsync(id));

    private async Task VerifyHashAsync(string id)
    {
        await _hashGate.WaitAsync();
        try
        {
            var manifest = Get(id);
            var url = manifest?["downloadUrl"]?.GetValue<string>();
            var expected = manifest?["sha256"]?.GetValue<string>();
            if (manifest == null || url == null || expected == null) return;

            string status, message;
            try
            {
                using var response = await _hashHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    total += n;
                    if (total > MaxPakBytes) throw new InvalidDataException($"文件超过 {MaxPakBytes / 1024 / 1024} MB 上限");
                    hash.AppendData(buffer, 0, n);
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                (status, message) = actual == expected
                    ? ("verified", "")
                    : ("mismatch", "实际 sha256 为 " + actual + "，与上传者提供的不一致");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                (status, message) = ("failed", "下载校验失败：" + ex.Message);
            }

            lock (_gate)
            {
                var current = ReadManifestLocked(id);
                // 校验期间模组被删除或改了链接/哈希，丢弃本次结果（新的校验已排队）。
                if (current == null || current["downloadUrl"]?.GetValue<string>() != url ||
                    current["sha256"]?.GetValue<string>() != expected) return;
                current["hashStatus"] = status;
                current["hashMessage"] = message;
                current["hashCheckedAt"] = DateTime.UtcNow.ToString("O");
                WriteManifestLocked(id, current);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"模组 {id} sha256 校验异常：{ex.Message}");
        }
        finally
        {
            _hashGate.Release();
        }
    }

    /// <summary>启动时为仍处于 pending 的模组重新排队校验（例如上次校验时服务器重启）。</summary>
    public void ResumePendingVerifications()
    {
        foreach (var manifest in List(includeHidden: true))
            if (manifest["hashStatus"]?.GetValue<string>() is null or "pending")
                StartHashVerification(manifest["id"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ 链接检查
    /// <summary>
    /// 只检查下载链接可访问：发送 GET 并只读响应头，不下载文件内容，也不检查文件格式
    /// （Pak 合法性由客户端负责）。能拿到大小时一并返回（Content-Range 或 Content-Length）。
    /// </summary>
    public async Task<(bool Ok, string Message, long Size)> CheckLinkAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // 请求第一个字节：支持 Range 的服务器只回 1 字节，并在 Content-Range 中给出总大小。
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return (false, "文件为空", 0);
            if (!response.IsSuccessStatusCode)
                return (false, $"链接无法访问（HTTP {(int)response.StatusCode}）", 0);
            var size = response.StatusCode == HttpStatusCode.PartialContent
                ? response.Content.Headers.ContentRange?.Length ?? 0
                : response.Content.Headers.ContentLength ?? 0;
            return (true, "ok", size);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, "访问下载链接超时", 0);
        }
        catch (HttpRequestException ex)
        {
            return (false, "无法访问下载链接：" + ex.Message, 0);
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
            ["sha256"] = manifest["sha256"]?.DeepClone(),
            ["hashStatus"] = manifest["hashStatus"]?.DeepClone(),
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

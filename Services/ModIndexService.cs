using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace fyserver.Services;

/// <summary>
/// 模组索引（审核制）。服务端不托管 Pak，也不检查文件合法性：上传者提交名称、简介、图标、
/// Pak 下载直链和 sha256，管理员在后台人工审核。
///
/// 每个模组一个目录 ./data/mods/&lt;id&gt;/：
///   manifest.json — { id, ownerId, ownerName, createdAt, downloads, hidden, published, submission }
///   icon.&lt;ext&gt; — 已发布版本的图标；pending-icon.&lt;ext&gt; — 待审核提交的图标
/// published 为当前对玩家可见的版本（首次通过前为 null）；submission 为最近一次提交，
/// status 为 pending 或 rejected。驳回只作废这次提交，id 仍归原上传者；彻底删除会释放 id。
/// 全部使用 JsonNode，NativeAOT 安全。
/// </summary>
public sealed partial class ModIndexService
{
    private const string Root = "./data/mods";
    private const int MaxIconBytes = 512 * 1024;
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 2000;
    private const int MaxVersionLength = 32;
    private const int MaxUrlLength = 2048;
    private const int MaxRejectReasonLength = 500;

    private readonly object _gate = new();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{2,63}$")]
    private static partial Regex ModIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    public static bool IsValidId(string? id) => id != null && ModIdRegex().IsMatch(id);

    // ------------------------------------------------------------------ 读取
    public List<JsonObject> List()
    {
        lock (_gate)
        {
            var result = new List<JsonObject>();
            if (!Directory.Exists(Root)) return result;
            foreach (var dir in Directory.GetDirectories(Root))
                if (ReadManifestLocked(Path.GetFileName(dir)) is { } manifest) result.Add(manifest);
            return result;
        }
    }

    /// <summary>对玩家可见：已有通过审核的版本，且未被管理员隐藏。按发布时间从新到旧。</summary>
    public List<JsonObject> ListPublic() => List()
        .Where(IsPublic)
        .OrderByDescending(m => Str(m["published"]?["approvedAt"]) ?? "")
        .ToList();

    public static bool IsPublic(JsonObject manifest) =>
        manifest["published"] is JsonObject && !Bool(manifest["hidden"]);

    public JsonObject? Get(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate) return ReadManifestLocked(id);
    }

    public List<JsonObject> ListOwnedBy(int ownerId) => List()
        .Where(m => Int(m["ownerId"]) == ownerId)
        .OrderByDescending(m => Str(m["submission"]?["submittedAt"]) ?? Str(m["published"]?["approvedAt"]) ?? "")
        .ToList();

    /// <summary>读取图标：pending=true 时读待审核提交的图标（仅后台使用）。</summary>
    public (byte[] Data, string ContentType)? GetIcon(string id, bool pending)
    {
        if (!IsValidId(id)) return null;
        lock (_gate)
        {
            var manifest = ReadManifestLocked(id);
            var version = manifest?[pending ? "submission" : "published"] as JsonObject;
            var file = Str(version?["iconFile"]);
            if (file == null) return null;
            var path = Path.Combine(Root, id, file);
            if (!File.Exists(path)) return null;
            return (File.ReadAllBytes(path), Str(version!["iconContentType"]) ?? "image/png");
        }
    }

    public void RecordDownload(string id)
    {
        lock (_gate)
        {
            var manifest = ReadManifestLocked(id);
            if (manifest == null) return;
            manifest["downloads"] = (Long(manifest["downloads"]) ?? 0) + 1;
            WriteManifestLocked(id, manifest);
        }
    }

    // ------------------------------------------------------------------ 上传者提交
    /// <summary>
    /// 提交新模组或更新。新 id 会被该账号占用；已有 id 只有原上传者能提交更新。
    /// 提交进入待审核，覆盖之前未处理或被驳回的提交，已发布版本在审核通过前保持不变。
    /// </summary>
    public (bool Ok, int Status, string Message, JsonObject? Manifest) Submit(int ownerId, string ownerName, JsonObject body)
    {
        var id = Str(body["id"])?.Trim().ToLowerInvariant();
        var name = Str(body["name"])?.Trim();
        var description = Str(body["description"])?.Trim() ?? "";
        var version = Str(body["version"])?.Trim() ?? "";
        var url = Str(body["downloadUrl"])?.Trim();
        var sha256 = Str(body["sha256"])?.Trim().ToLowerInvariant();
        var iconBase64 = Str(body["icon"])?.Trim();
        var fileSize = Long(body["fileSize"]);

        if (!IsValidId(id)) return (false, 400, "模组 id 只能包含小写字母、数字、下划线、点和连字符，长度 3-64，且以字母或数字开头", null);
        if (id is "mine") return (false, 400, "该 id 为保留字，请换一个", null);
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return (false, 400, $"模组名称不能为空且不超过 {MaxNameLength} 个字符", null);
        if (description.Length > MaxDescriptionLength) return (false, 400, $"模组简介不能超过 {MaxDescriptionLength} 个字符", null);
        if (version.Length > MaxVersionLength) return (false, 400, $"版本号不能超过 {MaxVersionLength} 个字符", null);
        if (url == null || url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, 400, "下载直链必须是 http 或 https 地址", null);
        if (sha256 == null || !Sha256Regex().IsMatch(sha256))
            return (false, 400, "sha256 必须是 64 位十六进制字符串（Pak 文件的 SHA-256）", null);
        if (fileSize is < 0) return (false, 400, "fileSize 不能为负数", null);

        byte[]? iconBytes = null;
        string? iconType = null;
        if (!string.IsNullOrEmpty(iconBase64))
        {
            var parsed = ParseIcon(iconBase64);
            if (parsed.Error != null) return (false, 400, parsed.Error, null);
            iconBytes = parsed.Data;
            iconType = parsed.ContentType;
        }

        lock (_gate)
        {
            var manifest = ReadManifestLocked(id!);
            if (manifest != null && Int(manifest["ownerId"]) != ownerId)
                return (false, 403, "该模组 id 已被其他用户占用", null);
            var published = manifest?["published"] as JsonObject;
            if (iconBytes == null && published == null)
                return (false, 400, "首次提交必须提供图标（icon，base64 或 data URL）", null);

            var now = Now();
            var isNew = manifest == null;
            manifest ??= new JsonObject
            {
                ["id"] = id,
                ["ownerId"] = ownerId,
                ["createdAt"] = now,
                ["downloads"] = 0,
                ["hidden"] = false,
                ["published"] = null
            };
            manifest["ownerName"] = ownerName;

            var dir = Path.Combine(Root, id!);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "pending-icon.*")) File.Delete(old);
            string? iconFile = null;
            if (iconBytes != null)
            {
                iconFile = "pending-icon." + Extension(iconType!);
                File.WriteAllBytes(Path.Combine(dir, iconFile), iconBytes);
            }

            manifest["submission"] = new JsonObject
            {
                ["status"] = "pending",
                ["name"] = name,
                ["description"] = description,
                ["version"] = version,
                ["downloadUrl"] = uri!.ToString(),
                ["sha256"] = sha256,
                ["fileSize"] = fileSize,
                // 未提供新图标时沿用已发布版本的图标，审核通过时不替换。
                ["iconFile"] = iconFile,
                ["iconContentType"] = iconType,
                ["submittedAt"] = now
            };
            WriteManifestLocked(id!, manifest);
            return (true, 202, isNew ? "模组已提交，等待管理员审核" : "更新已提交，等待管理员审核；审核通过前玩家仍下载当前版本", manifest);
        }
    }

    /// <summary>上传者删除自己的模组：与后台彻底删除相同，id 随之释放。</summary>
    public (bool Ok, int Status, string Message) DeleteOwned(string id, int ownerId)
    {
        lock (_gate)
        {
            var manifest = IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, 404, "模组不存在");
            if (Int(manifest["ownerId"]) != ownerId) return (false, 403, "只能删除自己上传的模组");
            Directory.Delete(Path.Combine(Root, id), true);
            return (true, 200, "模组已删除，id 已释放");
        }
    }

    // ------------------------------------------------------------------ 后台审核
    public (bool Ok, string Message) Approve(string id, string reviewer)
    {
        lock (_gate)
        {
            var manifest = IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, "模组不存在");
            if (manifest["submission"] is not JsonObject submission || Str(submission["status"]) != "pending")
                return (false, "该模组没有待审核的提交");

            var dir = Path.Combine(Root, id);
            var previous = manifest["published"] as JsonObject;
            var iconFile = Str(previous?["iconFile"]);
            var iconType = Str(previous?["iconContentType"]);
            if (Str(submission["iconFile"]) is { } pendingIcon)
            {
                foreach (var old in Directory.GetFiles(dir, "icon.*")) File.Delete(old);
                iconType = Str(submission["iconContentType"]) ?? "image/png";
                iconFile = "icon." + Extension(iconType);
                File.Move(Path.Combine(dir, pendingIcon), Path.Combine(dir, iconFile), true);
            }

            var now = Now();
            manifest["published"] = new JsonObject
            {
                ["name"] = submission["name"]?.DeepClone(),
                ["description"] = submission["description"]?.DeepClone(),
                ["version"] = submission["version"]?.DeepClone(),
                ["downloadUrl"] = submission["downloadUrl"]?.DeepClone(),
                ["sha256"] = submission["sha256"]?.DeepClone(),
                ["fileSize"] = submission["fileSize"]?.DeepClone(),
                ["iconFile"] = iconFile,
                ["iconContentType"] = iconType,
                ["submittedAt"] = submission["submittedAt"]?.DeepClone(),
                ["approvedAt"] = now,
                ["approvedBy"] = reviewer
            };
            manifest["submission"] = null;
            WriteManifestLocked(id, manifest);
            return (true, previous == null ? "已通过，模组已上架" : "已通过，模组已更新为新版本");
        }
    }

    /// <summary>驳回：只作废这次提交，已发布版本不受影响，id 仍归原上传者。</summary>
    public (bool Ok, string Message) Reject(string id, string reviewer, string? reason)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length > MaxRejectReasonLength) return (false, $"驳回理由不能超过 {MaxRejectReasonLength} 个字符");
        lock (_gate)
        {
            var manifest = IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, "模组不存在");
            if (manifest["submission"] is not JsonObject submission || Str(submission["status"]) != "pending")
                return (false, "该模组没有待审核的提交");
            submission["status"] = "rejected";
            submission["reviewedAt"] = Now();
            submission["reviewedBy"] = reviewer;
            submission["rejectReason"] = reason;
            // 驳回的提交图标不再需要。
            if (Str(submission["iconFile"]) is { } pendingIcon)
            {
                var path = Path.Combine(Root, id, pendingIcon);
                if (File.Exists(path)) File.Delete(path);
                submission["iconFile"] = null;
            }
            WriteManifestLocked(id, manifest);
            return (true, "已驳回此次提交");
        }
    }

    /// <summary>彻底删除：删除模组的已发布版本与所有提交，id 被释放。</summary>
    public (bool Ok, string Message) Purge(string id)
    {
        lock (_gate)
        {
            var manifest = IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, "模组不存在");
            Directory.Delete(Path.Combine(Root, id), true);
            return (true, "模组已彻底删除，id 已释放");
        }
    }

    public (bool Ok, string Message) SetHidden(string id, bool hidden)
    {
        lock (_gate)
        {
            var manifest = IsValidId(id) ? ReadManifestLocked(id) : null;
            if (manifest == null) return (false, "模组不存在");
            manifest["hidden"] = hidden;
            WriteManifestLocked(id, manifest);
            return (true, hidden ? "模组已隐藏" : "模组已显示");
        }
    }

    // ------------------------------------------------------------------ 输出
    /// <summary>玩家接口用的已发布版本 manifest；withIcon 时附带 base64 图标。</summary>
    public JsonObject? ToPublic(JsonObject manifest, bool withIcon)
    {
        if (manifest["published"] is not JsonObject p) return null;
        var id = Str(manifest["id"])!;
        var json = new JsonObject
        {
            ["id"] = id,
            ["name"] = p["name"]?.DeepClone(),
            ["description"] = p["description"]?.DeepClone(),
            ["version"] = p["version"]?.DeepClone(),
            ["downloadUrl"] = p["downloadUrl"]?.DeepClone(),
            ["sha256"] = p["sha256"]?.DeepClone(),
            ["fileSize"] = p["fileSize"]?.DeepClone(),
            ["uploaderName"] = manifest["ownerName"]?.DeepClone(),
            ["downloads"] = manifest["downloads"]?.DeepClone(),
            ["createdAt"] = manifest["createdAt"]?.DeepClone(),
            ["updatedAt"] = p["approvedAt"]?.DeepClone(),
            ["iconUrl"] = "/mods/" + id + "/icon",
            ["iconContentType"] = p["iconContentType"]?.DeepClone()
        };
        if (withIcon && GetIcon(id, pending: false) is { } icon) json["icon"] = Convert.ToBase64String(icon.Data);
        return json;
    }

    /// <summary>上传者查看自己的模组：已发布版本概要 + 最近一次提交的审核状态。</summary>
    public static JsonObject ToOwnerView(JsonObject manifest)
    {
        var submission = manifest["submission"] as JsonObject;
        var published = manifest["published"] as JsonObject;
        return new JsonObject
        {
            ["id"] = manifest["id"]?.DeepClone(),
            ["hidden"] = manifest["hidden"]?.DeepClone(),
            ["downloads"] = manifest["downloads"]?.DeepClone(),
            ["published"] = published == null ? null : new JsonObject
            {
                ["name"] = published["name"]?.DeepClone(),
                ["version"] = published["version"]?.DeepClone(),
                ["downloadUrl"] = published["downloadUrl"]?.DeepClone(),
                ["sha256"] = published["sha256"]?.DeepClone(),
                ["approvedAt"] = published["approvedAt"]?.DeepClone()
            },
            ["submission"] = submission == null ? null : new JsonObject
            {
                ["status"] = submission["status"]?.DeepClone(),
                ["name"] = submission["name"]?.DeepClone(),
                ["version"] = submission["version"]?.DeepClone(),
                ["downloadUrl"] = submission["downloadUrl"]?.DeepClone(),
                ["sha256"] = submission["sha256"]?.DeepClone(),
                ["submittedAt"] = submission["submittedAt"]?.DeepClone(),
                ["reviewedAt"] = submission["reviewedAt"]?.DeepClone(),
                ["rejectReason"] = submission["rejectReason"]?.DeepClone()
            }
        };
    }

    // ------------------------------------------------------------------ 图标与存储
    private static string Extension(string contentType) =>
        contentType == "image/jpeg" ? "jpg" : contentType == "image/webp" ? "webp" : "png";

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
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return (data, "image/png", null);
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return (data, "image/jpeg", null);
        if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') return (data, "image/webp", null);
        return (null, null, "图标必须是 PNG、JPEG 或 WebP 图片");
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

    private static string Now() => DateTime.UtcNow.ToString("O");
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

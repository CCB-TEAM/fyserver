using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace fyserver.Services;

/// <summary>
/// 模组文件的对象存储（S3 兼容，主要面向 Cloudflare R2）。
/// 服务端只签发预签名链接：启动器直接把 Pak 上传到存储桶，玩家从公开域名下载，
/// 文件流量不经过本服务器。签名为手写 AWS SigV4，无需 SDK，NativeAOT 安全。
/// 配置保存在 ./data/mod-storage.json（data 目录在容器部署中可写）。
/// </summary>
public sealed class ModStorageService
{
    private const string ConfigPath = "./data/mod-storage.json";
    private const string Service = "s3";
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private StorageConfig? _config;

    public sealed record StorageConfig(bool Enabled, string Endpoint, string Bucket, string Region,
        string AccessKeyId, string SecretAccessKey, string PublicBaseUrl, bool VirtualHosted);

    // ------------------------------------------------------------------ 配置
    public StorageConfig Config
    {
        get
        {
            lock (_gate) return _config ??= Load();
        }
    }

    public bool IsEnabled
    {
        get
        {
            var c = Config;
            return c.Enabled && c.Endpoint != "" && c.Bucket != "" && c.AccessKeyId != "" &&
                   c.SecretAccessKey != "" && c.PublicBaseUrl != "";
        }
    }

    /// <summary>给后台看的配置：密钥只显示是否已设置。</summary>
    public JsonObject PublicConfig()
    {
        var c = Config;
        return new JsonObject
        {
            ["enabled"] = c.Enabled,
            ["ready"] = IsEnabled,
            ["endpoint"] = c.Endpoint,
            ["bucket"] = c.Bucket,
            ["region"] = c.Region,
            ["accessKeyId"] = c.AccessKeyId,
            ["secretAccessKeySet"] = c.SecretAccessKey != "",
            ["publicBaseUrl"] = c.PublicBaseUrl,
            ["virtualHosted"] = c.VirtualHosted
        };
    }

    /// <summary>保存配置。secretAccessKey 为空时保留原值。</summary>
    public (bool Ok, string Message) Save(JsonObject body)
    {
        string Str(string key) => body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var enabled = body["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b) && b;
        var endpoint = Str("endpoint").TrimEnd('/');
        var bucket = Str("bucket");
        var region = Str("region");
        var accessKeyId = Str("accessKeyId");
        var secret = Str("secretAccessKey");
        var publicBaseUrl = Str("publicBaseUrl").TrimEnd('/');
        var virtualHosted = body["virtualHosted"] is JsonValue vh && vh.TryGetValue<bool>(out var vhb) && vhb;

        if (endpoint != "" && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var ep) || ep.Scheme != "https"))
            return (false, "端点必须是 https 地址，例如 https://<账号ID>.r2.cloudflarestorage.com");
        if (publicBaseUrl != "" && (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var pb) || pb.Scheme is not ("http" or "https")))
            return (false, "公开下载域名必须是 http(s) 地址，例如 https://mods.example.com");
        if (bucket != "" && !bucket.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-'))
            return (false, "存储桶名称只能包含小写字母、数字和连字符");

        lock (_gate)
        {
            var old = _config ??= Load();
            var config = new StorageConfig(enabled, endpoint, bucket, region == "" ? "auto" : region,
                accessKeyId, secret == "" ? old.SecretAccessKey : secret, publicBaseUrl, virtualHosted);
            if (config.Enabled && (config.Endpoint == "" || config.Bucket == "" || config.AccessKeyId == "" ||
                                   config.SecretAccessKey == "" || config.PublicBaseUrl == ""))
                return (false, "启用前请填写端点、存储桶、Access Key ID、Secret Access Key 和公开下载域名");

            var json = new JsonObject
            {
                ["enabled"] = config.Enabled,
                ["endpoint"] = config.Endpoint,
                ["bucket"] = config.Bucket,
                ["region"] = config.Region,
                ["accessKeyId"] = config.AccessKeyId,
                ["secretAccessKey"] = config.SecretAccessKey,
                ["publicBaseUrl"] = config.PublicBaseUrl,
                ["virtualHosted"] = config.VirtualHosted
            };
            var temp = ConfigPath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(temp, json.ToJsonString());
                File.Move(temp, ConfigPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, "保存存储配置失败：" + ex.Message);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            _config = config;
            return (true, config.Enabled ? "存储配置已保存并启用" : "存储配置已保存（未启用）");
        }
    }

    private static StorageConfig Load()
    {
        var empty = new StorageConfig(false, "", "", "auto", "", "", "", false);
        if (!File.Exists(ConfigPath)) return empty;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is not JsonObject o) return empty;
            string Str(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var enabled = o["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b) && b;
            var region = Str("region");
            return new StorageConfig(enabled, Str("endpoint"), Str("bucket"), region == "" ? "auto" : region,
                Str("accessKeyId"), Str("secretAccessKey"), Str("publicBaseUrl"),
                o["virtualHosted"] is JsonValue vh && vh.TryGetValue<bool>(out var vhb) && vhb);
        }
        catch (System.Text.Json.JsonException)
        {
            return empty;
        }
    }

    // ------------------------------------------------------------------ 对象路径
    /// <summary>模组文件在存储桶中的路径；按 sha256 区分版本，更新模组不会覆盖正在被下载的旧文件。</summary>
    public static string ObjectKey(string modId, string sha256) => $"mods/{modId}/{sha256}.pak";

    public string PublicUrl(string key) => Config.PublicBaseUrl + "/" + key;

    /// <summary>若下载地址属于本存储桶的公开域名，返回对应的对象路径。</summary>
    public string? KeyFromPublicUrl(string? url)
    {
        var prefix = Config.PublicBaseUrl + "/";
        if (Config.PublicBaseUrl == "" || url == null || !url.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = url[prefix.Length..];
        return key.StartsWith("mods/", StringComparison.Ordinal) && !key.Contains("..") ? key : null;
    }

    // ------------------------------------------------------------------ 签名
    public (string Url, DateTime ExpiresAt) PresignPut(string key) => Presign("PUT", key, UploadUrlLifetime);

    /// <summary>删除存储桶中的对象（模组删除或更换文件时调用），失败只记录日志。</summary>
    public async Task DeleteObjectAsync(string key)
    {
        if (!IsEnabled) return;
        try
        {
            var (url, _) = Presign("DELETE", key, TimeSpan.FromMinutes(5));
            using var response = await _http.DeleteAsync(url);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                Console.WriteLine($"删除模组文件 {key} 失败：HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"删除模组文件 {key} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// AWS SigV4 查询串预签名。路径风格：endpoint/bucket/key（Cloudflare R2）；
    /// 虚拟主机风格：bucket.endpoint/key（阿里云 OSS 等只支持这种方式的服务）。
    /// </summary>
    private (string Url, DateTime ExpiresAt) Presign(string method, string key, TimeSpan lifetime)
    {
        var c = Config;
        var endpoint = new Uri(c.Endpoint);
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{date}/{c.Region}/{Service}/aws4_request";
        var hostName = c.VirtualHosted ? $"{c.Bucket}.{endpoint.Host}" : endpoint.Host;
        var host = endpoint.IsDefaultPort ? hostName : $"{hostName}:{endpoint.Port}";
        var keyPath = string.Join("/", key.Split('/').Select(Encode));
        var path = c.VirtualHosted ? "/" + keyPath : "/" + Encode(c.Bucket) + "/" + keyPath;

        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{c.AccessKeyId}/{scope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = ((int)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host"
        };
        var canonicalQuery = string.Join("&", query.Select(kv => Encode(kv.Key) + "=" + Encode(kv.Value)));
        var canonicalRequest = $"{method}\n{path}\n{canonicalQuery}\nhost:{host}\n\nhost\nUNSIGNED-PAYLOAD";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var signingKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + c.SecretAccessKey), date);
        signingKey = Hmac(signingKey, c.Region);
        signingKey = Hmac(signingKey, Service);
        signingKey = Hmac(signingKey, "aws4_request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        var url = $"{endpoint.Scheme}://{host}{path}?{canonicalQuery}&X-Amz-Signature={signature}";
        return (url, now + lifetime);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>SigV4 要求的 URI 编码：只保留 A-Z a-z 0-9 - _ . ~，其余按 UTF-8 百分号编码（大写十六进制）。</summary>
    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var ch = (char)b;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '~') sb.Append(ch);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

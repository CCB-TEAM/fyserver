using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace fyserver.Pages;

/// <summary>首页/公告配置编辑：直接编辑 config/frontpage.json 原文。</summary>
[IgnoreAntiforgeryToken]
public class FrontpageModel : PageModel
{
    private const string ConfigPath = "./config/frontpage.json";

    private readonly FrontpageConfigService _frontpage;

    public FrontpageModel(FrontpageConfigService frontpage) => _frontpage = frontpage;

    [BindProperty]
    public string? Json { get; set; }

    public string FilePath => ConfigPath;
    public long FileSize { get; private set; }
    public string LastWriteTime { get; private set; } = "(文件不存在)";
    public int ElementCount { get; private set; }
    public int TargetedCount { get; private set; }

    public void OnGet()
    {
        Json = _frontpage.ReadRaw();
        RefreshFileInfo();
    }

    /// <summary>保存编辑后的 JSON（保存前校验格式并自动备份）。</summary>
    public IActionResult OnPost()
    {
        var (ok, message) = _frontpage.SaveRaw(Json ?? "");
        if (ok)
            TempData["Message"] = message;
        else
            TempData["Error"] = message;

        return RedirectToPage();
    }

    /// <summary>把编辑器内容重新缩进排版后再加载（不写文件）。</summary>
    public IActionResult OnPostFormat()
    {
        if (string.IsNullOrWhiteSpace(Json))
        {
            TempData["Error"] = "内容为空，无法格式化";
            return RedirectToPage();
        }

        try
        {
            var node = JsonNode.Parse(Json);
            Json = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
            TempData["Message"] = "已格式化（尚未保存，确认无误后点击保存）";
        }
        catch (JsonException ex)
        {
            TempData["Error"] = $"JSON 格式错误：{ex.Message}";
        }

        RefreshFileInfo();
        return Page();
    }

    private void RefreshFileInfo()
    {
        try
        {
            if (!System.IO.File.Exists(ConfigPath))
                return;

            var info = new FileInfo(ConfigPath);
            FileSize = info.Length;
            LastWriteTime = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

            var node = JsonNode.Parse(System.IO.File.ReadAllText(ConfigPath));
            if (node is JsonObject root)
            {
                ElementCount = (root["elements"] as JsonArray)?.Count ?? 0;
                TargetedCount = (root["targeted"] as JsonArray)?.Count ?? 0;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LastWriteTime = $"(读取失败：{ex.Message})";
        }
    }
}

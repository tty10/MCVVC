using System.Net.Http;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class CfProjectInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Summary { get; init; } = "";
    public long DownloadCount { get; init; }

    public string DownloadText => DownloadCount >= 1_000_000
        ? $"{DownloadCount / 1_000_000.0:0.#}M 下载"
        : DownloadCount >= 1000
            ? $"{DownloadCount / 1000.0:0.#}K 下载"
            : $"{DownloadCount} 下载";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public sealed class CfApiFile
{
    public int Id { get; init; }
    public string FileName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long FileLength { get; init; }
}

/// <summary>搜索结果：条目 + 分页信息。totalCount 用来判断还有没有下一页。</summary>
public sealed class CfSearchResult
{
    public List<CfProjectInfo> Items { get; } = new();
    public int TotalCount { get; set; }
}

/// <summary>
/// CurseForge 官方 API。所有请求都要 key，放 x-api-key 头。
///
/// CDN 下载同样建议带 key（归属统计，官方已公告强制鉴权），见
/// CurseForgeService.AuthHeaders()，ModpackInstaller 的下载任务已带上。
///
/// classId 刻意不硬编码 —— Modpacks 的类别 id 各时期不一样，写死一旦变了搜索就静默返回空。
/// 所以先拉 categories，按 slug 找 "modpacks" 再搜。
/// </summary>
public static class CurseForgeApi
{
    private const string Base = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static bool HasKey(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    public static async Task<int?> FindModpacksClassIdAsync(string apiKey, CancellationToken ct = default)
    {
        var json = await GetAsync($"{Base}/categories?gameId={MinecraftGameId}", apiKey, ct);
        if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonArray array)
            return null;

        // 同一个 slug 可能出现多次（一次是 class、一次是它下面的 category），
        // 要的是 class 本身，所以优先挑 isClass 的。
        JsonObject? fallback = null;

        foreach (var node in array)
        {
            if (node is not JsonObject category) continue;
            if (!Str(category, "slug").Equals("modpacks", StringComparison.OrdinalIgnoreCase)) continue;

            var isClass = category["isClass"] is JsonValue cv && cv.TryGetValue<bool>(out var flag) && flag;
            if (isClass)
            {
                fallback = category;
                break;
            }
            fallback ??= category;
        }

        if (fallback == null) return null;
        return fallback["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) ? id : null;
    }

    /// <summary>
    /// 按热度搜整合包。index/pageSize 翻页：pageSize 上限 50（实测 100 直接报错），
    /// 想翻更多页用 index 而不是加大 pageSize。
    /// </summary>
    public static async Task<CfSearchResult> SearchModpacksAsync(
        string apiKey,
        string? query,
        int index = 0,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var classId = await FindModpacksClassIdAsync(apiKey, ct);
        if (classId == null) throw new InvalidOperationException("没找到 modpacks 类别，key 可能无效");

        // sortField 2 = Popularity（按热度）
        var url = $"{Base}/mods/search?gameId={MinecraftGameId}&classId={classId}"
                  + $"&pageSize={pageSize}&index={index}&sortField=2&sortOrder=desc";
        if (!string.IsNullOrWhiteSpace(query))
            url += "&searchFilter=" + Uri.EscapeDataString(query);

        var json = await GetAsync(url, apiKey, ct);
        var result = new CfSearchResult();
        if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonArray data)
            return result;

        if (root["pagination"] is JsonObject page)
            result.TotalCount = page["totalCount"] is JsonValue tv && tv.TryGetValue<int>(out var total)
                ? total
                : 0;

        foreach (var node in data)
        {
            if (node is not JsonObject project) continue;
            if (project["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id)) continue;

            result.Items.Add(new CfProjectInfo
            {
                Id = id,
                Name = Str(project, "name"),
                Slug = Str(project, "slug"),
                Summary = Str(project, "summary"),
                DownloadCount = project["downloadCount"] is JsonValue dv && dv.TryGetValue<long>(out var d) ? d : 0,
            });
        }
        return result;
    }

    public static async Task<List<CfApiFile>> GetFilesAsync(
        string apiKey,
        int projectId,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var url = $"{Base}/mods/{projectId}/files?pageSize={pageSize}&index=0";
        var json = await GetAsync(url, apiKey, ct);

        // 注意这里是 { data: [...] }，不是裸数组
        if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonArray array)
            return new List<CfApiFile>();

        var result = new List<CfApiFile>();
        foreach (var node in array)
        {
            if (node is not JsonObject file) continue;
            var downloadUrl = Str(file, "downloadUrl");
            if (string.IsNullOrEmpty(downloadUrl)) continue;

            result.Add(new CfApiFile
            {
                Id = file["id"] is JsonValue iv && iv.TryGetValue<int>(out var id) ? id : 0,
                FileName = Str(file, "fileName"),
                DownloadUrl = downloadUrl,
                FileLength = file["fileLength"] is JsonValue fv && fv.TryGetValue<long>(out var len) ? len : 0,
            });
        }
        return result;
    }

    /// <summary>在项目的文件列表里按 id 找。</summary>
    public static async Task<CfApiFile?> GetFileByIdAsync(
        string apiKey,
        int projectId,
        int fileId,
        CancellationToken ct = default)
    {
        var files = await GetFilesAsync(apiKey, projectId, 50, ct);
        return files.FirstOrDefault(f => f.Id == fileId);
    }

    private static async Task<string> GetAsync(string url, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 官方 API 用这个 header 传 key
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"CurseForge API {(int)response.StatusCode}：{Trim(body)}");
        }
        return body;
    }

    private static string Trim(string text)
    {
        text = text.Replace("\n", " ").Replace("\r", "");
        return text.Length > 160 ? text[..160] : text;
    }

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

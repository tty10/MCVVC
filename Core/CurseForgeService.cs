using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class CfFile
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string ProjectType { get; init; } = "";
}

/// <summary>
/// CurseForge 文件解析。
///
/// 官方 API 要 key（没有就是 403），所以走社区镜像 api.cfwidget.com 查项目文件列表，
/// 再用 Forge 的 CDN 命名规则拼下载直链 —— 这条链实测可用且无需鉴权：
///   mediafilez.forgecdn.net/files/{fileId/1000}/{fileId%1000}/{fileName}
///
/// 注意这是非官方途径，有被限流或挂掉的风险，项目查询结果做了进程内缓存。
/// </summary>
public static class CurseForgeService
{
    private const string WidgetBase = "https://api.cfwidget.com/minecraft/mod/";
    private const string CdnBase = "https://mediafilez.forgecdn.net/files/";

    private static readonly ConcurrentDictionary<int, Task<CfProject>> Cache = new();

    public static Task<CfProject> GetProjectAsync(int projectId, CancellationToken ct = default)
    {
        return Cache.GetOrAdd(projectId, id => LoadAsync(id, ct));
    }

    private static async Task<CfProject> LoadAsync(int projectId, CancellationToken ct)
    {
        var project = new CfProject { Id = projectId };

        try
        {
            var json = await Downloader.GetStringAsync(WidgetBase + projectId, ct);
            if (JsonNode.Parse(json) is not JsonObject root) return project;

            project.Type = Str(root, "type");
            project.Slug = Str(root, "slug");

            if (root["files"] is not JsonArray files) return project;

            foreach (var node in files)
            {
                if (node is not JsonObject file) continue;
                if (file["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id)) continue;

                var name = Str(file, "name");
                if (string.IsNullOrEmpty(name)) continue;

                project.Files.Add(new CfFile
                {
                    Id = id,
                    Name = name,
                    Size = file["filesize"] is JsonValue sv && sv.TryGetValue<long>(out var n) ? n : 0,
                    ProjectType = project.Type,
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 查不到就返回空，调用方会按“缺文件”报错
        }

        return project;
    }

    /// <summary>查某个 fileID 对应的文件名。</summary>
    public static async Task<CfFile?> GetFileAsync(int projectId, int fileId, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        return project.Files.FirstOrDefault(f => f.Id == fileId);
    }

    public static string DirectUrl(int fileId, string fileName)
    {
        var head = fileId / 1000;
        var tail = fileId % 1000;
        return $"{CdnBase}{head}/{tail:D3}/{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    /// CurseForge 已公告要对文件下载强制 API key 鉴权。目前直链还能免 key 下，
    /// 但填了 key 就自动带上，强制那天不用改代码。
    /// </summary>
    public static Dictionary<string, string>? AuthHeaders()
    {
        var key = Settings.CurseForgeKey;
        return string.IsNullOrWhiteSpace(key) ? null : new() { ["x-api-key"] = key };
    }

    private static string Str(JsonObject obj, string key, string fallback = "") =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : fallback;
}

public sealed class CfProject
{
    public int Id { get; init; }
    public string Type { get; set; } = "";
    public string Slug { get; set; } = "";
    public List<CfFile> Files { get; } = new();
}

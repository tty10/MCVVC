using System.Text.Json.Nodes;

namespace MclLauncher.Core;

/// <summary>
/// Fabric 加载器安装。不需要跑 installer jar —— Fabric 的 meta 接口直接返回
/// 一份完整的版本 JSON，写进 versions/ 再下库就能用。
/// </summary>
public static class FabricService
{
    private const string MetaBase = "https://meta.fabricmc.net/v2/versions/loader";
    private const string DefaultMaven = "https://maven.fabricmc.net/";

    public static async Task<string> InstallAsync(
        string mcVersion,
        string? loaderVersion,
        GamePaths paths,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        loaderVersion ??= await GetLatestLoaderAsync(mcVersion, ct);

        progress?.Report(new InstallProgress { Stage = $"获取 Fabric {loaderVersion} 配置" });

        var profileUrl = $"{MetaBase}/{mcVersion}/{loaderVersion}/profile/json";
        var json = await Downloader.GetStringAsync(profileUrl, ct);
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new InvalidDataException("Fabric profile JSON 解析失败");

        var id = root["id"] is JsonValue iv && iv.TryGetValue<string>(out var rawId)
            ? rawId
            : $"fabric-loader-{loaderVersion}-{mcVersion}";

        var dir = Path.Combine(paths.Versions, id);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, id + ".json");
        await File.WriteAllTextAsync(jsonPath, json, ct);

        // 下载 Fabric 自己的库
        var tasks = new List<DownloadTask>();
        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var node in libraries)
            {
                if (node is not JsonObject lib) continue;
                var name = lib["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : "";
                if (string.IsNullOrEmpty(name)) continue;

                var baseUrl = lib["url"] is JsonValue uv && uv.TryGetValue<string>(out var u) ? u : DefaultMaven;
                var (relative, url) = MavenPath(name, baseUrl);
                if (string.IsNullOrEmpty(relative)) continue;

                tasks.Add(new DownloadTask
                {
                    Url = url,
                    Destination = Path.Combine(paths.Libraries, relative.Replace('/', Path.DirectorySeparatorChar)),
                    Label = name,
                });
            }
        }

        if (tasks.Count > 0)
        {
            var report = new InstallProgress { Stage = "下载 Fabric 依赖", Total = tasks.Count };
            progress?.Report(report);
            await Downloader.DownloadAllAsync(tasks, 16, new Progress<DownloadProgress>(p =>
            {
                report.Done = p.Completed;
                report.Detail = p.Current;
                progress?.Report(report);
            }), ct);
        }

        return id;
    }

    public static async Task<List<(string Version, bool Stable)>> GetLoaderVersionsAsync(
        string mcVersion, CancellationToken ct = default)
    {
        var json = await Downloader.GetStringAsync($"{MetaBase}/{mcVersion}", ct);
        var result = new List<(string, bool)>();
        if (JsonNode.Parse(json) is not JsonArray array) return result;

        foreach (var node in array)
        {
            if (node?["loader"] is not JsonObject loader) continue;
            var version = loader["version"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            if (string.IsNullOrEmpty(version)) continue;

            var stable = loader["stable"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
            result.Add((version, stable));
        }
        return result;
    }

    public static async Task<string> GetLatestLoaderAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await Downloader.GetStringAsync($"{MetaBase}/{mcVersion}", ct);
        if (JsonNode.Parse(json) is not JsonArray array || array.Count == 0)
            throw new InvalidDataException($"Fabric 没有支持 {mcVersion} 的加载器");

        // 优先稳定版，没有就取第一个
        foreach (var node in array)
        {
            if (node?["loader"] is not JsonObject loader) continue;
            if (loader["stable"] is JsonValue sv && sv.TryGetValue<bool>(out var stable) && stable)
                return loader["version"]?.GetValue<string>() ?? "";
        }

        return array[0]?["loader"]?["version"]?.GetValue<string>()
               ?? throw new InvalidDataException("无法解析 Fabric 版本列表");
    }

    /// <summary>把 Maven 坐标 "group:artifact:version[:classifier]" 转成路径和完整 URL。</summary>
    private static (string path, string url) MavenPath(string coordinates, string baseUrl)
    {
        var parts = coordinates.Split(':');
        if (parts.Length < 3) return ("", "");

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var file = classifier == null
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{classifier}.jar";

        var path = $"{group}/{artifact}/{version}/{file}";
        var url = baseUrl.TrimEnd('/') + "/" + path;
        return (path, url);
    }
}

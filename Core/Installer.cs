using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class InstallProgress
{
    public string Stage { get; set; } = "";
    public int Done { get; set; }
    public int Total { get; set; }
    public long Bytes { get; set; }
    public string Detail { get; set; } = "";

    public double Percent => Total > 0 ? Math.Round(Done * 100.0 / Total, 1) : 0;

    public override string ToString() =>
        Total > 0 ? $"{Stage}  {Done}/{Total}" : Stage;
}

/// <summary>
/// 把一个远程版本装进本地 .minecraft。
/// 顺序：写 version json → client jar → libraries → assetIndex → assets → natives。
/// </summary>
public static class Installer
{
    public static async Task<VersionDetail> InstallAsync(
        ManifestEntry entry,
        GamePaths paths,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var report = new InstallProgress { Stage = "获取版本信息" };
        progress?.Report(report);

        // 1. 版本 JSON 先落地，后面的步骤都以它为准
        var jsonText = await Downloader.GetStringAsync(entry.Url, ct);
        if (JsonNode.Parse(jsonText) is not JsonObject root)
            throw new InvalidDataException($"{entry.Id} 的版本 JSON 解析失败");

        var versionDir = Path.Combine(paths.Versions, entry.Id);
        Directory.CreateDirectory(versionDir);
        var jsonPath = Path.Combine(versionDir, entry.Id + ".json");
        await File.WriteAllTextAsync(jsonPath, jsonText, ct);

        var version = new VersionDetail(root, jsonPath);

        // 2. client jar
        if (!string.IsNullOrEmpty(version.ClientUrl))
        {
            report = new InstallProgress { Stage = "下载客户端", Total = 1 };
            progress?.Report(report);

            await Downloader.DownloadAsync(new DownloadTask
            {
                Url = version.ClientUrl,
                Destination = version.ClientJarPath,
                Sha1 = version.ClientSha1,
                Size = version.ClientSize,
                Label = $"{entry.Id}.jar",
            }, null, null, ct);

            report.Done = 1;
            progress?.Report(report);
        }

        // 3. libraries（过 rules，别把 macOS 的库拉到 Windows 上）
        var libraryTasks = BuildLibraryTasks(version, paths);
        if (libraryTasks.Count > 0)
        {
            report = new InstallProgress { Stage = "下载依赖库", Total = libraryTasks.Count };
            progress?.Report(report);
            await Downloader.DownloadAllAsync(libraryTasks, 16, Wrap(progress, report), ct);
        }

        // 4. assetIndex
        if (!string.IsNullOrEmpty(version.AssetIndexUrl))
        {
            var indexDir = Path.Combine(paths.Assets, "indexes");
            var indexPath = Path.Combine(indexDir, version.AssetIndexId + ".json");

            report = new InstallProgress { Stage = "下载资源索引", Total = 1 };
            progress?.Report(report);

            await Downloader.DownloadAsync(new DownloadTask
            {
                Url = version.AssetIndexUrl,
                Destination = indexPath,
                Sha1 = version.AssetIndexSha1,
                Label = "asset index",
            }, null, null, ct);

            // 5. assets 本体，几千个对象
            var assetTasks = BuildAssetTasks(indexPath, paths);
            if (assetTasks.Count > 0)
            {
                report = new InstallProgress { Stage = "下载游戏资源", Total = assetTasks.Count };
                progress?.Report(report);
                await Downloader.DownloadAllAsync(assetTasks, 32, Wrap(progress, report), ct);
            }

            // 老版本的 virtual 映射
            TryMaterializeVirtual(indexPath, paths);
        }

        // 6. natives
        report = new InstallProgress { Stage = "解压本地库" };
        progress?.Report(report);
        GameBuilder.ExtractNatives(version, paths, version.NativesDirectory);

        report.Stage = "安装完成";
        progress?.Report(report);
        return version;
    }

    private static List<DownloadTask> BuildLibraryTasks(VersionDetail version, GamePaths paths)
    {
        var tasks = new List<DownloadTask>();
        foreach (var lib in version.GetLibraries())
        {
            if (!RuleEngine.Allow(lib.Rules)) continue;

            if (lib.Artifact is { } artifact && !string.IsNullOrEmpty(artifact.Url))
            {
                tasks.Add(new DownloadTask
                {
                    Url = artifact.Url,
                    Destination = Path.Combine(paths.Libraries, artifact.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = artifact.Sha1,
                    Size = artifact.Size,
                    Label = lib.Name,
                });
            }

            // 1.19 以前的原生库单独打包，也要下
            if (!string.IsNullOrEmpty(lib.NativesClassifier)
                && lib.Classifiers.TryGetValue(lib.NativesClassifier, out var native))
            {
                tasks.Add(new DownloadTask
                {
                    Url = native.Url,
                    Destination = Path.Combine(paths.Libraries, native.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1 = native.Sha1,
                    Size = native.Size,
                    Label = lib.Name + " (natives)",
                });
            }
        }
        return tasks;
    }

    private static List<DownloadTask> BuildAssetTasks(string indexPath, GamePaths paths)
    {
        var tasks = new List<DownloadTask>();
        if (!File.Exists(indexPath)) return tasks;

        if (JsonNode.Parse(File.ReadAllText(indexPath)) is not JsonObject root) return tasks;
        if (root["objects"] is not JsonObject objects) return tasks;

        foreach (var kv in objects)
        {
            if (kv.Value is not JsonObject obj) continue;
            var hash = obj["hash"]?.GetValue<string>();
            if (string.IsNullOrEmpty(hash)) continue;

            tasks.Add(new DownloadTask
            {
                Url = "https://resources.download.minecraft.net/" + hash[..2] + "/" + hash,
                Destination = Path.Combine(paths.Assets, "objects", hash[..2], hash),
                Sha1 = hash,
                Size = obj["size"] is JsonValue sv && sv.TryGetValue<long>(out var n) ? n : 0,
                Label = kv.Key,
            });
        }
        return tasks;
    }

    private static void TryMaterializeVirtual(string indexPath, GamePaths paths)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(indexPath)) is not JsonObject root) return;
            if (root["virtual"] == null && root["map_to_resources"] == null) return;

            var target = Path.Combine(paths.Assets, "virtual", "legacy");
            if (root["objects"] is not JsonObject objects) return;

            foreach (var kv in objects)
            {
                if (kv.Value is not JsonObject obj) continue;
                var hash = obj["hash"]?.GetValue<string>();
                if (string.IsNullOrEmpty(hash)) continue;

                var source = Path.Combine(paths.Assets, "objects", hash[..2], hash);
                if (!File.Exists(source)) continue;

                var dest = Path.Combine(target, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? target);
                if (!File.Exists(dest)) File.Copy(source, dest);
            }
        }
        catch
        {
            // virtual 映射失败不影响新版客户端，忽略
        }
    }

    /// <summary>把下载器的进度翻译成安装进度，UI 只认一种。</summary>
    private static IProgress<DownloadProgress> Wrap(IProgress<InstallProgress>? outer, InstallProgress report)
    {
        return new Progress<DownloadProgress>(p =>
        {
            report.Done = p.Completed;
            report.Detail = p.Current;
            report.Bytes = p.Bytes;
            outer?.Report(report);
        });
    }
}

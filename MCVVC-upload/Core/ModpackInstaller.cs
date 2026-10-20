using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MclLauncher.Core;

public sealed class ModpackFile
{
    // CurseForge 的 manifest.json 只给这两个 id，文件名和路径都要反查
    public int ProjectId { get; set; }
    public int FileId { get; set; }
}

public sealed class ModpackManifest
{
    public string Name { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public List<ModpackFile> Files { get; } = new();

    public string LoaderLabel => Loader switch
    {
        "fabric" => $"Fabric {LoaderVersion}",
        "forge" => $"Forge {LoaderVersion}",
        "neoforge" => $"NeoForge {LoaderVersion}",
        "quilt" => $"Quilt {LoaderVersion}",
        _ => "原版",
    };
}

/// <summary>
/// CurseForge 整合包（.zip）导入。
///
/// 结构：manifest.json + overrides/
/// 每个实例是一个独立 gameDir（versions/&lt;实例名&gt;），启动时 --gameDir 指向它，
/// 这样不同整合包的 mods 不会互相污染 —— 这就是版本隔离。
/// </summary>
public static class ModpackInstaller
{
    private static readonly Regex InvalidChars = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);

    public static ModpackManifest Read(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);

        if (zip.GetEntry("manifest.json") is { } curse)
            return ReadCurseForge(curse);

        throw new InvalidDataException(
            "不是有效的 CurseForge 整合包：zip 里没有 manifest.json");
    }

    /// <summary>
    /// CurseForge 的 manifest.json 只给 projectID / fileID，**不带文件名也不带路径**，
    /// 所以文件名得靠社区镜像反查，落盘位置按项目类型推断。
    /// </summary>
    private static ModpackManifest ReadCurseForge(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        if (JsonNode.Parse(stream) is not JsonObject root)
            throw new InvalidDataException("manifest.json 解析失败");

        var manifest = new ModpackManifest
        {
            Name = Str(root, "name", "未命名整合包"),
            VersionId = Str(root, "version", ""),
        };

        if (root["minecraft"] is JsonObject mc)
        {
            manifest.MinecraftVersion = Str(mc, "version", "");

            if (mc["modLoaders"] is JsonArray loaders)
            {
                foreach (var node in loaders)
                {
                    // 元素是对象：{ "id": "forge-14.23.5.2860", "primary": true }
                    var id = node is JsonObject loaderObj ? Str(loaderObj, "id", "") : "";
                    if (string.IsNullOrEmpty(id)) continue;

                    var split = id.IndexOf('-');
                    if (split <= 0) continue;

                    manifest.Loader = id[..split];
                    manifest.LoaderVersion = id[(split + 1)..];
                    break;   // 取第一个（通常是 primary）
                }
            }
        }

        if (root["files"] is JsonArray files)
        {
            foreach (var node in files)
            {
                if (node is not JsonObject file) continue;

                var projectId = 0;
                var fileId = 0;
                if (file["projectID"] is JsonValue pv && pv.TryGetValue<int>(out var p)) projectId = p;
                if (file["fileID"] is JsonValue fv && fv.TryGetValue<int>(out var f)) fileId = f;

                if (projectId <= 0 || fileId <= 0) continue;

                manifest.Files.Add(new ModpackFile { ProjectId = projectId, FileId = fileId });
            }
        }

        return manifest;
    }

    public static async Task<string> InstallAsync(
        string zipPath,
        GamePaths paths,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var manifest = Read(zipPath);

        if (string.IsNullOrEmpty(manifest.MinecraftVersion))
            throw new InvalidDataException("整合包没有声明 minecraft 版本");

        if (manifest.Loader == "neoforge")
            throw new NotSupportedException("NeoForge 整合包还没接，Fabric / Forge 可以正常导入。");

        // 1. 原版必须先有
        var vanillaDir = Path.Combine(paths.Versions, manifest.MinecraftVersion);
        if (!File.Exists(Path.Combine(vanillaDir, manifest.MinecraftVersion + ".json")))
        {
            progress?.Report(new InstallProgress { Stage = $"安装原版 {manifest.MinecraftVersion}" });
            var all = await ManifestService.GetAsync(ct: ct);
            var entry = all.Versions.FirstOrDefault(v => v.Id == manifest.MinecraftVersion)
                        ?? throw new InvalidDataException($"找不到版本 {manifest.MinecraftVersion}");
            await Installer.InstallAsync(entry, paths, progress, ct);
        }

        // 2. 加载器
        var baseVersionId = manifest.MinecraftVersion;
        if (manifest.Loader == "fabric")
        {
            progress?.Report(new InstallProgress { Stage = "安装 Fabric 加载器" });
            baseVersionId = await FabricService.InstallAsync(
                manifest.MinecraftVersion, manifest.LoaderVersion, paths, progress, ct);
        }
        else if (manifest.Loader == "forge")
        {
            progress?.Report(new InstallProgress { Stage = "安装 Forge 加载器" });
            baseVersionId = await ForgeService.InstallAsync(
                manifest.MinecraftVersion, manifest.LoaderVersion, paths, null, progress, ct);
            await ForgeService.EnsureLibrariesAsync(baseVersionId, paths, progress, ct);
        }
        else if (manifest.Loader == "quilt")
        {
            throw new NotSupportedException("Quilt 加载器还没接，先支持 Fabric / Forge。");
        }

        // 3. 建实例
        var instanceId = MakeInstanceId(manifest, paths);
        var instanceDir = Path.Combine(paths.Versions, instanceId);
        Directory.CreateDirectory(instanceDir);

        progress?.Report(new InstallProgress { Stage = $"创建实例 {instanceId}" });

        // 4. 实例的版本 JSON：复制加载器/原版的，改 id，jar 指回原版
        var sourceJson = Path.Combine(paths.Versions, baseVersionId, baseVersionId + ".json");
        if (JsonNode.Parse(File.ReadAllText(sourceJson)) is not JsonObject root)
            throw new InvalidDataException("基础版本 JSON 解析失败");

        root["id"] = instanceId;
        if (root["jar"] == null && baseVersionId != manifest.MinecraftVersion)
            root["jar"] = manifest.MinecraftVersion;

        await File.WriteAllTextAsync(
            Path.Combine(instanceDir, instanceId + ".json"),
            root.ToJsonString(), ct);

        // 5. 下载 mod 和资源：CurseForge 直链统一带 key（归属统计 + 防强制鉴权）
        var resolved = await ResolveFilesAsync(manifest, ct);
        var cfHeaders = CurseForgeService.AuthHeaders();
        var tasks = resolved
            .Select(f => new DownloadTask
            {
                Url = f.url,
                Destination = SafeJoin(instanceDir, f.path),
                Size = f.size,
                Label = Path.GetFileName(f.path),
                Headers = cfHeaders,
            })
            .ToList();

        if (tasks.Count > 0)
        {
            var report = new InstallProgress { Stage = "下载模组", Total = tasks.Count };
            progress?.Report(report);
            await Downloader.DownloadAllAsync(tasks, 12, new Progress<DownloadProgress>(p =>
            {
                report.Done = p.Completed;
                report.Detail = p.Current;
                report.Bytes = p.Bytes;
                progress?.Report(report);
            }), ct);
        }

        // 6. overrides 覆盖到实例目录
        progress?.Report(new InstallProgress { Stage = "解压配置文件" });
        ExtractOverrides(zipPath, instanceDir);

        progress?.Report(new InstallProgress { Stage = $"整合包 {manifest.Name} 安装完成" });
        return instanceId;
    }

    private static async Task<List<(string path, string url, long size)>> ResolveFilesAsync(
        ModpackManifest manifest, CancellationToken ct)
    {
        var result = new List<(string, string, long)>();

        // CurseForge：manifest.json 只给 projectID/fileID，拿不到文件名和路径。
        // 有 API key 就走官方 /files 拿 downloadUrl（最可靠），没有才走社区镜像拼链。
        var useOfficialApi = Core.Settings.HasCurseForgeKey;

        foreach (var f in manifest.Files)
        {
            string url;
            string name;
            long size;
            string projectType = "";

            if (useOfficialApi)
            {
                var cfFile = await Core.CurseForgeApi.GetFileByIdAsync(
                    Core.Settings.CurseForgeKey, f.ProjectId, f.FileId, ct)
                    ?? throw new InvalidDataException(
                        $"CurseForge 项目 {f.ProjectId} 里找不到文件 {f.FileId}");

                url = cfFile.DownloadUrl;
                name = cfFile.FileName;
                size = cfFile.FileLength;
            }
            else
            {
                var file = await CurseForgeService.GetFileAsync(f.ProjectId, f.FileId, ct);
                if (file == null)
                {
                    throw new InvalidDataException(
                        $"CurseForge 项目 {f.ProjectId} 里找不到文件 {f.FileId}，社区镜像可能没同步");
                }

                projectType = file.ProjectType;
                name = file.Name;
                size = file.Size;
                url = CurseForgeService.DirectUrl(file.Id, file.Name);
            }

            // 官方 API 不返回项目类型，默认按 mod 处理
            var directory = useOfficialApi ? "mods" : InferDirectory(projectType);
            result.Add(($"{directory}/{name}", url, size));
        }

        return result;
    }

    private static string InferDirectory(string projectType) => projectType switch
    {
        "mod" => "mods",
        "resourcepack" => "resourcepacks",
        "shader" => "shaderpacks",
        "world" => "saves",
        "modpack" => "mods",
        _ => "mods",
    };

    private static void ExtractOverrides(string zipPath, string instanceDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.FullName.EndsWith("/")) continue;

            var relative = entry.FullName["overrides/".Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;

            var target = SafeJoin(instanceDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? instanceDir);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>zip 里的路径不可信，挡掉 ../ 之类的穿越。</summary>
    private static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"整合包里有非法路径：{relative}");
        return full;
    }

    private static string MakeInstanceId(ModpackManifest manifest, GamePaths paths)
    {
        var baseName = InvalidChars.Replace(manifest.Name, "_").Trim();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "modpack";
        if (baseName.Length > 40) baseName = baseName[..40];

        var candidate = baseName;
        var index = 2;
        while (Directory.Exists(Path.Combine(paths.Versions, candidate)))
        {
            candidate = $"{baseName} ({index++})";
        }
        return candidate;
    }

    private static string Str(JsonObject obj, string key, string fallback) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : fallback;
}

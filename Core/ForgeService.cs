using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class ForgeBuild
{
    public string McVersion { get; init; } = "";
    public string Version { get; init; } = "";
    public bool IsRecommended { get; set; }
    public bool IsLatest { get; set; }

    /// <summary>installer 生成的版本目录名，形如 1.20.1-forge-47.2.0。</summary>
    public string FullId => $"{McVersion}-forge-{Version}";

    public string Display => IsRecommended ? $"{Version}（推荐）" : IsLatest ? $"{Version}（最新）" : Version;
}

/// <summary>
/// Forge 安装。和 Fabric 完全不同 —— Forge 必须跑官方 installer jar，
/// 由它生成版本 JSON 并下载一部分库，启动器只负责补剩下的。
/// </summary>
public static class ForgeService
{
    private const string MavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string MetadataUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    private const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    public static async Task<List<ForgeBuild>> GetBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await Downloader.GetStringAsync(MetadataUrl, ct);
        if (JsonNode.Parse(json) is not JsonObject root) return new List<ForgeBuild>();
        if (root[mcVersion] is not JsonArray array) return new List<ForgeBuild>();

        var builds = new List<ForgeBuild>();
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var full)) continue;
            if (string.IsNullOrEmpty(full)) continue;

            var version = full.StartsWith(mcVersion + "-", StringComparison.Ordinal)
                ? full[(mcVersion.Length + 1)..]
                : full;

            builds.Add(new ForgeBuild { McVersion = mcVersion, Version = version });
        }

        // 标出推荐版和最新版
        try
        {
            var promos = await Downloader.GetStringAsync(PromotionsUrl, ct);
            if (JsonNode.Parse(promos) is JsonObject p && p["promos"] is JsonObject table)
            {
                var recommended = Str(table, $"{mcVersion}-recommended");
                var latest = Str(table, $"{mcVersion}-latest");

                foreach (var build in builds)
                {
                    if (!string.IsNullOrEmpty(recommended) && build.Version == recommended) build.IsRecommended = true;
                    if (!string.IsNullOrEmpty(latest) && build.Version == latest) build.IsLatest = true;
                }
            }
        }
        catch
        {
            // 推荐标记拿不到不影响安装
        }

        return builds
            .OrderByDescending(b => b.IsRecommended)
            .ThenByDescending(b => VersionSort(b.Version))
            .ToList();
    }

    public static async Task<string> InstallAsync(
        string mcVersion,
        string forgeVersion,
        GamePaths paths,
        JavaRuntime? java = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var full = $"{mcVersion}-{forgeVersion}";
        var versionId = $"{full}-forge-{forgeVersion}";
        var existing = Path.Combine(paths.Versions, versionId, versionId + ".json");
        if (File.Exists(existing))
        {
            progress?.Report(new InstallProgress { Stage = $"Forge {forgeVersion} 已安装" });
            return versionId;
        }

        // 0. 原版必须先装好。
        // Forge installer 只会把原版 jar 拖下来，**不会写原版版本 JSON** ——
        // 没有那个 JSON，inheritsFrom 就找不到父版本，合并失败、Java 版本也读不到。
        var vanillaJson = Path.Combine(paths.Versions, mcVersion, mcVersion + ".json");
        if (!File.Exists(vanillaJson))
        {
            progress?.Report(new InstallProgress { Stage = $"先安装原版 {mcVersion}" });
            var manifest = await ManifestService.GetAsync(ct: ct);
            var vanilla = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                          ?? throw new InvalidDataException($"找不到原版版本 {mcVersion}");
            await Installer.InstallAsync(vanilla, paths, progress, ct);
        }

        // 1. 下 installer
        var tempDir = Path.Combine(Path.GetTempPath(), "mcl-forge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, $"forge-{full}-installer.jar");

        progress?.Report(new InstallProgress { Stage = $"下载 Forge {forgeVersion} installer" });

        var installerUrl = $"{MavenBase}/{full}/forge-{full}-installer.jar";
        await Downloader.DownloadAsync(new DownloadTask
        {
            Url = installerUrl,
            Destination = installerPath,
            Label = $"forge-{full}-installer.jar",
        }, null, null, ct);

        // 2. 跑 installer。--installClient 是静默安装，不加会弹 GUI
        java ??= PickInstallerJava(paths, mcVersion);
        progress?.Report(new InstallProgress { Stage = $"运行 Forge installer（Java {java.MajorVersion}）" });

        var output = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = java.JavaExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(paths.Root);

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        if (!process.Start()) throw new InvalidOperationException("Forge installer 启动失败");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // installer 会联网下库，慢的时候要几分钟
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Forge installer 超时（8 分钟），可能是网络问题");
        }

        var exitCode = process.ExitCode;
        var logTail = Tail(output.ToString(), 12);

        try { Directory.Delete(tempDir, recursive: true); } catch { }

        if (exitCode != 0 || !File.Exists(existing))
        {
            throw new InvalidOperationException(
                $"Forge installer 失败（退出码 {exitCode}）。\n{logTail}");
        }

        // installer 不会生成 client jar。但它的 -DignoreList 里写的是 <version>.jar，
        // 所以必须把原版 jar 复制一份改名为 <version-id>.jar —— 否则原版 jar 不在
        // 忽略列表里，会被模块系统当成两个模块加载同一个包，直接 ResolutionException。
        var forgeJar = Path.Combine(paths.Versions, versionId, versionId + ".jar");
        var vanillaJar = Path.Combine(paths.Versions, mcVersion, mcVersion + ".jar");
        if (!File.Exists(forgeJar) && File.Exists(vanillaJar))
            File.Copy(vanillaJar, forgeJar);

        progress?.Report(new InstallProgress { Stage = $"Forge {forgeVersion} 安装完成" });
        return versionId;
    }

    /// <summary>installer 本身也要 Java 跑。按原版要求的版本挑，挑不到就退而求其次。</summary>
    private static JavaRuntime PickInstallerJava(GamePaths paths, string mcVersion)
    {
        var required = 17;
        var jsonPath = Path.Combine(paths.Versions, mcVersion, mcVersion + ".json");
        if (File.Exists(jsonPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(jsonPath)) is JsonObject root)
                    required = new VersionDetail(root, jsonPath).RequiredJava;
            }
            catch { }
        }

        var runtimes = JavaRuntimeLocator.DetectAll(paths);
        return JavaRuntimeLocator.PickFor(runtimes, required)
               ?? runtimes.OrderByDescending(r => r.MajorVersion).FirstOrDefault()
               ?? throw new InvalidOperationException("没有可用的 Java 运行时来执行 Forge installer");
    }

    /// <summary>installer 只装了一部分库，剩下的用统一流程补齐。</summary>
    public static async Task EnsureLibrariesAsync(
        string versionId,
        GamePaths paths,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var jsonPath = Path.Combine(paths.Versions, versionId, versionId + ".json");
        if (!File.Exists(jsonPath)) return;

        if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject root) return;
        var version = new VersionDetail(root, jsonPath);
        var resolved = VersionResolver.Resolve(version, paths);

        var tasks = new List<DownloadTask>();
        foreach (var lib in resolved.GetLibraries())
        {
            if (!RuleEngine.Allow(lib.Rules)) continue;
            if (lib.Artifact is not { } artifact || string.IsNullOrEmpty(artifact.Url)) continue;

            var destination = Path.Combine(paths.Libraries, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination)) continue;

            tasks.Add(new DownloadTask
            {
                Url = artifact.Url,
                Destination = destination,
                Sha1 = artifact.Sha1,
                Size = artifact.Size,
                Label = lib.Name,
            });
        }

        if (tasks.Count == 0) return;

        var report = new InstallProgress { Stage = "补齐 Forge 依赖", Total = tasks.Count };
        progress?.Report(report);
        await Downloader.DownloadAllAsync(tasks, 16, new Progress<DownloadProgress>(p =>
        {
            report.Done = p.Completed;
            report.Detail = p.Current;
            progress?.Report(report);
        }), ct);
    }

    private static string VersionSort(string version)
    {
        // 让 "47.10.0" 排在 "47.9.0" 前面：按段补零
        var parts = version.Split('.');
        return string.Join('.', parts.Select(p => int.TryParse(p, out var n) ? n.ToString("D6") : p));
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.TakeLast(lines));
    }

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

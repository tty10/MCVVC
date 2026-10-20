using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MclLauncher.Core;

public sealed class LaunchOptions
{
    public string PlayerName { get; set; } = "Player";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "0";
    public string UserType { get; set; } = "legacy";
    public string Xuid { get; set; } = "";
    public string ClientId { get; set; } = "";
    public int MaxMemoryMb { get; set; } = 2048;
    public string? JavaPath { get; set; }
    public string VersionName { get; set; } = "";
    public string VersionType { get; set; } = "release";
    public string GameDir { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string LauncherName { get; set; } = "MclLauncher";
    public string LauncherVersion { get; set; } = "1.0";
}

public sealed class ClasspathResult
{
    public string Value { get; set; } = "";
    public List<string> Missing { get; } = new();
}

public static class GameBuilder
{
    private static readonly Regex Placeholder = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static ClasspathResult BuildClasspath(
        VersionDetail version,
        GamePaths paths,
        string clientJar,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var result = new ClasspathResult();
        var entries = new List<string>();

        foreach (var lib in version.GetLibraries())
        {
            if (!RuleEngine.Allow(lib.Rules, features)) continue;
            if (lib.Artifact == null || string.IsNullOrEmpty(lib.Artifact.Path)) continue;

            var full = Path.Combine(paths.Libraries, lib.Artifact.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) entries.Add(full);
            else result.Missing.Add(full);
        }

        // client.jar 放最后：加了 Forge/Fabric 之后顺序错了会 NoSuchMethodError
        entries.Add(clientJar);
        result.Value = string.Join(Path.PathSeparator, entries);
        return result;
    }

    /// <summary>把 natives-*.jar 里的 dll 解到独立目录，交给 -Djava.library.path。</summary>
    public static int ExtractNatives(
        VersionDetail version,
        GamePaths paths,
        string nativesDir,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        Directory.CreateDirectory(nativesDir);
        var count = 0;

        foreach (var lib in version.GetLibraries())
        {
            if (!RuleEngine.Allow(lib.Rules, features)) continue;
            if (string.IsNullOrEmpty(lib.NativesClassifier)) continue;
            if (!lib.Classifiers.TryGetValue(lib.NativesClassifier, out var download)) continue;

            var jar = Path.Combine(paths.Libraries, download.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jar)) continue;

            using var zip = ZipFile.OpenRead(jar);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                if (lib.Exclude.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var target = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(target, overwrite: true);
                count++;
            }
        }
        return count;
    }

    public static Dictionary<string, string> BuildContext(
        VersionDetail version,
        GamePaths paths,
        string classpath,
        string nativesDir,
        LaunchOptions opt)
    {
        var uuid = opt.Uuid.Replace("-", "");
        var ctx = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = opt.PlayerName,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = opt.AccessToken,
            ["auth_session"] = $"token:{opt.AccessToken}:{uuid}",
            ["user_type"] = opt.UserType,
            ["auth_xuid"] = opt.Xuid,
            ["clientid"] = opt.ClientId,

            // 1.8.x 的 minecraftArguments 里带 ${user_properties}，漏了会直接崩
            ["user_properties"] = "{}",

            ["version_name"] = string.IsNullOrEmpty(opt.VersionName) ? version.Id : opt.VersionName,
            ["version_type"] = string.IsNullOrEmpty(opt.VersionType) ? version.Type : opt.VersionType,
            ["game_directory"] = string.IsNullOrEmpty(opt.GameDir) ? paths.Root : opt.GameDir,
            ["assets_root"] = paths.Assets,
            ["assets_index_name"] = version.AssetIndexId,
            ["natives_directory"] = nativesDir,
            ["library_directory"] = paths.Libraries,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),

            ["launcher_name"] = opt.LauncherName,
            ["launcher_version"] = opt.LauncherVersion,

            ["game_assets"] = "",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = "",
        };

        if (opt.Width is int w) ctx["resolution_width"] = w.ToString();
        if (opt.Height is int h) ctx["resolution_height"] = h.ToString();
        return ctx;
    }

    public static List<string> BuildCommand(
        VersionDetail version,
        GamePaths paths,
        LaunchOptions opt,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        if (string.IsNullOrEmpty(opt.JavaPath))
            throw new InvalidOperationException("还没选 Java 运行时");

        var classpath = BuildClasspath(version, paths, version.ClientJarPath, features);
        var nativesDir = version.NativesDirectory;
        ExtractNatives(version, paths, nativesDir, features);

        var ctx = BuildContext(version, paths, classpath.Value, nativesDir, opt);
        var cmd = new List<string> { opt.JavaPath };

        if (version.Root["arguments"] is JsonObject arguments)
        {
            // 1.13+ 的格式：jvm 和 game 分开，占位符全在 JSON 里
            cmd.AddRange(Expand(arguments["jvm"] as JsonArray, ctx, features));
            cmd.Add(version.MainClass);
            cmd.AddRange(Expand(arguments["game"] as JsonArray, ctx, features));
        }
        else
        {
            // 1.12- 的格式：只有一整个 minecraftArguments 字符串，JVM 参数自己拼
            cmd.AddRange(DefaultJvmArgs(ctx, opt.MaxMemoryMb));
            cmd.Add(version.MainClass);
            var raw = version.Root["minecraftArguments"] is JsonValue v && v.TryGetValue<string>(out var s)
                ? s
                : "";
            foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                cmd.Add(Substitute(token, ctx));
        }

        return cmd;
    }

    private static IEnumerable<string> DefaultJvmArgs(Dictionary<string, string> ctx, int maxMemoryMb)
    {
        yield return $"-Xmx{maxMemoryMb}M";
        yield return $"-Xms{Math.Min(maxMemoryMb, 1024)}M";
        yield return "-XX:+UnlockExperimentalVMOptions";
        yield return "-XX:+UseG1GC";
        yield return "-XX:G1NewSizePercent=20";
        yield return "-XX:G1ReservePercent=20";
        yield return "-XX:MaxGCPauseMillis=50";
        yield return "-XX:G1HeapRegionSize=32M";
        yield return $"-Djava.library.path={ctx["natives_directory"]}";
        yield return $"-Dminecraft.launcher.brand={ctx["launcher_name"]}";
        yield return $"-Dminecraft.launcher.version={ctx["launcher_version"]}";
        yield return "-cp";
        yield return ctx["classpath"];
    }

    private static IEnumerable<string> Expand(
        JsonArray? array,
        Dictionary<string, string> ctx,
        IReadOnlyDictionary<string, bool>? features)
    {
        if (array == null) yield break;

        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                yield return Substitute(text, ctx);
            }
            else if (node is JsonObject obj)
            {
                var rules = ParseRules(obj["rules"] as JsonArray);
                if (!RuleEngine.Allow(rules, features)) continue;

                var raw = obj["value"];
                if (raw is JsonArray values)
                {
                    foreach (var item in values)
                    {
                        if (item is JsonValue iv && iv.TryGetValue<string>(out var s))
                            yield return Substitute(s, ctx);
                    }
                }
                else if (raw is JsonValue single && single.TryGetValue<string>(out var one))
                {
                    yield return Substitute(one, ctx);
                }
            }
        }
    }

    private static List<Rule> ParseRules(JsonArray? array)
    {
        var rules = new List<Rule>();
        if (array == null) return rules;

        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;
            var rule = new Rule
            {
                Action = obj["action"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : "allow"
            };
            if (obj["os"] is JsonObject os)
            {
                if (os["name"] is JsonValue nv && nv.TryGetValue<string>(out var n)) rule.OsName = n;
                if (os["arch"] is JsonValue arv && arv.TryGetValue<string>(out var ar)) rule.OsArch = ar;
            }
            if (obj["features"] is JsonObject feats)
            {
                foreach (var kv in feats)
                {
                    if (kv.Value is JsonValue fv && fv.TryGetValue<bool>(out var b))
                        rule.Features[kv.Key] = b;
                }
            }
            rules.Add(rule);
        }
        return rules;
    }

    private static string Substitute(string text, Dictionary<string, string> ctx)
    {
        if (!text.Contains("${")) return text;
        return Placeholder.Replace(text, m =>
            ctx.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }

    public static List<string> FindLeakedPlaceholders(IEnumerable<string> command) =>
        command.SelectMany(arg => Placeholder.Matches(arg).Select(m => m.Groups[1].Value))
               .Distinct()
               .ToList();
}

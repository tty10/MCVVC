using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class ManifestEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public DateTime ReleaseTime { get; set; }
}

public sealed class VersionManifest
{
    public string LatestRelease { get; set; } = "";
    public string LatestSnapshot { get; set; } = "";
    public List<ManifestEntry> Versions { get; set; } = new();
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public string? OsName { get; set; }
    public string? OsArch { get; set; }
    public Dictionary<string, bool> Features { get; set; } = new();
}

public sealed class LibraryDownload
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public List<Rule> Rules { get; set; } = new();
    public LibraryDownload? Artifact { get; set; }
    public Dictionary<string, LibraryDownload> Classifiers { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public string? NativesClassifier { get; set; }
}

/// <summary>
/// 版本 JSON 的只读包装。用 JsonObject 而不是强类型，因为 rules / arguments
/// 的结构在不同年代差异很大，强类型反而要写一堆兼容分支。
/// </summary>
public sealed class VersionDetail
{
    public JsonObject Root { get; }
    public string JsonPath { get; }
    public string Directory { get; }

    public VersionDetail(JsonObject root, string jsonPath)
    {
        Root = root;
        JsonPath = jsonPath;
        Directory = Path.GetDirectoryName(jsonPath) ?? "";
    }

    public string Id => Str("id");
    public string MainClass => Str("mainClass");
    public string Type => Str("type", "release");
    public string? InheritsFrom => StrOrNull("inheritsFrom");

    public int RequiredJava =>
        Root["javaVersion"] is JsonObject jv && jv["majorVersion"] is JsonValue v && v.TryGetValue<int>(out var major)
            ? major
            : 8;

    public string AssetIndexId => StrOrNull("assetIndex", "id") ?? "legacy";
    public string? AssetIndexUrl => StrOrNull("assetIndex", "url");
    public string? AssetIndexSha1 => StrOrNull("assetIndex", "sha1");

    public string? ClientUrl => StrOrNull("downloads", "client", "url");
    public string? ClientSha1 => StrOrNull("downloads", "client", "sha1");
    public long ClientSize =>
        Root["downloads"]?["client"]?["size"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    public DateTime ReleaseTime
    {
        get
        {
            var raw = StrOrNull("releaseTime");
            return DateTime.TryParse(raw, out var dt) ? dt : DateTime.MinValue;
        }
    }

    /// <summary>
    /// jar 字段可能指向别的版本（Forge/Fabric 的版本 JSON 就是这么干的），
    /// 这时要去那个版本的目录里拿 jar，而不是在当前版本目录找。
    /// </summary>
    public string ClientJarPath
    {
        get
        {
            var jarId = StrOrNull("jar");
            if (!string.IsNullOrEmpty(jarId) && jarId != Id)
            {
                var versionsRoot = Path.GetDirectoryName(Directory) ?? Directory;
                return Path.Combine(versionsRoot, jarId, jarId + ".jar");
            }
            return Path.Combine(Directory, Id + ".jar");
        }
    }
    public string NativesDirectory => Path.Combine(Directory, "natives");

    public List<Library> GetLibraries()
    {
        var result = new List<Library>();
        if (Root["libraries"] is not JsonArray array) return result;

        foreach (var node in array)
        {
            if (node is not JsonObject obj) continue;

            var lib = new Library { Name = StrOf(obj["name"]) };

            if (obj["rules"] is JsonArray rules)
            {
                foreach (var r in rules)
                {
                    if (r is not JsonObject ro) continue;
                    var rule = new Rule { Action = StrOf(ro["action"], "allow") };
                    if (ro["os"] is JsonObject os)
                    {
                        rule.OsName = StrOrNullOf(os["name"]);
                        rule.OsArch = StrOrNullOf(os["arch"]);
                    }
                    if (ro["features"] is JsonObject feats)
                    {
                        foreach (var kv in feats)
                        {
                            if (kv.Value is JsonValue fv && fv.TryGetValue<bool>(out var b))
                                rule.Features[kv.Key] = b;
                        }
                    }
                    lib.Rules.Add(rule);
                }
            }

            if (obj["downloads"] is JsonObject downloads)
            {
                if (downloads["artifact"] is JsonObject artifact)
                    lib.Artifact = ParseDownload(artifact);

                if (downloads["classifiers"] is JsonObject classifiers)
                {
                    foreach (var kv in classifiers)
                    {
                        if (kv.Value is JsonObject co)
                            lib.Classifiers[kv.Key] = ParseDownload(co);
                    }
                }
            }

            if (obj["extract"] is JsonObject extract && extract["exclude"] is JsonArray excludes)
            {
                foreach (var e in excludes)
                {
                    var s = StrOrNullOf(e);
                    if (!string.IsNullOrEmpty(s)) lib.Exclude.Add(s);
                }
            }
            if (lib.Exclude.Count == 0) lib.Exclude.Add("META-INF/");

            // 1.19 以前的原生库靠 natives 字段指向 classifier key
            if (obj["natives"] is JsonObject natives && natives[Platform.OsName] is JsonValue nv
                && nv.TryGetValue<string>(out var classifier))
            {
                lib.NativesClassifier = classifier.Replace("${arch}", Platform.ArchBits);
            }

            result.Add(lib);
        }
        return result;
    }

    private static LibraryDownload ParseDownload(JsonObject obj) => new()
    {
        Path = StrOf(obj["path"]),
        Url = StrOf(obj["url"]),
        Sha1 = StrOf(obj["sha1"]),
        Size = obj["size"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0,
    };

    private string Str(string key, string fallback = "")
    {
        var value = StrOrNull(key);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private string? StrOrNull(string key) => StrOrNullOf(Root[key]);

    private string? StrOrNull(string a, string b) =>
        Root[a] is JsonObject obj ? StrOrNullOf(obj[b]) : null;

    private string? StrOrNull(string a, string b, string c) =>
        Root[a] is JsonObject o1 && o1[b] is JsonObject o2 ? StrOrNullOf(o2[c]) : null;

    private static string StrOf(JsonNode? node, string fallback = "") => StrOrNullOf(node) ?? fallback;

    private static string? StrOrNullOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

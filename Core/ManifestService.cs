using System.Text.Json.Nodes;

namespace MclLauncher.Core;

/// <summary>
/// version_manifest_v2.json 的访问层。只在内存里缓存一次，启动器生命周期内不重复拉。
/// </summary>
public static class ManifestService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static VersionManifest? _cache;

    public static async Task<VersionManifest> GetAsync(bool forceReload = false, CancellationToken ct = default)
    {
        if (_cache != null && !forceReload) return _cache;

        await Gate.WaitAsync(ct);
        try
        {
            if (_cache != null && !forceReload) return _cache;

            var json = await Downloader.GetStringAsync(ManifestUrl, ct);
            _cache = Parse(json);
            return _cache;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static VersionManifest Parse(string json)
    {
        var result = new VersionManifest();
        if (JsonNode.Parse(json) is not JsonObject root) return result;

        if (root["latest"] is JsonObject latest)
        {
            result.LatestRelease = Str(latest, "release");
            result.LatestSnapshot = Str(latest, "snapshot");
        }

        if (root["versions"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject obj) continue;
                result.Versions.Add(new ManifestEntry
                {
                    Id = Str(obj, "id"),
                    Type = Str(obj, "type"),
                    Url = Str(obj, "url"),
                    Sha1 = Str(obj, "sha1"),
                    ReleaseTime = DateTime.TryParse(Str(obj, "releaseTime"), out var dt) ? dt : DateTime.MinValue,
                });
            }
        }

        return result;
    }

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

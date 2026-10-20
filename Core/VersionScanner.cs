using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class GamePaths
{
    public string Root { get; set; } = "";

    public string Libraries => Path.Combine(Root, "libraries");
    public string Assets => Path.Combine(Root, "assets");
    public string Versions => Path.Combine(Root, "versions");
    public string Runtime => Path.Combine(Root, "runtime");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public static GamePaths FromRoot(string root) => new() { Root = root };
}

public static class VersionScanner
{
    /// <summary>扫描 versions/*/&lt;id&gt;.json，读不出来的目录直接跳过。</summary>
    public static List<VersionDetail> Scan(string versionsDir)
    {
        var result = new List<VersionDetail>();
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var text = File.ReadAllText(jsonPath);
                if (JsonNode.Parse(text) is JsonObject root)
                    result.Add(new VersionDetail(root, jsonPath));
            }
            catch
            {
                // 损坏的版本 JSON 不应该让整个列表崩掉
            }
        }

        return result
            .OrderByDescending(v => v.ReleaseTime)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

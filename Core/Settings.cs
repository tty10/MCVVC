using System.Text.Json;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

/// <summary>本地设置。目前只存 CurseForge API key。</summary>
public static class Settings
{
    private static string FilePath => Path.Combine(GamePaths.DefaultRoot, "mcl_settings.json");

    private static string _curseForgeKey = Load();

    public static string CurseForgeKey
    {
        get => _curseForgeKey;
        set
        {
            _curseForgeKey = value?.Trim() ?? "";
            Save();
        }
    }

    public static bool HasCurseForgeKey => !string.IsNullOrWhiteSpace(_curseForgeKey);

    private static string Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            return node?["curseForgeKey"]?.GetValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static void Save()
    {
        try
        {
            var json = new JsonObject { ["curseForgeKey"] = _curseForgeKey }.ToJsonString();
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 存不下来不影响本次会话
        }
    }
}

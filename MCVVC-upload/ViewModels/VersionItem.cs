using MclLauncher.Core;

namespace MclLauncher.ViewModels;

public sealed class VersionItem
{
    public VersionItem(VersionDetail detail, GamePaths? paths = null)
    {
        Detail = detail;
        // Forge/Fabric 的 Java 需求在父版本里，要合并后才能读到
        RequiredJava = paths == null
            ? detail.RequiredJava
            : VersionResolver.Resolve(detail, paths).RequiredJava;
    }

    public VersionDetail Detail { get; }
    public int RequiredJava { get; }
    public string Id => Detail.Id;

    public string TypeLabel => Detail.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Detail.Type,
    };

    /// <summary>版本卡片左上角的字母图标。PCL2 用材质图标，这里用首字符代替。</summary>
    public string Initial => Id.Length > 0 ? Id[..1].ToUpperInvariant() : "?";

    public bool HasClientJar => File.Exists(Detail.ClientJarPath);

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { TypeLabel };
            if (!HasClientJar) parts.Add("缺少 jar");
            else if (RequiredJava > 8) parts.Add($"Java {RequiredJava}");
            return string.Join(" · ", parts);
        }
    }

    public string JavaHint => $"Java {RequiredJava}";
}

namespace MclLauncher.ViewModels;

/// <summary>
/// 整合包列表项。在线源只有 CurseForge，包一层供 UI 统一绑定。
/// </summary>
public sealed class ModpackItem
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Initial { get; init; } = "?";
    public string DownloadText { get; init; } = "";
    public string VersionText { get; init; } = "";
}

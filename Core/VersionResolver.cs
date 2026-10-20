using System.Text.Json.Nodes;

namespace MclLauncher.Core;

/// <summary>
/// 处理 inheritsFrom。Forge / Fabric / OptiFine 的版本 JSON 都不是完整的，
/// 它们指向原版，必须把父子合并后才能启动。不合并就是 NoClassDefFoundError。
/// </summary>
public static class VersionResolver
{
    public static VersionDetail Resolve(VersionDetail version, GamePaths paths)
    {
        if (string.IsNullOrEmpty(version.InheritsFrom)) return version;

        var chain = new List<VersionDetail> { version };
        var current = version;

        for (var depth = 0; depth < 8; depth++)
        {
            var parentId = current.InheritsFrom;
            if (string.IsNullOrEmpty(parentId)) break;

            var parentPath = Path.Combine(paths.Versions, parentId, parentId + ".json");
            if (!File.Exists(parentPath)) break;

            var parent = Load(parentPath);
            if (parent == null) break;

            chain.Add(parent);
            current = parent;
        }

        if (chain.Count < 2) return version;

        // 从最底层的原版开始，逐层往上覆盖
        var merged = (JsonObject)chain[^1].Root.DeepClone();
        for (var i = chain.Count - 2; i >= 0; i--)
            MergeInto(merged, chain[i].Root);

        // 注意：这里**不能**把 jar 指回父版本。
        // Forge 的 -DignoreList 里写的是 ${version_name}.jar，指回原版的话
        // 原版 jar 不在忽略列表里，会被当成两个模块重复加载 → module ResolutionException。
        // 由 ForgeService 把原版 jar 复制成 <version-id>.jar。
        return new VersionDetail(merged, version.JsonPath);
    }

    private static VersionDetail? Load(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                ? new VersionDetail(root, path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void MergeInto(JsonObject target, JsonObject child)
    {
        // libraries 子在前父在后：子版本可以覆盖父版本的库版本
        if (child["libraries"] is JsonArray childLibraries)
        {
            var combined = new JsonArray();
            foreach (var item in childLibraries) combined.Add(item?.DeepClone());
            if (target["libraries"] is JsonArray parentLibraries)
                foreach (var item in parentLibraries) combined.Add(item?.DeepClone());
            target["libraries"] = combined;
        }

        // arguments 同理，子在前父在后
        if (child["arguments"] is JsonObject childArguments)
        {
            var parentArguments = target["arguments"] as JsonObject;
            var result = new JsonObject();
            foreach (var section in new[] { "game", "jvm" })
            {
                var merged = new JsonArray();
                if (childArguments[section] is JsonArray childItems)
                    foreach (var item in childItems) merged.Add(item?.DeepClone());
                if (parentArguments?[section] is JsonArray parentItems)
                    foreach (var item in parentItems) merged.Add(item?.DeepClone());
                result[section] = merged;
            }
            target["arguments"] = result;
        }

        foreach (var pair in child)
        {
            if (pair.Key is "libraries" or "arguments") continue;
            target[pair.Key] = pair.Value?.DeepClone();
        }
    }
}

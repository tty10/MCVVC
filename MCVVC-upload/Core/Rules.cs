using System.Runtime.InteropServices;

namespace MclLauncher.Core;

public static class Platform
{
    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string ArchBits => Environment.Is64BitProcess ? "64" : "32";

    /// <summary>Mojang 的 arch 里 x86 指 32 位进程，不是 32 位机器。</summary>
    public static string JavaArch
    {
        get
        {
            if (!Environment.Is64BitProcess) return "x86";
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
        }
    }
}

public static class RuleEngine
{
    /// <summary>
    /// rules 是「顺序覆盖」语义，不是「或」语义：
    /// 从拒绝开始，逐条匹配，命中就把结果覆盖成该条的 action。
    /// 没有 rules 的条目默认放行。
    /// </summary>
    public static bool Allow(IReadOnlyList<Rule>? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules == null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Action is not ("allow" or "disallow")) continue;
            if (Matches(rule, features)) allowed = rule.Action == "allow";
        }
        return allowed;
    }

    private static bool Matches(Rule rule, IReadOnlyDictionary<string, bool>? features)
    {
        if (!string.IsNullOrEmpty(rule.OsName) && rule.OsName != Platform.OsName)
            return false;

        if (!string.IsNullOrEmpty(rule.OsArch))
        {
            var current = Platform.JavaArch;
            if (rule.OsArch == "x86")
            {
                if (current != "x86") return false;
            }
            else if (rule.OsArch != current)
            {
                return false;
            }
        }

        if (rule.Features.Count > 0)
        {
            foreach (var (key, want) in rule.Features)
            {
                var have = features != null && features.TryGetValue(key, out var b) && b;
                if (have != want) return false;
            }
        }
        return true;
    }
}

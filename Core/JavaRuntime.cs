using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MclLauncher.Core;

public sealed class JavaRuntime
{
    public string JavaExe { get; init; } = "";
    public string? JavaWExe { get; init; }
    public int MajorVersion { get; init; }
    public string Source { get; init; } = "";

    public string LaunchExe => JavaWExe ?? JavaExe;

    public override string ToString() => $"Java {MajorVersion} ({Source})";
}

public static class JavaRuntimeLocator
{
    private static readonly Regex VersionPattern = new(@"version ""(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    /// <summary>按优先级收集候选路径：游戏自带的 runtime → JAVA_HOME → PATH → 常见安装目录。</summary>
    public static List<string> CandidatePaths(GamePaths paths)
    {
        var list = new List<string>();

        if (Directory.Exists(paths.Runtime))
        {
            try
            {
                list.AddRange(Directory.GetFiles(paths.Runtime, "java.exe", SearchOption.AllDirectories));
            }
            catch { /* 权限问题就当没有 */ }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(exe)) list.Add(exe);
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim('"'), "java.exe");
                if (File.Exists(exe)) list.Add(exe);
            }
            catch { }
        }

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java",
                     @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft",
                     @"C:\Program Files\Zulu",
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe)) list.Add(exe);
                }
            }
            catch { }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static JavaRuntime? Detect(string javaExe, string source = "")
    {
        var major = GetMajorVersion(javaExe);
        if (major == null) return null;

        var dir = Path.GetDirectoryName(javaExe) ?? "";
        var javaw = Path.Combine(dir, "javaw.exe");
        return new JavaRuntime
        {
            JavaExe = javaExe,
            JavaWExe = File.Exists(javaw) ? javaw : null,
            MajorVersion = major.Value,
            Source = string.IsNullOrEmpty(source) ? dir : source,
        };
    }

    /// <summary>扫一遍所有候选，返回检测成功的运行时（已去重）。</summary>
    public static List<JavaRuntime> DetectAll(GamePaths paths)
    {
        var result = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exe in CandidatePaths(paths))
        {
            if (!seen.Add(exe)) continue;
            var runtime = Detect(exe);
            if (runtime != null) result.Add(runtime);
        }
        return result.OrderBy(r => r.MajorVersion).ToList();
    }

    /// <summary>挑一个满足要求的最低版本（避免拿 Java 21 跑 1.8.9 这种反直觉组合）。</summary>
    public static JavaRuntime? PickFor(IEnumerable<JavaRuntime> runtimes, int requiredMajor) =>
        runtimes.Where(r => r.MajorVersion >= requiredMajor)
                .OrderBy(r => r.MajorVersion)
                .FirstOrDefault();

    public static int? GetMajorVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var text = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(8000);

            var match = VersionPattern.Match(text);
            if (!match.Success) return null;

            var major = int.Parse(match.Groups[1].Value);
            if (major == 1 && match.Groups[2].Success)
                return int.Parse(match.Groups[2].Value);   // "1.8.0_461" -> 8
            return major;
        }
        catch
        {
            return null;
        }
    }
}

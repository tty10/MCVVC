using System.Diagnostics;
using System.Text;

namespace MclLauncher.Core;

public sealed class GameProcess : IDisposable
{
    private Process? _process;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public int? ProcessId => _process?.Id;

    public void Start(IReadOnlyList<string> command, string workingDirectory, string? logPath = null)
    {
        if (command.Count == 0) throw new ArgumentException("命令为空");
        Directory.CreateDirectory(workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ArgumentList 会自动给带空格的路径加引号，不用手工拼字符串
        for (var i = 1; i < command.Count; i++) psi.ArgumentList.Add(command[i]);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Emit(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(e.Data); };
        process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = process.ExitCode; } catch { }
            Exited?.Invoke(code);
        };

        if (!process.Start()) throw new InvalidOperationException("进程启动失败");

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!string.IsNullOrEmpty(logPath))
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {string.Join(' ', command)}";
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }
    }

    private void Emit(string line) => OutputReceived?.Invoke(line);

    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _process = null;
    }
}

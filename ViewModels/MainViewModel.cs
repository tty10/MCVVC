using System.Text;
using System.Windows;
using System.Windows.Input;
using MclLauncher.Core;

namespace MclLauncher.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly StringBuilder _log = new();
    private GameProcess? _game;
    private StoredAccount _account = AccountStore.Load() ?? AccountStore.CreateOffline("Player");

    private VersionItem? _selectedVersion;
    private JavaRuntime? _selectedJava;
    private int _memoryMb = 2048;
    private string _playerName = "Player";
    private string _statusText = "就绪";
    private string _logText = "";
    private bool _isLaunching;

    public MainViewModel()
    {
        Paths = GamePaths.FromRoot(GamePaths.DefaultRoot);

        Download = new DownloadViewModel(Paths, id => Refresh(id));

        RefreshCommand = new RelayCommand(() => Refresh());
        LaunchCommand = new RelayCommand(async () => await LaunchAsync(), () => !IsLaunching && SelectedVersion != null);
        StopCommand = new RelayCommand(Stop, () => IsLaunching);
        OpenGameFolderCommand = new RelayCommand(() => Shell(Paths.Root));
        OpenLogCommand = new RelayCommand(() => Shell(Path.Combine(Paths.Root, "logs")));
        LoginCommand = new RelayCommand(OpenLogin);

        Refresh();
    }

    public GamePaths Paths { get; }
    public DownloadViewModel Download { get; }
    public List<VersionItem> Versions { get; private set; } = new();
    public List<JavaRuntime> JavaRuntimes { get; private set; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenLogCommand { get; }

    public VersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetField(ref _selectedVersion, value)) return;
            OnPropertyChanged(nameof(SelectedVersionTitle));
            OnPropertyChanged(nameof(SelectedVersionMeta));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public JavaRuntime? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!SetField(ref _selectedJava, value)) return;
            OnPropertyChanged(nameof(JavaStatusText));
        }
    }

    public int MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (!SetField(ref _memoryMb, value)) return;
            OnPropertyChanged(nameof(MemoryText));
        }
    }

    public string PlayerName
    {
        get => _playerName;
        set => SetField(ref _playerName, value);
    }

    public StoredAccount Account
    {
        get => _account;
        private set
        {
            if (!SetField(ref _account, value)) return;
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountInitial));
            OnPropertyChanged(nameof(AccountBadge));
            OnPropertyChanged(nameof(AccountIsMsa));
        }
    }

    public string AccountName => Account.Name;
    public string AccountInitial => Account.Initial;
    public string AccountBadge => Account.IsMsa ? "微软正版" : "离线账号";
    public bool AccountIsMsa => Account.IsMsa;

    public string CurseForgeKey
    {
        get => Core.Settings.CurseForgeKey;
        set
        {
            Core.Settings.CurseForgeKey = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetField(ref _logText, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        set
        {
            if (!SetField(ref _isLaunching, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(CanStop));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanStop => IsLaunching;

    public string LaunchButtonText => IsLaunching ? "游戏中" : "启动游戏";

    public string MemoryText => $"{MemoryMb} MB";

    public string SelectedVersionTitle => SelectedVersion?.Id ?? "未选择版本";

    public string SelectedVersionMeta =>
        SelectedVersion == null
            ? "从左侧选择一个版本"
            : $"{SelectedVersion.TypeLabel} · {SelectedVersion.Detail.MainClass}";

    public string JavaStatusText => SelectedJava == null
        ? "未检测到可用 Java"
        : $"Java {SelectedJava.MajorVersion} · {SelectedJava.Source}";

    /// <summary>合并 inheritsFrom 之后的真实 Java 需求。</summary>
    public int ResolvedRequiredJava => SelectedVersion == null
        ? 8
        : VersionResolver.Resolve(SelectedVersion.Detail, Paths).RequiredJava;

    public string VersionCountText => $"共 {Versions.Count} 个版本";

    public void Refresh(string? selectId = null)
    {
        Versions = VersionScanner.Scan(Paths.Versions)
            .Select(v => new VersionItem(v, Paths))
            .ToList();
        OnPropertyChanged(nameof(Versions));
        OnPropertyChanged(nameof(VersionCountText));

        JavaRuntimes = JavaRuntimeLocator.DetectAll(Paths);
        OnPropertyChanged(nameof(JavaRuntimes));

        SelectedVersion = (selectId == null ? null : Versions.FirstOrDefault(v => v.Id == selectId))
                          ?? Versions.FirstOrDefault();

        if (SelectedVersion != null)
        {
            // Java 需求要按合并后的版本来算，否则 Forge 版本会误判成 Java 8
            var required = VersionResolver.Resolve(SelectedVersion.Detail, Paths).RequiredJava;
            var picked = JavaRuntimeLocator.PickFor(JavaRuntimes, required);
            SelectedJava = picked ?? JavaRuntimes.FirstOrDefault();
        }
        else
        {
            SelectedJava = JavaRuntimes.FirstOrDefault();
        }

        OnPropertyChanged(nameof(JavaStatusText));
        AppendLog($"扫描到 {Versions.Count} 个版本，{JavaRuntimes.Count} 个 Java 运行时");
    }

    /// <summary>只构建命令不执行，用来核对 classpath 和占位符。</summary>
    public List<string> PreviewCommand()
    {
        if (SelectedVersion == null)
            return new List<string> { "（未选择版本）" };

        // 和真实启动保持一致：Forge/Fabric 必须先合并父版本
        var version = VersionResolver.Resolve(SelectedVersion.Detail, Paths);
        var options = new LaunchOptions
        {
            PlayerName = Account.Name,
            Uuid = Account.Uuid,
            AccessToken = string.IsNullOrEmpty(Account.AccessToken) ? "0" : Account.AccessToken,
            UserType = Account.IsMsa ? "msa" : "legacy",
            Xuid = Account.Xuid,
            ClientId = Account.ClientId,
            MaxMemoryMb = MemoryMb,
            JavaPath = SelectedJava?.LaunchExe ?? "javaw.exe",
            VersionName = SelectedVersion.Id,
            VersionType = version.Type,
            GameDir = ResolveGameDir(SelectedVersion.Detail, Paths),
        };

        return GameBuilder.BuildCommand(version, Paths, options);
    }

    private async Task LaunchAsync()
    {
        if (SelectedVersion == null) return;

        var version = SelectedVersion.Detail;
        // Forge/Fabric 的版本 JSON 不完整，必须和原版合并后才能启动
        var resolved = VersionResolver.Resolve(version, Paths);

        if (!File.Exists(resolved.ClientJarPath))
        {
            StatusText = "缺少 client jar，无法启动";
            AppendLog($"× 找不到 {resolved.ClientJarPath}");
            return;
        }

        var required = version.RequiredJava;
        var java = SelectedJava;
        if (java == null || java.MajorVersion < required)
        {
            StatusText = $"需要 Java {required}";
            AppendLog($"× 当前 Java {(java?.MajorVersion.ToString() ?? "未找到")}，这个版本需要 {required}");
            return;
        }

        IsLaunching = true;
        StatusText = "正在构建启动命令…";

        try
        {
            await EnsureFreshTokenAsync();

            var options = new LaunchOptions
            {
                PlayerName = Account.Name,
                Uuid = Account.Uuid,
                AccessToken = string.IsNullOrEmpty(Account.AccessToken) ? "0" : Account.AccessToken,
                UserType = Account.IsMsa ? "msa" : "legacy",
                Xuid = Account.Xuid,
                ClientId = Account.ClientId,
                MaxMemoryMb = MemoryMb,
                JavaPath = java.LaunchExe,
                VersionName = version.Id,
                VersionType = version.Type,
                GameDir = ResolveGameDir(version, Paths),
            };

            var command = await Task.Run(() => GameBuilder.BuildCommand(resolved, Paths, options));

            var leaked = GameBuilder.FindLeakedPlaceholders(command);
            if (leaked.Count > 0)
                AppendLog($"! 未替换的占位符：{string.Join(", ", leaked)}");

            AppendLog($"启动 {version.Id}（Java {java.MajorVersion}，{MemoryMb} MB）");

            var process = new GameProcess();
            process.OutputReceived += OnGameOutput;
            process.Exited += OnGameExited;
            process.Start(command, Paths.Root, Path.Combine(Paths.Root, "launcher.log"));

            _game = process;
            StatusText = $"游戏运行中（PID {process.ProcessId}）";
        }
        catch (Exception ex)
        {
            IsLaunching = false;
            StatusText = "启动失败";
            AppendLog($"× {ex.Message}");
        }
    }

    private void Stop()
    {
        _game?.Kill();
        AppendLog("已请求结束游戏进程");
    }

    private void OnGameOutput(string line)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(line));
    }

    private void OnGameExited(int code)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsLaunching = false;
            StatusText = code == 0 ? "游戏已正常退出" : $"游戏退出，代码 {code}";
            AppendLog($"—— 进程结束，退出码 {code} ——");
            if (code != 0)
            {
                AppendLog("提示：日志里搜 Exception / Caused by，或看 crash-reports 目录");
            }
        });
    }

    public void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > 120_000) _log.Remove(0, 40_000);
        LogText = _log.ToString();
    }

    private void OpenLogin()
    {
        var window = new MclLauncher.LoginWindow { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() == true && window.Result != null)
        {
            Account = window.Result;
            PlayerName = window.Result.Name;
            StatusText = $"当前账号：{window.Result.Name}";
            AppendLog($"已切换账号：{window.Result.Display}");
        }
    }

    /// <summary>Minecraft 令牌 24 小时过期，启动前用 refresh_token 续一下。</summary>
    private async Task EnsureFreshTokenAsync()
    {
        if (!Account.IsMsa || !Account.IsExpired) return;

        if (string.IsNullOrEmpty(Account.RefreshToken) || string.IsNullOrEmpty(Account.ClientId))
        {
            AppendLog("! 令牌已过期，请重新登录");
            return;
        }

        AppendLog("令牌已过期，正在刷新…");
        try
        {
            var info = await MsaAuth.RefreshAsync(Account.RefreshToken, Account.ClientId);
            var updated = new StoredAccount
            {
                Type = "msa",
                Name = info.Name,
                Uuid = info.Uuid,
                AccessToken = info.AccessToken,
                RefreshToken = info.RefreshToken,
                Xuid = info.Xuid,
                ClientId = Account.ClientId,
                ExpiresAt = info.ExpiresAt,
            };
            AccountStore.Save(updated);
            Account = updated;
            AppendLog($"令牌已刷新（{info.Name}）");
        }
        catch (Exception ex)
        {
            AppendLog($"× 刷新失败：{ex.Message}，请在账号里重新登录");
        }
    }

    /// <summary>
    /// 版本隔离：版本目录里出现 mods / config / options.txt 就说明这是个独立实例
    /// （整合包），gameDir 指向它，避免不同整合包的 mods 互相污染。
    /// </summary>
    private static string ResolveGameDir(VersionDetail version, GamePaths paths)
    {
        var dir = version.Directory;
        if (Directory.Exists(Path.Combine(dir, "mods")) ||
            Directory.Exists(Path.Combine(dir, "config")) ||
            File.Exists(Path.Combine(dir, "options.txt")))
        {
            return dir;
        }
        return paths.Root;
    }

    private static void Shell(string path)
    {
        try
        {
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
        }
        catch { }
    }
}

using System.Windows.Input;
using System.Windows.Threading;
using MclLauncher.Core;

namespace MclLauncher.ViewModels;

public sealed class RemoteVersionItem
{
    public RemoteVersionItem(ManifestEntry entry, bool installed)
    {
        Entry = entry;
        Installed = installed;
    }

    public ManifestEntry Entry { get; }
    public bool Installed { get; set; }

    public string Id => Entry.Id;

    public string TypeLabel => Entry.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Entry.Type,
    };

    public string DateText => Entry.ReleaseTime == DateTime.MinValue
        ? ""
        : Entry.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");

    public string Initial => Id.Length > 0 ? Id[..1].ToUpperInvariant() : "?";

    public string Subtitle => Installed
        ? $"{TypeLabel} · 已安装"
        : $"{TypeLabel} · {DateText}";

    public string Badge => Installed ? "已装" : "";
}

public sealed class DownloadViewModel : ViewModelBase
{
    private readonly GamePaths _paths;
    private readonly Action<string?> _onInstalled;

    private List<RemoteVersionItem> _versions = new();
    private RemoteVersionItem? _selected;
    private bool _isBusy;
    private bool _showSnapshots;
    private double _percent;
    private string _stage = "进入下载页会自动刷新，也可点「刷新列表」手动刷新";
    private string _detail = "";
    private string _latestText = "";
    private string _selectedLoaderType = "原版";
    private List<string> _loaderVersions = new();
    private string? _selectedLoaderVersion;
    private string _browseMode = "version";
    private string _modpackQuery = "";
    private const int ModpackPageSize = 50;   // CurseForge /mods/search 单页上限，实测再大直接报错
    private List<ModpackItem> _modpackItems = new();
    private ModpackItem? _selectedModpackItem;
    private int _modpackIndex;        // 当前页起始 index（0 / 50 / 100…）
    private int _modpackTotal;        // CurseForge 侧总条数，用来算页数和判断翻页边界

    // ---------------- 自动刷新（PCL 同款：一进主页就在后台预加载，之后定时静默刷新） ----------------

    private const int AutoRefreshIntervalMinutes = 3;   // 后台定时刷新周期
    private const int AutoRefreshThrottleSeconds = 3;  // 同一来源 3 秒内的重复触发只放行一次
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastActivationUtc = DateTime.MinValue;
    private bool _preloadStarted;      // 启动/进主页后的预加载是否已触发（只跑一次）
    private bool _versionListLoaded;   // 版本列表是否已加载过（没加载过时点进下载页要立刻拉）
    private bool _modpackLoaded;       // 整合包列表是否已加载过（切 tab 免等）
    private bool _modpackLoading;      // 整合包列表是否正在拉（防止预加载和切 tab 并发拉两次）
    private bool _autoRefreshEnabled = true;
    private int _autoRefreshRemaining = AutoRefreshIntervalMinutes * 60;
    private bool _autoRefreshRunning;
    private string _autoRefreshStatusText = "";

    public DownloadViewModel(GamePaths paths, Action<string?> onInstalled)
    {
        _paths = paths;
        _onInstalled = onInstalled;

        LoadCommand = new RelayCommand(async () => await LoadAsync(), () => !IsBusy);
        InstallCommand = new RelayCommand(async () => await InstallAsync(), () => !IsBusy && Selected is { Installed: false });
        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => !IsBusy);
        SearchCommand = new RelayCommand(async () => await SearchModpacksAsync(), () => !IsBusy);
        PrevModpackPageCommand = new RelayCommand(async () => await PrevModpackPageAsync(),
            () => !IsBusy && HasPrevPage);
        NextModpackPageCommand = new RelayCommand(async () => await NextModpackPageAsync(),
            () => !IsBusy && HasNextPage);
        InstallModpackCommand = new RelayCommand(async () => await InstallModpackAsync(),
            () => !IsBusy && SelectedModpackItem != null);

        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _autoRefreshTimer.Start();
        UpdateAutoRefreshStatusText();
    }

    public ICommand LoadCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PrevModpackPageCommand { get; }
    public ICommand NextModpackPageCommand { get; }
    public ICommand InstallModpackCommand { get; }

    public List<RemoteVersionItem> Versions
    {
        get => _versions;
        private set
        {
            if (!SetField(ref _versions, value)) return;
            OnPropertyChanged(nameof(VersionCountText));
        }
    }

    public RemoteVersionItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedText));
            _ = ReloadLoaderVersionsAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsBusy;

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (!SetField(ref _showSnapshots, value)) return;
            ApplyFilter();
        }
    }

    public double Percent
    {
        get => _percent;
        private set => SetField(ref _percent, value);
    }

    public string Stage
    {
        get => _stage;
        private set => SetField(ref _stage, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    public string LatestText
    {
        get => _latestText;
        private set => SetField(ref _latestText, value);
    }

    public string VersionCountText => Versions.Count == 0 ? "" : $"共 {Versions.Count} 个";

    public string SelectedText => Selected == null
        ? "未选择版本"
        : $"{Selected.Id}  ·  {Selected.TypeLabel}  ·  {Selected.DateText}";

    // ---------------- 加载器（原版 / Fabric / Forge） ----------------

    public List<string> LoaderTypes { get; } = new() { "原版", "Fabric", "Forge" };

    public string SelectedLoaderType
    {
        get => _selectedLoaderType;
        set
        {
            if (!SetField(ref _selectedLoaderType, value)) return;
            OnPropertyChanged(nameof(IsLoaderMode));
            OnPropertyChanged(nameof(InstallButtonText));
            _ = ReloadLoaderVersionsAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsLoaderMode => SelectedLoaderType != "原版";

    public List<string> LoaderVersions
    {
        get => _loaderVersions;
        private set => SetField(ref _loaderVersions, value);
    }

    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set => SetField(ref _selectedLoaderVersion, value);
    }

    public string InstallButtonText => SelectedLoaderType switch
    {
        "Fabric" => "安装 Fabric",
        "Forge" => "安装 Forge",
        _ => "下载并安装",
    };

    private async Task ReloadLoaderVersionsAsync()
    {
        if (Selected == null || SelectedLoaderType == "原版")
        {
            LoaderVersions = new List<string>();
            SelectedLoaderVersion = null;
            return;
        }

        var type = SelectedLoaderType;
        var mcVersion = Selected.Id;

        try
        {
            if (type == "Fabric")
            {
                var list = await FabricService.GetLoaderVersionsAsync(mcVersion);
                LoaderVersions = list.Select(x => x.Version).ToList();
            }
            else
            {
                var builds = await ForgeService.GetBuildsAsync(mcVersion);
                LoaderVersions = builds.Select(b => b.Version).ToList();
            }
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoaderVersions = new List<string>();
            SelectedLoaderVersion = null;
            Stage = $"{type} 版本列表拉取失败：{ex.Message}";
        }
    }

    // ---------------- 在线整合包浏览（CurseForge） ----------------

    public bool IsVersionMode
    {
        get => _browseMode == "version";
        set { if (value) SetBrowseMode("version"); }
    }

    public bool IsModpackMode
    {
        get => _browseMode == "modpack";
        set { if (value) SetBrowseMode("modpack"); }
    }

    private void SetBrowseMode(string mode)
    {
        if (_browseMode == mode) return;
        _browseMode = mode;
        OnPropertyChanged(nameof(IsVersionMode));
        OnPropertyChanged(nameof(IsModpackMode));
        OnPropertyChanged(nameof(SelectedListText));
        CommandManager.InvalidateRequerySuggested();

        // 切到在线整合包页：已加载过就直接显示（预加载保证点开即见），没有才立刻拉
        if (mode == "modpack" && !_modpackLoaded && !_modpackLoading)
            _ = SearchModpacksAsync();
    }

    public string ModpackQuery
    {
        get => _modpackQuery;
        set => SetField(ref _modpackQuery, value);
    }

    public string SourceBadgeText => "CurseForge · 官方 API";

    /// <summary>CurseForge 整合包总量上万，单页上限 50，只能按 index 一页页翻。</summary>
    public bool HasPrevPage => _modpackIndex > 0;

    public bool HasNextPage => _modpackTotal > 0 && _modpackIndex + ModpackItems.Count < _modpackTotal;

    public string ModpackPageText
    {
        get
        {
            if (_modpackTotal <= 0) return "";
            var page = _modpackIndex / ModpackPageSize + 1;
            var pages = (_modpackTotal + ModpackPageSize - 1) / ModpackPageSize;
            return $"第 {page} / {pages} 页";
        }
    }

    public List<ModpackItem> ModpackItems
    {
        get => _modpackItems;
        private set => SetField(ref _modpackItems, value);
    }

    public ModpackItem? SelectedModpackItem
    {
        get => _selectedModpackItem;
        set
        {
            if (!SetField(ref _selectedModpackItem, value)) return;
            OnPropertyChanged(nameof(ModpackSelectedText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ModpackSelectedText => SelectedModpackItem == null
        ? "未选择整合包"
        : $"{SelectedModpackItem.Title} · {SelectedModpackItem.VersionText} · {SelectedModpackItem.DownloadText}";

    public string SelectedListText => IsModpackMode
        ? (SelectedModpackItem?.Title ?? "未选择整合包")
        : (Selected?.Id ?? "未选择版本");

    public string SelectedListSub => IsModpackMode
        ? ModpackSelectedText
        : Selected == null ? "" : $"{Selected.TypeLabel} · {Selected.DateText}";

    public async Task SearchModpacksAsync()
    {
        IsBusy = true;
        _modpackLoading = true;
        Percent = 0;

        // 在线整合包只有 CurseForge 一个源，必须有 key，没有就直接提示
        if (!Core.Settings.HasCurseForgeKey)
        {
            Stage = "请先在设置里填写 CurseForge API key（在线整合包仅支持 CurseForge）";
            ModpackItems = new List<ModpackItem>();
            SelectedModpackItem = null;
            _modpackIndex = 0;
            _modpackTotal = 0;
            RefreshModpackPager();
            IsBusy = false;
            _modpackLoading = false;
            return;
        }

        Stage = "正在从 CurseForge 拉取整合包列表…";

        try
        {
            await LoadModpackPageAsync(0);
            _modpackLoaded = true;   // 之后切 tab / 点进下载页直接可见
        }
        catch (Exception ex)
        {
            // 唯一来源，失败就如实报错，不再静默换平台
            ModpackItems = new List<ModpackItem>();
            SelectedModpackItem = null;
            _modpackIndex = 0;
            _modpackTotal = 0;
            Stage = $"CurseForge 拉取失败：{ex.Message}";
            RefreshModpackPager();
        }
        finally
        {
            IsBusy = false;
            _modpackLoading = false;
        }
    }

    /// <summary>
    /// 加载指定页并**替换**列表内容（翻页，不是追加）。
    /// CurseForge 单页上限 50，总量上万，只能按 index 一页一页翻。
    /// </summary>
    private async Task LoadModpackPageAsync(int index, bool silent = false)
    {
        var query = string.IsNullOrWhiteSpace(ModpackQuery) ? null : ModpackQuery.Trim();

        var result = await Core.CurseForgeApi.SearchModpacksAsync(
            Core.Settings.CurseForgeKey, query, index: index);

        _modpackIndex = index;
        _modpackTotal = result.TotalCount;

        ModpackItems = result.Items.Select(p => new ModpackItem
        {
            ProjectId = p.Id.ToString(),
            Title = p.Name,
            Description = p.Summary,
            Initial = p.Initial,
            DownloadText = p.DownloadText,
        }).ToList();

        SelectedModpackItem = ModpackItems.FirstOrDefault();
        Stage = silent
            ? $"共 {_modpackTotal} 个整合包 · {DateTime.Now:HH:mm:ss} 已刷新"
            : $"共 {_modpackTotal} 个整合包";
        RefreshModpackPager();
    }

    private async Task PrevModpackPageAsync()
    {
        if (IsBusy || !HasPrevPage) return;

        IsBusy = true;
        Stage = "正在加载上一页…";
        try
        {
            await LoadModpackPageAsync(Math.Max(0, _modpackIndex - ModpackPageSize));
        }
        catch (Exception ex)
        {
            Stage = $"翻页失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task NextModpackPageAsync()
    {
        if (IsBusy || !HasNextPage) return;

        IsBusy = true;
        Stage = "正在加载下一页…";
        try
        {
            await LoadModpackPageAsync(_modpackIndex + ModpackPageSize);
        }
        catch (Exception ex)
        {
            Stage = $"翻页失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>页码相关属性统一在这里发通知，避免漏掉哪个。</summary>
    private void RefreshModpackPager()
    {
        OnPropertyChanged(nameof(HasPrevPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(ModpackPageText));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------- 自动刷新 ----------------

    /// <summary>
    /// 自动刷新开关，默认开启（PCL 行为）：
    /// 进入下载页立即拉一次当前列表，离开后暂停；开启期间每 3 分钟在后台静默刷新，不打断操作。
    /// </summary>
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (!SetField(ref _autoRefreshEnabled, value)) return;
            _autoRefreshRemaining = AutoRefreshIntervalMinutes * 60;
            _autoRefreshTimer.IsEnabled = value;
            UpdateAutoRefreshStatusText();
        }
    }

    public string AutoRefreshStatusText
    {
        get => _autoRefreshStatusText;
        private set => SetField(ref _autoRefreshStatusText, value);
    }

    /// <summary>
    /// 页面变为可见时调用（MainWindow 导航钩子）。
    /// 不管在主页还是设置页都先把下载页数据在后台备好，用户点进下载页 / 整合包直接见货。
    /// </summary>
    public void OnActivated(string page)
    {
        // RadioButton / 窗口加载可能连抛多个激活事件，节流成一次
        var now = DateTime.UtcNow;
        if ((now - _lastActivationUtc).TotalSeconds < AutoRefreshThrottleSeconds)
        {
            UpdateAutoRefreshStatusText();
            return;
        }
        _lastActivationUtc = now;
        _autoRefreshRemaining = AutoRefreshIntervalMinutes * 60;

        // 启动 / 进主页即开始后台预加载：版本列表 + 整合包第一页
        if (!_preloadStarted) StartPreload();

        // 点进下载页：列表没加载过就立刻拉（非静默，让用户看到在动）；预加载进行中就交给它，别并发拉两次
        if (page == "download")
        {
            if (IsModpackMode)
            {
                if (!_modpackLoaded && !_modpackLoading)
                    _ = SearchModpacksAsync();
            }
            else if (!_versionListLoaded && !_autoRefreshRunning)
            {
                _ = LoadAsync(silent: false);
            }
        }

        UpdateAutoRefreshStatusText();
    }

    /// <summary>启动后只做一次的预加载：版本列表 + 整合包第一页（有 key 才拉）。</summary>
    private void StartPreload()
    {
        _preloadStarted = true;

        _ = RefreshNowAsync(silent: true);   // 预拉版本列表（静默，不打扰主页）

        if (Core.Settings.HasCurseForgeKey && !_modpackLoaded && !_modpackLoading)
            _ = PreloadModpacksAsync();
    }

    private async Task PreloadModpacksAsync()
    {
        _modpackLoading = true;
        try
        {
            await LoadModpackPageAsync(0, silent: true);
            _modpackLoaded = true;
        }
        catch
        {
            // 预加载失败不打扰；切到整合包 tab 时会重试
        }
        finally
        {
            _modpackLoading = false;
        }
    }

    /// <summary>立即刷新当前正在浏览的列表。silent = true 时不打扰 Stage（后台定时刷新用）。</summary>
    private async Task RefreshNowAsync(bool silent)
    {
        // 正在下载 / 翻页 / 安装 / 上一次刷新还没完时让路，下一轮定时会补上
        if (_autoRefreshRunning || IsBusy) return;

        _autoRefreshRunning = true;
        IsBusy = true;   // 挡住用户翻页 / 搜索，避免和自动刷新抢列表
        try
        {
            if (IsModpackMode)
            {
                if (!Core.Settings.HasCurseForgeKey)
                {
                    if (!silent)
                    {
                        Stage = "请先在设置里填写 CurseForge API key（在线整合包仅支持 CurseForge）";
                        ModpackItems = new List<ModpackItem>();
                        RefreshModpackPager();
                    }
                    return;
                }

                // 停留在用户当前所在页刷新，不把人冲回第一页
                var index = _modpackItems.Count > 0 ? _modpackIndex : 0;
                await LoadModpackPageAsync(index, silent);
            }
            else
            {
                await LoadAsync(silent);
            }
        }
        catch
        {
            // LoadAsync / LoadModpackPageAsync 内部已各自消化异常，这里只是兜底
            if (!silent) Stage = "刷新失败";
        }
        finally
        {
            IsBusy = false;
            _autoRefreshRunning = false;
            UpdateAutoRefreshStatusText();
        }
    }

    private void AutoRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_autoRefreshEnabled)
        {
            UpdateAutoRefreshStatusText();
            return;
        }

        if (_autoRefreshRemaining > 0) _autoRefreshRemaining--;
        if (_autoRefreshRemaining <= 0)
        {
            _autoRefreshRemaining = AutoRefreshIntervalMinutes * 60;
            _ = RefreshNowAsync(silent: true);
        }

        UpdateAutoRefreshStatusText();
    }

    private void UpdateAutoRefreshStatusText()
    {
        if (!_autoRefreshEnabled)
        {
            AutoRefreshStatusText = "已关闭";
            return;
        }

        var left = TimeSpan.FromSeconds(Math.Max(0, _autoRefreshRemaining));
        AutoRefreshStatusText = $"{(int)left.TotalMinutes}:{left.Seconds:D2} 后刷新";
    }

    private async Task InstallModpackAsync()
    {
        if (SelectedModpackItem == null) return;

        IsBusy = true;
        Percent = 0;
        Detail = "";

        var progress = new Progress<InstallProgress>(p =>
        {
            Percent = p.Percent;
            Stage = p.Stage;
            Detail = p.Detail;
        });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var temp = Path.Combine(Path.GetTempPath(), $"mcl-modpack-{tag}.zip");

        try
        {
            Stage = "查找整合包文件…";

            // 唯一来源 CurseForge：官方 API 查文件，downloadUrl 指向 edge.forgecdn.net。
            // 下载统一带 x-api-key（归属统计 + 官方已公告的强制鉴权）。
            var projectId = int.Parse(SelectedModpackItem.ProjectId);
            var cfFiles = await Core.CurseForgeApi.GetFilesAsync(Core.Settings.CurseForgeKey, projectId);
            var cfFile = cfFiles.FirstOrDefault()
                         ?? throw new InvalidOperationException("CurseForge 上没找到可下载文件");

            var fileUrl = cfFile.DownloadUrl;
            var fileName = cfFile.FileName;
            var fileSize = cfFile.FileLength;

            Stage = $"下载 {fileName}…";
            await Downloader.DownloadAsync(new DownloadTask
            {
                Url = fileUrl,
                Destination = temp,
                Size = fileSize,
                Label = fileName,
                Headers = Core.CurseForgeService.AuthHeaders(),
            }, null, null, CancellationToken.None);

            var instanceId = await ModpackInstaller.InstallAsync(temp, _paths, progress);
            _onInstalled?.Invoke(instanceId);

            Stage = $"{SelectedModpackItem.Title} 已安装：{instanceId}";
            Percent = 100;
        }
        catch (Exception ex)
        {
            Stage = "安装失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private async Task EnsureVanillaAsync(string mcVersion, IProgress<InstallProgress> progress)
    {
        var jsonPath = Path.Combine(_paths.Versions, mcVersion, mcVersion + ".json");
        if (File.Exists(jsonPath)) return;

        var manifest = await ManifestService.GetAsync();
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == mcVersion)
                    ?? throw new InvalidDataException($"找不到原版版本 {mcVersion}");
        await Installer.InstallAsync(entry, _paths, progress);
    }

    private List<RemoteVersionItem> _all = new();

    public async Task LoadAsync(bool silent = false)
    {
        IsBusy = true;
        if (!silent) Stage = "正在从 Mojang 拉取版本列表…";
        Percent = 0;

        try
        {
            var manifest = await ManifestService.GetAsync(forceReload: true);

            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_paths.Versions))
            {
                foreach (var dir in Directory.GetDirectories(_paths.Versions))
                {
                    var name = Path.GetFileName(dir);
                    if (File.Exists(Path.Combine(dir, name + ".json")))
                        installed.Add(name);
                }
            }

            _all = manifest.Versions
                .Select(v => new RemoteVersionItem(v, installed.Contains(v.Id)))
                .ToList();

            LatestText = $"最新正式版 {manifest.LatestRelease}　最新快照 {manifest.LatestSnapshot}";
            ApplyFilter();
            _versionListLoaded = true;   // 预加载成功，点进下载页即可直接看
            if (silent)
                Stage = $"已自动刷新 {DateTime.Now:HH:mm:ss}";
            else
                Stage = "选一个版本，点「下载并安装」";
        }
        catch (Exception ex)
        {
            Stage = silent ? $"自动刷新失败：{ex.Message}" : "拉取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Versions = ShowSnapshots
            ? _all
            : _all.Where(v => v.Entry.Type == "release").ToList();

        Selected = Versions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedText));
    }

    private async Task InstallAsync()
    {
        if (Selected == null) return;

        IsBusy = true;
        Percent = 0;
        Detail = "";

        var progress = new Progress<InstallProgress>(p =>
        {
            Percent = p.Percent;
            Stage = p.Stage;
            Detail = p.Detail;
        });

        try
        {
            var mcVersion = Selected.Id;

            switch (SelectedLoaderType)
            {
                case "Fabric":
                {
                    await EnsureVanillaAsync(mcVersion, progress);
                    var loader = SelectedLoaderVersion
                                 ?? await FabricService.GetLatestLoaderAsync(mcVersion);
                    var id = await FabricService.InstallAsync(mcVersion, loader, _paths, progress);
                    _onInstalled?.Invoke(id);
                    Stage = $"Fabric {loader} 安装完成，可以回主页启动";
                    break;
                }
                case "Forge":
                {
                    if (string.IsNullOrEmpty(SelectedLoaderVersion))
                        throw new InvalidOperationException("还没选 Forge 版本");

                    var id = await ForgeService.InstallAsync(
                        mcVersion, SelectedLoaderVersion, _paths, null, progress);
                    await ForgeService.EnsureLibrariesAsync(id, _paths, progress);
                    _onInstalled?.Invoke(id);
                    Stage = $"Forge {SelectedLoaderVersion} 安装完成，可以回主页启动";
                    break;
                }
                default:
                {
                    var target = Selected;
                    await Installer.InstallAsync(target.Entry, _paths, progress);
                    target.Installed = true;
                    OnPropertyChanged(nameof(Versions));
                    _onInstalled?.Invoke(target.Id);
                    Stage = $"{target.Id} 安装完成，可以回主页启动了";
                    break;
                }
            }

            Percent = 100;
        }
        catch (Exception ex)
        {
            Stage = "安装失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 CurseForge 整合包",
            Filter = "CurseForge 整合包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Percent = 0;
        Detail = "";

        var progress = new Progress<InstallProgress>(p =>
        {
            Percent = p.Percent;
            Stage = p.Stage;
            Detail = p.Detail;
        });

        try
        {
            var instanceId = await ModpackInstaller.InstallAsync(dialog.FileName, _paths, progress);
            _onInstalled?.Invoke(instanceId);
            Stage = $"整合包已安装：{instanceId}";
            Percent = 100;
        }
        catch (NotSupportedException ex)
        {
            Stage = ex.Message;
        }
        catch (Exception ex)
        {
            Stage = "导入失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

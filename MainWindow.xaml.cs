using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MclLauncher.ViewModels;

namespace MclLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // 日志一更新就滚到底，不然用户得自己拖
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText))
                LogBox.ScrollToEnd();
        };
    }

    /// <summary>
    /// 带 --shot 参数启动时，把窗口渲染成 PNG 后退出。开发期用来做 UI 回归，
    /// 也方便在没有截图工具的环境里确认界面。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        var args = Environment.GetCommandLineArgs();

        var installIndex = Array.IndexOf(args, "--install");
        if (installIndex >= 0 && installIndex + 1 < args.Length)
        {
            RunInstall(args[installIndex + 1]);
            return;
        }

        var cfReadIndex = Array.IndexOf(args, "--cf-read");
        if (cfReadIndex >= 0 && cfReadIndex + 1 < args.Length)
        {
            _ = Task.Run(() =>
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "cfread.txt");
                try
                {
                    var m = Core.ModpackInstaller.Read(args[cfReadIndex + 1]);
                    File.WriteAllText(logPath,
                        $"name={m.Name}\nversion={m.VersionId}\n" +
                        $"mc={m.MinecraftVersion}\nloader={m.Loader} {m.LoaderVersion}\n" +
                        $"files={m.Files.Count}\n" +
                        string.Join("\n", m.Files.Select(f => $"  project={f.ProjectId} file={f.FileId}")));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(logPath, "ERROR " + ex.Message);
                }
                finally
                {
                    Dispatcher.Invoke(Close);
                }
            });
            return;
        }

        var cfFileIndex = Array.IndexOf(args, "--cf-file");
        if (cfFileIndex >= 0 && cfFileIndex + 2 < args.Length)
        {
            _ = Task.Run(async () =>
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "cffile.txt");
                try
                {
                    var projectId = int.Parse(args[cfFileIndex + 1]);
                    var fileId = int.Parse(args[cfFileIndex + 2]);
                    var file = await Core.CurseForgeApi.GetFileByIdAsync(
                        Core.Settings.CurseForgeKey, projectId, fileId);

                    File.WriteAllText(logPath, file == null
                        ? "NOT FOUND"
                        : $"{file.FileName}\n{file.FileLength}\n{file.DownloadUrl}");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(logPath, "ERROR " + ex.Message);
                }
                finally
                {
                    Dispatcher.Invoke(Close);
                }
            });
            return;
        }

        var cfIndex = Array.IndexOf(args, "--cf-url");
        if (cfIndex >= 0 && cfIndex + 2 < args.Length)
        {
            _ = Task.Run(async () =>
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "cfurl.txt");
                try
                {
                    var projectId = int.Parse(args[cfIndex + 1]);
                    var fileId = int.Parse(args[cfIndex + 2]);
                    var file = await Core.CurseForgeService.GetFileAsync(projectId, fileId);
                    File.WriteAllText(logPath, file == null
                        ? "NOT FOUND"
                        : $"{file.Name}\n{file.Size}\n{Core.CurseForgeService.DirectUrl(file.Id, file.Name)}");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(logPath, "ERROR " + ex);
                }
                finally
                {
                    Dispatcher.Invoke(Close);
                }
            });
            return;
        }

        if (args.Contains("--dry-run"))
        {
            // --dry-run <版本号> 可以指定要检查的版本
            var index = Array.IndexOf(args, "--dry-run");
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                _vm.Refresh(args[index + 1]);

            var command = _vm.PreviewCommand();
            var leaked = Core.GameBuilder.FindLeakedPlaceholders(command);
            var report = new List<string>
            {
                $"版本: {_vm.SelectedVersionTitle}",
                $"Java: {_vm.SelectedJava}",
                $"需要 Java: {_vm.ResolvedRequiredJava}",
                $"未替换占位符: {(leaked.Count == 0 ? "无" : string.Join(", ", leaked))}",
                $"参数个数: {command.Count}",
                "",
                "命令行:",
            };
            report.AddRange(command.Select(a => "  " + a));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dryrun.txt"),
                string.Join(Environment.NewLine, report));
            Close();
            return;
        }

        if (!args.Contains("--shot")) return;

        if (args.Contains("--login"))
        {
            var login = new LoginWindow { Owner = this };
            login.Show();
            return;
        }

        if (args.Contains("--settings")) NavSettings.IsChecked = true;

        if (args.Contains("--modpacks"))
        {
            NavDownload.IsChecked = true;
            _vm.Download.IsModpackMode = true;
        }
        else if (args.Contains("--download"))
        {
            // 勾选导航即触发 OnActivated → 自动拉取版本列表，不用再手动调 LoadAsync
            NavDownload.IsChecked = true;
        }

        // --preload：验证"启动停主页 → 稍后点进下载页"时序下，点进去时列表是否已就绪
        if (args.Contains("--preload"))
        {
            var preloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4500) };
            preloadTimer.Tick += (_, _) =>
            {
                preloadTimer.Stop();
                NavDownload.IsChecked = true;   // 相当于用户此时才点进「下载」
            };
            preloadTimer.Start();
        }

        // 拉列表需要时间，带页面参数时多等一会儿
        var delay = args.Contains("--preload") ? 9000
            : args.Contains("--download") || args.Contains("--modpacks") ? 4500 : 1400;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { SaveScreenshot(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            Close();
        };
        timer.Start();
    }

    /// <summary>CLI 安装模式：--install &lt;版本号&gt;，进度写 install.log。</summary>
    private void RunInstall(string versionId)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "install.log");
        File.WriteAllText(logPath, $"install {versionId}  {DateTime.Now:HH:mm:ss}{Environment.NewLine}");

        _ = Task.Run(async () =>
        {
            var lastStage = "";
            try
            {
                var manifest = await Core.ManifestService.GetAsync();
                var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                            ?? throw new InvalidOperationException($"没有版本 {versionId}");

                var progress = new Progress<Core.InstallProgress>(p =>
                {
                    if (p.Stage == lastStage) return;
                    lastStage = p.Stage;
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] {p.Stage} (共 {p.Total}){Environment.NewLine}");
                });

                var version = await Core.Installer.InstallAsync(entry, _vm.Paths, progress);
                File.AppendAllText(logPath,
                    $"DONE {version.Id}  {DateTime.Now:HH:mm:ss}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, "ERROR " + ex + Environment.NewLine);
            }
            finally
            {
                Dispatcher.Invoke(Close);
            }
        });
    }

    private void SaveScreenshot() => ScreenshotHelper.Save(this, "shot.png");

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 拖动过程中窗口状态变化会抛，忽略 */ }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" 在 InitializeComponent 阶段就会触发，此时页面还没实例化
        if (PageHome == null || PageDownload == null || PageSettings == null) return;

        PageHome.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDownload.Visibility = NavDownload.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // 自动刷新：不管停在哪一页都后台预加载/刷新下载页数据，点进下载页直接见货
        var active = PageDownload.Visibility == Visibility.Visible ? "download"
            : PageHome.Visibility == Visibility.Visible ? "home"
            : "settings";
        _vm.Download.OnActivated(active);
    }
}

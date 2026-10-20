using System.Windows;
using System.Windows.Input;
using MclLauncher.Core;

namespace MclLauncher;

public partial class LoginWindow : Window
{
    private CancellationTokenSource? _cts;
    private string _verificationUri = "";
    private string _userCode = "";

    public StoredAccount? Result { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();

        var current = AccountStore.Load();
        OfflineName.Text = current?.Name ?? "Player";
        ClientIdBox.Text = current?.ClientId ?? "";

        // 微软账号登录过就直接落在微软 tab
        if (current is { IsMsa: true }) TabMsa.IsChecked = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--shot")) return;

        if (args.Contains("--msa")) TabMsa.IsChecked = true;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { ScreenshotHelper.Save(this, args.Contains("--msa") ? "shot-login-msa.png" : "shot-login.png"); }
            catch { }
            Close();
            Application.Current?.Shutdown();
        };
        timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelOffline == null || PanelMsa == null) return;
        var offline = TabOffline.IsChecked == true;
        PanelOffline.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        PanelMsa.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "先填一个玩家名称。", "登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var account = AccountStore.CreateOffline(name);
        // 保留已填的 client_id，下次切到微软登录不用重填
        account.ClientId = ClientIdBox.Text.Trim();
        AccountStore.Save(account);

        Result = account;
        DialogResult = true;
        Close();
    }

    private async void StartLogin_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            MsaStatus.Text = "先填 client_id，否则微软不会认这个请求。";
            return;
        }

        StartButton.IsEnabled = false;
        MsaStatus.Text = "正在向微软申请设备码…";

        try
        {
            var device = await MsaAuth.BeginDeviceCodeAsync(clientId);

            _verificationUri = device.VerificationUri;
            _userCode = device.UserCode;
            UserCodeText.Text = device.UserCode;
            CodePanel.Visibility = Visibility.Visible;
            MsaStatus.Text = "浏览器已打开，输入上面的代码并完成登录。";

            TryOpenBrowser();

            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(text => MsaStatus.Text = text);

            var info = await MsaAuth.CompleteAsync(device, clientId, progress, _cts.Token);
            SaveMsaAccount(info, clientId);
        }
        catch (OperationCanceledException)
        {
            MsaStatus.Text = "已取消。";
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LogAuthError(ex);
            MsaStatus.Text = ex.Message;
            StartButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 微软正版网页登录：默认浏览器打开微软登录页，用户直接输邮箱 + 密码，
    /// 授权码经本地回环地址回传（微软已禁用内嵌 WebView 跑 OAuth，这是桌面端唯一方式）。
    /// </summary>
    private async void WebLogin_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            MsaStatus.Text = "先填 client_id，否则微软不会认这个请求。";
            return;
        }

        WebLoginButton.IsEnabled = false;
        CodePanel.Visibility = Visibility.Collapsed;   // 网页登录不需要设备码面板
        MsaStatus.Text = "正在打开微软登录页…";

        try
        {
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(text => MsaStatus.Text = text);

            var info = await MsaAuth.InteractiveLoginAsync(clientId, progress, _cts.Token);
            SaveMsaAccount(info, clientId);
        }
        catch (OperationCanceledException)
        {
            MsaStatus.Text = "已取消。";
            WebLoginButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LogAuthError(ex);
            MsaStatus.Text = ex.Message;
            WebLoginButton.IsEnabled = true;
        }
    }

    /// <summary>两条微软登录路径（设备码 / 网页授权码）的公共收尾：存账号、关窗口。</summary>
    private void SaveMsaAccount(AccountInfo info, string clientId)
    {
        var account = new StoredAccount
        {
            Type = "msa",
            Name = info.Name,
            Uuid = info.Uuid,
            AccessToken = info.AccessToken,
            RefreshToken = info.RefreshToken,
            Xuid = info.Xuid,
            ClientId = clientId,
            ExpiresAt = info.ExpiresAt,
        };
        AccountStore.Save(account);

        Result = account;
        MsaStatus.Text = $"登录成功：{info.Name}";
        DialogResult = true;
        Close();
    }

    /// <summary>登录失败时把完整原因写进 mcl_auth.log，界面上放不下时靠它排查。</summary>
    private static void LogAuthError(Exception ex)
    {
        try
        {
            var path = Path.Combine(GamePaths.DefaultRoot, "mcl_auth.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不影响主流程
        }
    }

    private void TryOpenBrowser()
    {
        if (string.IsNullOrEmpty(_verificationUri)) return;
        OpenUrl(_verificationUri);
    }

    private void OpenReviewForm_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://aka.ms/mce-reviewappid");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开浏览器就让用户手动复制地址
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) => TryOpenBrowser();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_userCode)) return;
        try
        {
            Clipboard.SetText(_userCode);
            MsaStatus.Text = "代码已复制，去浏览器里粘贴。";
        }
        catch
        {
            MsaStatus.Text = "复制失败，手动输入吧。";
        }
    }
}

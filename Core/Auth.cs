using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class AccountInfo
{
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Xuid { get; set; } = "";
    public string UserType { get; set; } = "msa";
    public string ClientId { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.MinValue;

    public bool IsExpired => ExpiresAt != DateTime.MinValue && DateTime.UtcNow >= ExpiresAt;
}

public sealed class DeviceCodeInfo
{
    public string UserCode { get; init; } = "";
    public string VerificationUri { get; init; } = "";
    public string DeviceCode { get; init; } = "";
    public int Interval { get; init; } = 5;
    public int ExpiresIn { get; init; } = 900;
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

/// <summary>
/// 微软账号 → Minecraft 令牌的五级链路。
/// 每一关都拿上一关的 token 当输入，任何一关失败都表现为「登录不了」，
/// 但原因完全不同，所以错误信息必须带关卡名。
/// </summary>
public static class MsaAuth
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.signin offline_access";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private static readonly Dictionary<string, string> XstsErrors = new()
    {
        ["2148916233"] = "这个微软账号没有 Xbox 账号，先去 xbox.com 建一个（免费）",
        ["2148916235"] = "所在地区不支持 Xbox Live",
        ["2148916236"] = "需要成人验证（账号地区被判定为受限）",
        ["2148916237"] = "需要成人验证（账号地区被判定为受限）",
        ["2148916238"] = "未成年账号，需由成人把它加入 Microsoft 家庭组",
    };

    public static async Task<DeviceCodeInfo> BeginDeviceCodeAsync(string clientId, CancellationToken ct = default)
    {
        var body = await PostFormAsync(Authority + "/devicecode", new()
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        }, ct);

        return new DeviceCodeInfo
        {
            UserCode = Str(body, "user_code"),
            VerificationUri = Str(body, "verification_uri"),
            DeviceCode = Str(body, "device_code"),
            Interval = body["interval"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 5,
            ExpiresIn = body["expires_in"] is JsonValue ev && ev.TryGetValue<int>(out var e) ? e : 900,
        };
    }

    public static async Task<AccountInfo> CompleteAsync(
        DeviceCodeInfo device,
        string clientId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = device.Interval;
        JsonObject? tokens = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var (status, body) = await PostFormRawAsync(Authority + "/token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = device.DeviceCode,
            }, ct);

            if (status == 200) { tokens = body; break; }

            var error = Str(body, "error");
            switch (error)
            {
                case "authorization_pending":
                    progress?.Report("等待你在浏览器里完成登录…");
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "authorization_declined":
                    throw new AuthException("你在授权页面上点了拒绝");
                case "expired_token":
                    throw new AuthException("验证码已过期，请重新登录");
                default:
                    throw new AuthException($"微软 OAuth 失败：{error} {Str(body, "error_description")}");
            }
        }

        if (tokens == null) throw new AuthException("轮询超时，验证码已过期");

        var msToken = Str(tokens, "access_token");
        var refreshToken = Str(tokens, "refresh_token");

        return await ContinueWithMsTokenAsync(msToken, refreshToken, clientId, progress, ct);
    }

    /// <summary>
    /// 浏览器授权码登录：开系统默认浏览器，用户在微软登录页直接输邮箱 + 密码，
    /// 授权码经本地回环地址返回。这是桌面上唯一可行的方式——微软已封掉内嵌
    /// WebView 跑 OAuth，官方推荐「系统浏览器 + loopback redirect」；
    /// Azure 对 localhost 回环地址匹配时忽略端口，所以随便开个空闲端口即可。
    /// </summary>
    public static async Task<AccountInfo> InteractiveLoginAsync(
        string clientId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var port = FreePort();

        // 微软只忽略 localhost 回环地址的**端口**，主机名必须字面匹配：
        // 注册成 http://localhost 的，送 127.0.0.1 会直接 invalid_request。
        // 所以先探一下哪个能过，两个主机名都挂上监听，浏览器怎么解析都能回来。
        var candidates = new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" };
        var redirect = candidates[0];
        foreach (var candidate in candidates)
        {
            if (await RedirectUriAcceptedAsync(clientId, candidate, ct)) { redirect = candidate; break; }
        }

        using var listener = new HttpListener();
        foreach (var prefix in candidates)
        {
            try { listener.Prefixes.Add(prefix); }
            catch { /* 某个主机名加不上不影响另一个 */ }
        }
        listener.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));   // 5 分钟不点完作废

        var state = Guid.NewGuid().ToString("N");
        string? code = null, error = null, errorDescription = null, stateBack = null;

        try
        {
            var url = $"{Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
                      "&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&scope={Uri.EscapeDataString(Scope)}&state={state}";

            progress?.Report("正在打开微软登录页…");
            OpenBrowserUrl(url);

            // 等到带 code / error 的那次回调（favicon 之类的请求直接回掉，继续等）
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(cts.Token);
                var query = context.Request.QueryString;

                if (context.Request.Url?.AbsolutePath.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await WriteCallbackPageAsync(context, 404, "text/plain; charset=utf-8", "");
                    continue;
                }

                code = query.Get("code");
                error = query.Get("error");
                errorDescription = query.Get("error_description");
                stateBack = query.Get("state");

                // charset 必须显式写：不写浏览器按系统默认编码（中文 Windows 是 GBK）解 UTF-8，全是乱码
                await WriteCallbackPageAsync(context, 200, "text/html; charset=utf-8",
                    string.IsNullOrEmpty(error)
                        ? CallbackPage("登录成功", "可以关闭这个页面，回到 MclLauncher 了。", true)
                        : CallbackPage("登录失败", "可以关闭这个页面，回启动器重试。", false));
                break;
            }
        }
        catch (OperationCanceledException)
        {
            throw new AuthException("登录已取消或等待超时，请重试");
        }
        finally
        {
            try { listener.Stop(); } catch { /* 端口释放失败也无所谓，进程马上就退 */ }
        }

        if (!string.IsNullOrEmpty(error))
            throw new AuthException($"微软授权失败：{error} {errorDescription}");
        if (stateBack != state)
            throw new AuthException("state 校验不通过（可能被其他页面的回调干扰），请重试");
        if (string.IsNullOrEmpty(code))
            throw new AuthException("浏览器没有回传授权码，请重试");

        progress?.Report("正在用授权码换令牌…");
        var tokens = await PostFormAsync(Authority + "/token", new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["scope"] = Scope,
        }, ct);

        var msToken = Str(tokens, "access_token");
        var refreshToken = Str(tokens, "refresh_token");

        return await ContinueWithMsTokenAsync(msToken, refreshToken, clientId, progress, ct);
    }

    /// <summary>拿到微软 access_token 之后的第 2~5 关：Xbox → XSTS → Minecraft 令牌 → 档案。</summary>
    private static async Task<AccountInfo> ContinueWithMsTokenAsync(
        string msToken,
        string refreshToken,
        string clientId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("第 2 关：Xbox Live 认证…");
        var (xblToken, uhs) = await XboxLiveAsync(msToken, ct);

        progress?.Report("第 3 关：XSTS 授权…");
        var (xstsToken, xstsUhs, xuid) = await XstsAsync(xblToken, ct);

        progress?.Report("第 4 关：换取 Minecraft 令牌…");
        var (mcToken, expiresIn) = await MinecraftLoginAsync(xstsUhs, xstsToken, ct);

        progress?.Report("第 5 关：读取玩家档案…");
        var profile = await GetProfileAsync(mcToken, ct);

        return new AccountInfo
        {
            Name = Str(profile, "name"),
            Uuid = Str(profile, "id"),
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            Xuid = xuid,
            UserType = "msa",
            ClientId = clientId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
        };
    }

    private static async Task<(string token, string uhs)> XboxLiveAsync(string msToken, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                // "d=" 前缀不能漏，漏了直接 400
                ["RpsTicket"] = "d=" + msToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        };

        var (status, body) = await PostJsonRawAsync("https://user.auth.xboxlive.com/user/authenticate", payload, ct);
        if (status != 200) throw new AuthException($"XBL 认证失败 {status}");

        var token = Str(body, "Token");
        var uhs = body["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>() ?? "";
        return (token, uhs);
    }

    private static async Task<(string token, string uhs, string xuid)> XstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xblToken),
            },
            // 必须是 Minecraft 的，写成别的会拿到一张没法用的票
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        };

        var (status, body) = await PostJsonRawAsync("https://xsts.auth.xboxlive.com/xsts/authorize", payload, ct);
        if (status != 200)
        {
            var xerr = body["XErr"]?.ToString() ?? "";
            throw new AuthException(XstsErrors.TryGetValue(xerr, out var hint) ? hint : $"XSTS 拒绝 {status}（XErr={xerr}）");
        }

        var token = Str(body, "Token");
        var claim = body["DisplayClaims"]?["xui"]?[0];
        return (token, claim?["uhs"]?.GetValue<string>() ?? "", claim?["xid"]?.GetValue<string>() ?? "");
    }

    private static async Task<(string token, int expiresIn)> MinecraftLoginAsync(string uhs, string xstsToken, CancellationToken ct)
    {
        // uhs 为空时拼出来的 token 一定被拒（403），先把原因说清楚，别让用户以为是账号问题
        if (string.IsNullOrEmpty(uhs))
            throw new AuthException("XSTS 没有返回用户哈希（uhs），登录中止");

        var payload = new JsonObject
        {
            // 格式是 "XBL3.0 x=<uhs>;<xsts>"，中间是分号
            ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}",
        };

        var (status, body) = await PostJsonRawAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox", payload, ct);

        if (status != 200)
        {
            var message = Str(body, "errorMessage");
            var reason = body["details"]?["reason"] is JsonValue rv && rv.TryGetValue<string>(out var r) ? r : "";
            var detail = string.IsNullOrEmpty(message) ? body.ToJsonString() : message;
            if (!string.IsNullOrEmpty(reason)) detail += $"（{reason}）";

            // Minecraft 对第三方启动器的 app id 有白名单，自建注册通常卡在这
            if (detail.Contains("app registration", StringComparison.OrdinalIgnoreCase))
                throw new AuthException(
                    "Minecraft 拒绝了这个 client_id（403 Invalid app registration）：" +
                    "Mojang 对第三方应用做人工白名单审核，新注册的 ID 默认不在名单内，" +
                    "微软侧配置正确也过不了这一步。申请入口：https://aka.ms/mce-reviewappid " +
                    "（老牌启动器如 PCL2 / HMCL 早已在名单里，所以它们能登）。");

            if (detail.Contains("suspended", StringComparison.OrdinalIgnoreCase))
                throw new AuthException($"这个 Minecraft 账号被临时封停，通常 24 小时内自动恢复。原文：{detail}");

            throw new AuthException($"Minecraft 登录失败 {status}：{detail}");
        }

        var token = Str(body, "access_token");
        var expires = body["expires_in"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 86400;
        return (token, expires);
    }

    /// <summary>
    /// 用 refresh_token 续命。Minecraft 令牌只有 24 小时，过期后不必让用户重新走
    /// device code，拿 refresh_token 重跑第 1~5 关即可。
    /// </summary>
    public static async Task<AccountInfo> RefreshAsync(
        string refreshToken,
        string clientId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var (status, body) = await PostFormRawAsync(Authority + "/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }, ct);

        if (status != 200)
            throw new AuthException($"令牌刷新失败（{status}），需要重新登录");

        var msToken = Str(body, "access_token");
        var newRefresh = Str(body, "refresh_token");

        progress?.Report("Xbox Live 认证…");
        var (xblToken, uhs) = await XboxLiveAsync(msToken, ct);

        progress?.Report("XSTS 授权…");
        var (xstsToken, xstsUhs, xuid) = await XstsAsync(xblToken, ct);

        progress?.Report("换取 Minecraft 令牌…");
        var (mcToken, expiresIn) = await MinecraftLoginAsync(xstsUhs, xstsToken, ct);

        var profile = await GetProfileAsync(mcToken, ct);

        return new AccountInfo
        {
            Name = Str(profile, "name"),
            Uuid = Str(profile, "id"),
            AccessToken = mcToken,
            RefreshToken = string.IsNullOrEmpty(newRefresh) ? refreshToken : newRefresh,
            Xuid = xuid,
            UserType = "msa",
            ClientId = clientId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
        };
    }

    private static async Task<JsonObject> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new("Bearer", mcToken);

        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new AuthException("这个账号没有 Minecraft: Java Edition 的所有权");

        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"拉取档案失败 {(int)response.StatusCode}");

        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        var (status, body) = await PostFormRawAsync(url, form, ct);
        if (status != 200) throw new AuthException($"请求失败 {status}：{Str(body, "error_description")}");
        return body;
    }

    /// <summary>
    /// 探一下这个回环地址有没有在应用注册里登记：正常会跳到登录页 HTML，
    /// 没登记时微软的页面里会出现 invalid_request / AADSTS50011。
    /// 网络异常时放行，别因为探测失败挡住用户。
    /// </summary>
    private static async Task<bool> RedirectUriAcceptedAsync(string clientId, string redirectUri, CancellationToken ct)
    {
        var probe = $"{Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
                    "&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    $"&scope={Uri.EscapeDataString(Scope)}&state=probe";

        try
        {
            // HttpClient 默认跟随 302，最终落在登录页
            using var response = await Http.GetAsync(probe, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            return !html.Contains("invalid_request", StringComparison.OrdinalIgnoreCase)
                && !html.Contains("AADSTS50011", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>取一个空闲 TCP 端口给 loopback 监听用（Azure 匹配 localhost 时忽略端口）。</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static void OpenBrowserUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就把地址抛给用户手动访问
            throw new AuthException($"无法自动打开浏览器，请手动访问：{url}");
        }
    }

    private static async Task WriteCallbackPageAsync(
        HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.OutputStream.Close();
    }

    /// <summary>浏览器里给用户看的收尾页：<meta charset> 和响应头 charset 都写上，双保险。</summary>
    private static string CallbackPage(string title, string message, bool ok) => $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <title>{{title}} · MclLauncher</title>
          <style>
            body { margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center;
                   font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif; background: #f5f7fa; color: #1f2328; }
            .card { background: #fff; padding: 44px 52px; border-radius: 16px; text-align: center;
                    box-shadow: 0 10px 36px rgba(15, 23, 42, .10); max-width: 420px; }
            .icon { font-size: 46px; line-height: 1; margin-bottom: 14px; color: {{(ok ? "#16a34a" : "#dc2626")}}; }
            h1 { font-size: 20px; margin: 0 0 10px; font-weight: 600; }
            p { margin: 0; color: #5b6472; font-size: 14px; line-height: 1.7; }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="icon">{{(ok ? "&#10004;" : "&#10006;")}}</div>
            <h1>{{title}}</h1>
            <p>{{message}}</p>
          </div>
        </body>
        </html>
        """;

    private static async Task<(int status, JsonObject body)> PostFormRawAsync(
        string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(url, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, Parse(text));
    }

    private static async Task<(int status, JsonObject body)> PostJsonRawAsync(
        string url, JsonObject payload, CancellationToken ct)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, Parse(text));
    }

    private static JsonObject Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

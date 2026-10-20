# Minecraft Java Edition API — App ID Review Application

> 提交入口：https://aka.ms/mce-reviewappid
> 用法：把方括号 `[...]` 里的内容换成你自己的信息，其余部分可直接粘贴到表单对应字段。
> 两个版本：A 是逐字段填写版（字段名不确定时按语义对号入座），B 是一段式描述（表单只有一个大框时用）。

---

## 0. 关键事实（填写前确认，务必与事实一致）

| 项目 | 值 |
|---|---|
| Application (client) ID | `4e754f5a-338b-4a52-9eb0-5db9d801917c` |
| App / launcher name | `MclLauncher` |
| Platform | Windows desktop (C# / .NET 8 / WPF) |
| Distribution | [personal use only / source on GitHub: <link> / download: <link>] |
| Monetization | None (free, no ads, no in-app purchases, no donation prompts) |
| Redirect URI registered | `http://localhost` (public client / loopback) |
| OAuth flow | Authorization code flow in the system browser + loopback redirect; `state` validated |
| Scopes requested | `XboxLive.signin offline_access` |
| Where tokens are stored | Locally on the user's machine only (`%APPDATA%\.minecraft\mcl_accounts.json`) |
| Data collected by the app | None |

---

## A. 逐字段版本

### 1. Applicant / contact

- Name: `[你的姓名，如 Xixi Lin]`
- Email: `[你的邮箱]`
- Organization type: Individual developer (hobby project)
- Country / region: `[China]`

### 2. Application name and identifier

- Application name: `MclLauncher`
- Microsoft Entra application (client) ID: `4e754f5a-338b-4a52-9eb0-5db9d801917c`
- Application type: Desktop / native public client (no client secret)

### 3. Short description (one or two sentences)

> A lightweight, non-commercial Minecraft: Java Edition launcher for Windows that lets a user sign in with
> their own Microsoft account, install and launch game versions, and import CurseForge modpacks.

### 4. Detailed description of what the application does

> MclLauncher is a personal, free desktop launcher for Minecraft: Java Edition running on Windows.
> It performs four functions:
>
> 1. **Microsoft account sign-in** — the user clicks "Sign in", which opens Microsoft's own login page in
>    the system default browser. The user types their email and password **on Microsoft's page**; the
>    application never sees or stores the password. The authorization code is returned to a loopback
>    listener (`http://localhost:<random port>/`) and exchanged for tokens at
>    `login.microsoftonline.com/consumers/oauth2/v2.0/token`.
> 2. **Launching** — the launcher reads the installed version manifest, builds the Java classpath,
>    extracts natives, and starts the game process. It does **not** modify game files or inject code.
> 3. **Installing game versions** — it downloads version metadata, client jars, libraries and assets from
>    Mojang's public metadata/CDN endpoints, verifying SHA-1 hashes.
> 4. **Importing modpacks** — the user supplies a CurseForge modpack `.zip`; the launcher reads its
>    `manifest.json` and downloads the listed mod files from CurseForge.
>
> The application is a single-purpose hobby project. It is not a mod, not a server, not a tool for
> modifying or spoofing game data, and it does not redistribute game assets.

### 5. Which APIs are used, and why

> - **Microsoft identity platform** (`login.microsoftonline.com/consumers/oauth2/v2.0`) — OAuth 2.0
>   authorization code flow, scope `XboxLive.signin offline_access`, to obtain the user's XBL token.
> - **Xbox Live** (`user.auth.xboxlive.com/user/authenticate`) — exchange the Microsoft token for an XBL
>   token (`rp://api.minecraftservices.com/`).
> - **XSTS** (`xsts.auth.xboxlive.com/xsts/authorize`) — obtain the XSTS token required by Minecraft Services.
> - **Minecraft Services** (`api.minecraftservices.com/authentication/login_with_xbox`,
>   `api.minecraftservices.com/minecraft/profile`) — obtain the Minecraft access token and read the
>   player's own profile (name and UUID). Nothing else on `api.minecraftservices.com` is called.
> - **Mojang public metadata** (`piston-meta.mojang.com`, `launchermeta.mojang.com`,
>   `resources.download.minecraft.net`, `libraries.minecraft.net`) — version manifest, assets, libraries.
>
> The `offline_access` scope is used solely to refresh an expired token so the user does not have to sign
> in again every 24 hours.

### 6. Data collection, storage and privacy

> - The application collects **no** personal data and sends **no** telemetry or analytics anywhere.
> - It does not read browsing history, other files, or any data outside its own working directory.
> - The Minecraft access token and the OAuth refresh token are stored **locally on the user's own machine**
>   (plain JSON in the user's `.minecraft` folder) and are never transmitted to the developer or any third
>   party. There is no server component, no proxy, and no cloud sync.
> - Account passwords are never handled by the application: authentication happens entirely on Microsoft's
>   own web pages in the user's browser.
> - The application does not sell, share, or monetize any data or account.

### 7. Distribution and audience

> `[Choose one and delete the other: "The application is a personal project used only by the developer." /
> "The application is open source and published at <URL>; users build it themselves."]`
> There is no paid distribution, no bundled adware, and no user accounts created with the developer.

### 8. Compliance statement

> I confirm that:
> - The application uses only the official Microsoft OAuth 2.0 and Xbox Live / XSTS authentication chain,
>   with no credential collection and no third-party authentication servers.
> - It does not circumvent authentication, does not reuse other applications' client IDs, and does not
>   distribute modified game files or assets.
> - Access is requested solely for the APIs listed above, in the manner described.
> - I will comply with the Minecraft Usage Guidelines and the Minecraft EULA, and will remove API access
>   immediately upon request.

---

## B. 一段式描述（表单只有一个大框时用）

> MclLauncher is a free, non-commercial Minecraft: Java Edition launcher for Windows, developed by an
> individual for personal use. It signs the user in with their own Microsoft account through Microsoft's
> official OAuth 2.0 authorization code flow in the **system browser** (scope `XboxLive.signin
> offline_access`, loopback redirect `http://localhost`), then exchanges the token via Xbox Live
> (`user.auth.xboxlive.com`) and XSTS (`xsts.auth.xboxlive.com`, relying party
> `rp://api.minecraftservices.com/`), and finally calls `POST api.minecraftservices.com/authentication/
> login_with_xbox` plus `GET api.minecraftservices.com/minecraft/profile` to obtain the player's own
> Minecraft access token and profile. Those two endpoints are the only Minecraft Services calls the app
> makes. The launcher then installs and starts the game locally and can import CurseForge modpacks.
>
> The application collects no personal data, has no telemetry, no backend server and no proxy. The user's
> password is only ever entered on Microsoft's own login page — the app never sees it. Access and refresh
> tokens are stored locally on the user's machine and are never transmitted to the developer or anyone
> else. There is no monetization, no ads, and no redistribution of game assets. The requested client ID is
> `4e754f5a-338b-4a52-9eb0-5db9d801917c`.

---

## C. 填表提示

1. **别夸大**：如果代码不开源，就不要写 open source；如果只你自己用，就写 personal use。审核看的是真实性。
2. **一处不一致就会被拒**：表单里的 client ID 必须和 Azure 应用注册里的完全一致（`4e754f5a-338b-4a52-9eb0-5db9d801917c`）。
3. **强调"不接触密码"**：把"密码只在微软页面上输入"讲清楚，这是审核最关心的一条。
4. **强调"只调 2 个 Minecraft 端点"**：范围越窄越容易过。
5. **预期**：个人爱好项目通过率不高，可能几周无回复或被拒。被拒可补材料重提（例如开源仓库链接、界面上传截图、隐私说明）。
6. **别在同期反复提交**：一次提交后等结果，重复提交容易被判为刷单。
7. 如果应用还没有公开下载地址，把 Distribution 那段如实写明"个人使用，不对外分发"。

---

# D. 实际表单逐项对照（以截图里的 9 个字段为准）

| # | 字段 | 填什么 |
|---|---|---|
| 1 | 已阅读并理解 EULA / 使用指南 | 选「是的」 |
| 2 | 联系方式 | `linlinhcom@163.com`（**必须与 Azure 账号里登记的联系方式一致**，见下方提示） |
| 3 | 请求类型 | 选「新 AppID 批准」 |
| 4 | 应用名称 | `MCVVC`（与 Entra 应用注册的「显示名称」一字不差；项目内部叫 MclLauncher，表单上以注册名为准） |
| 5 | 应用 ID | `4e754f5a-338b-4a52-9eb0-5db9d801917c` |
| 6 | 租户识别（目录/租户 ID） | `3dcb21c6-7bd3-403f-acc3-7d42c8afa80c`（应用注册「概述」页的「目录(租户) ID」） |
| 7 | 相关网站或域名 | 见下方「第 7 项怎么填」 |
| 8 | 正当性 | 用下面「字段 8 粘贴文本」 |
| 9 | 其他信息 | 用下面「字段 9 粘贴文本」（可选，建议填） |

## 第 6 项：租户 ID 在哪里

1. 打开 https://entra.microsoft.com
2. 左侧「**概述**」（或右上角齿轮旁账户信息）
3. 页面上有「**租户 ID**」一栏，点复制图标
   也可以在「应用注册 → MCVVC → 概述」页看到「**目录(租户) ID**」，两者相同

注意：这个 ID 是目录 ID，不是 client_id；填错会被退回。

## 第 7 项怎么填

这一栏是让审核方"能找到关于你和这个项目的更多信息"，必填。按你的实际情况选一种：

- **开源了**：填仓库地址，如 `https://github.com/<你的用户名>/MclLauncher`
- **没开源，但有介绍页**：填知乎/B站/个人博客里介绍这个启动器的页面
- **什么都没有**：最稳妥的做法是现在建一个**公开 GitHub 仓库**，把这套代码或至少 README + 截图放上去，链接填进去。
  一个能打开的页面比任何文字说明都有说服力；空着或填 n/a 基本等于放弃审核。

## 字段 8 粘贴文本（正当性）

> MCVVC is a free, non-commercial Minecraft: Java Edition launcher for Windows, developed by an
> individual for personal use. I am requesting access for exactly two Minecraft Services endpoints:
> `POST /authentication/login_with_xbox` and `GET /minecraft/profile`. They are required to let a player
> sign in with their own Microsoft account and to read back that player's own profile (name and UUID).
>
> The sign-in flow uses Microsoft's official OAuth 2.0 authorization code flow in the system browser with
> a loopback redirect (`http://localhost`, scope `XboxLive.signin offline_access`), followed by Xbox Live
> and XSTS. The user's password is only ever typed on Microsoft's own login page; the application never
> receives or stores it. Access and refresh tokens are stored locally on the user's machine and are never
> transmitted to me or any third party. The application has no backend server, no proxy, no telemetry and
> no analytics.
>
> The launcher only installs and starts the game locally, and can import CurseForge modpacks. It does not
> modify game files, does not bypass authentication, does not reuse another application's client ID, and
> does not redistribute game assets. There is no monetization, advertising or paid feature.
>
> Access is requested solely for the two endpoints above, in the manner described. I will comply with the
> Minecraft Usage Guidelines and the Minecraft EULA, and will stop using the APIs immediately if requested.

## 字段 9 粘贴文本（其他信息，建议填）

> This submission is a new App ID approval request, not a report about a suspicious application.
>
> App display name: MCVVC. Client ID: `4e754f5a-338b-4a52-9eb0-5db9d801917c`. Tenant ID:
> `3dcb21c6-7bd3-403f-acc3-7d42c8afa80c`. Registered as a public client with a loopback redirect URI
> (`http://localhost`), audience "Accounts in any organizational directory and personal Microsoft
> accounts". No client secret is used.
>
> What the application does NOT do:
> - does not modify, patch or inject into game files or the Java process
> - does not bypass or proxy the Microsoft / Xbox / XSTS authentication chain
> - does not reuse or embed another launcher's client ID
> - does not run any server, proxy or relay service for third parties
> - does not collect, sell, share or upload user data or tokens
> - does not redistribute Minecraft assets, and does not allow offline-mode impersonation of premium accounts
>
> On request I can provide the source code, UI screenshots, or a step-by-step walkthrough of the
> sign-in flow. Contact email: `linlinhcom@163.com`.

## 第 7 项：没有网站怎么办（本机已准备好仓库）

项目目录 `E:\2026-10-20-15-46-05\MclLauncher` 已经 `git init` 并完成首次提交（44 个文件，
`bin/` `obj/` 已被 `.gitignore` 排除，没有硬编码密钥）。只差推到 GitHub：

1. 打开 https://github.com/new，仓库名填 `MCVVC`，**不要**勾选 README / .gitignore / license（必须是空仓库）
2. 本地执行：

```bash
cd E:\2026-10-20-15-46-05\MclLauncher
git remote add origin https://github.com/<你的用户名>/MCVVC.git
git push -u origin main
```

3. 第 7 项填 `https://github.com/<你的用户名>/MCVVC`

**不想公开源码**：只上传说明也够——新建仓库后只放 `README.md` 和 `preview*.png`（README 里已经引用这些截图），
代码留在本地。审核方要的是"能了解这个项目是什么"，不是源码本身。


## 提交前最后检查

- [ ] 第 5 项 client ID 与 Azure 里完全一致（32 位、4 段、小写十六进制）
- [ ] 第 6 项是**租户 ID**，不是 client_id
- [ ] 第 2 项邮箱已填进 Entra 账号的联系信息（否则"交叉核对"对不上会被退回）
- [ ] 第 7 项链接能匿名打开（无痕窗口试一次）
- [ ] 只提交一次，等结果

## 顺带一提：应用概览页那条黄色警告

「End users cannot grant consent to newly registered multitenant apps without verified publisher」
含义：多租户应用在未验证发布者时，**普通用户无法自行同意授权**，需要目录管理员同意。

- 你自己用（你是目录管理员）：不受影响，前面的登录能走到 XSTS 已经证明了
- 将来要把启动器发给别人：对方登录时可能提示「需要管理员批准」，需要加 MPN ID 验证发布者，
  或者让对方用自己租户的管理员同意一次

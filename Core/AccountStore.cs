using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MclLauncher.Core;

public sealed class StoredAccount
{
    public string Type { get; set; } = "offline";     // offline / msa
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Xuid { get; set; } = "";
    public string ClientId { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.MinValue;

    public bool IsMsa => Type == "msa";

    public bool IsExpired => IsMsa && ExpiresAt != DateTime.MinValue && DateTime.UtcNow >= ExpiresAt;

    public string Display => IsMsa ? $"{Name}（正版）" : $"{Name}（离线）";

    public string Initial => string.IsNullOrEmpty(Name)
        ? "?"
        : Name[..1].ToUpperInvariant();
}

/// <summary>
/// 账号持久化。存在 .minecraft/mcl_accounts.json。
///
/// 注意：refresh_token 目前是明文存的。真要分发出去，应该用 DPAPI
/// （CryptProtectData）绑到当前 Windows 用户，否则拿到文件就能盗号。
/// </summary>
public static class AccountStore
{
    private static string FilePath => Path.Combine(GamePaths.DefaultRoot, "mcl_accounts.json");

    public static StoredAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<StoredAccount>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(StoredAccount account)
    {
        try
        {
            var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 存不下来不影响本次使用
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public static string LoadClientId()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            return node?["ClientId"]?.GetValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static void SaveClientId(string clientId)
    {
        var account = Load();
        if (account == null) return;
        account.ClientId = clientId;
        Save(account);
    }

    /// <summary>
    /// 离线 UUID：和 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:&lt;name&gt;") 一致。
    /// 走 MD5 后按 RFC 4122 改版本位（3）和变体位，直接输出 32 位 hex ——
    /// 不要转成 Guid，.NET 的 Guid 字节序和 Java 不一样，会得到完全不同的 UUID。
    /// </summary>
    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static StoredAccount CreateOffline(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed)) trimmed = "Player";

        return new StoredAccount
        {
            Type = "offline",
            Name = trimmed,
            Uuid = OfflineUuid(trimmed),
            AccessToken = "0",
            ExpiresAt = DateTime.MinValue,
        };
    }
}

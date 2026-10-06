// Device authorization, token refresh and catalog request adapted from CC Switch.
// Upstream: farion1231/cc-switch, commit a4d07f313c783b0b16598c135d278a68d7653530.
// Copyright (c) 2025 Jason Young. MIT; see LICENSE-CC-SWITCH.txt.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public sealed class CodexCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string ProtectedAccess { get; set; } = "";
    public string ProtectedRefresh { get; set; } = "";
    public long ExpiresAt { get; set; }
    public long UpdatedAt { get; set; }
    public override string ToString() => Email.Length > 0 ? Email : "ChatGPT 订阅账号 " + Id[..8];
}
public sealed record DeviceAuthorization(string DeviceId, string UserCode, int Interval, long ExpiresAt);

public sealed class CodexOAuth : IDisposable
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string Version = "0.159.0";
    public const string Backend = "https://chatgpt.com/backend-api/codex";
    public const string VerificationUrl = "https://auth.openai.com/codex/device";
    private readonly AppStore store;
    private readonly HttpClient client;
    private readonly Dictionary<string, SemaphoreSlim> locks = new();
    private readonly string tokenUrl;
    public CodexOAuth(AppStore appStore, HttpMessageHandler? handler = null, string? oauthTokenUrl = null)
    {
        store = appStore; tokenUrl = oauthTokenUrl ?? "https://auth.openai.com/oauth/token";
        client = new HttpClient(handler ?? Network.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Claude-Link-Lite-Codex-OAuth");
    }
    public static JsonObject Claims(string token)
    {
        try
        {
            string part = token.Split('.')[1].Replace('-', '+').Replace('_', '/'); part += new string('=', (4 - part.Length % 4) % 4);
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))) as JsonObject ?? new();
        }
        catch { return new(); }
    }
    public static string Workspace(JsonObject claims) => claims["https://api.openai.com/auth"]?["chatgpt_account_id"]?.ToString() ?? claims["chatgpt_account_id"]?.ToString() ?? "";
    public static CodexCredential FromTokens(JsonObject tokens, string account = "", string email = "")
    {
        string access = tokens["access_token"]?.ToString() ?? "", refresh = tokens["refresh_token"]?.ToString() ?? "", id = tokens["id_token"]?.ToString() ?? "";
        var identity = Claims(id); var accessClaims = Claims(access);
        account = account.Length > 0 ? account : Workspace(identity).Length > 0 ? Workspace(identity) : Workspace(accessClaims);
        if (account.Length == 0 || refresh.Length == 0) throw new InvalidDataException("登录信息缺少订阅账号或刷新令牌，请重新登录。");
        email = email.Length > 0 ? email : identity["email"]?.ToString() ?? accessClaims["email"]?.ToString() ?? "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CodexCredential { AccountId = account, Email = email,
            ProtectedAccess = Secrets.Protect(access), ProtectedRefresh = Secrets.Protect(refresh),
            ExpiresAt = accessClaims["exp"]?.GetValue<long>() ?? (access.Length > 0 ? now + (tokens["expires_in"]?.GetValue<long>() ?? 0) : 0),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
    }
    public CodexCredential Add(CodexCredential incoming)
    {
        var existing = store.Settings.CodexAccounts.FirstOrDefault(x => x.AccountId == incoming.AccountId && x.Email == incoming.Email);
        if (existing != null)
        {
            incoming.Id = existing.Id;
            // Never replace a known working newer token with an older imported copy.
            if (existing.ExpiresAt > incoming.ExpiresAt && existing.ProtectedAccess.Length > 0) return existing;
            store.Settings.CodexAccounts.Remove(existing);
        }
        store.Settings.CodexAccounts.Add(incoming); store.Save(); return incoming;
    }
    public List<CodexCredential> ImportExisting(string userRoot)
    {
        var imported = new List<CodexCredential>();
        string cliPath = Path.Combine(userRoot, ".codex", "auth.json");
        if (File.Exists(cliPath))
        {
            var file = JsonFiles.Read(cliPath);
            if (file["auth_mode"]?.ToString() == "chatgpt" && file["tokens"] is JsonObject tokens)
                imported.Add(Add(FromTokens(tokens, tokens["account_id"]?.ToString() ?? "")));
        }
        string ccPath = Path.Combine(userRoot, ".cc-switch", "codex_oauth_auth.json");
        if (File.Exists(ccPath) && JsonFiles.Read(ccPath)["accounts"] is JsonObject accounts)
        {
            foreach (var entry in accounts.Select(x => x.Value).OfType<JsonObject>())
            {
                string account = entry["chatgpt_account_id"]?.ToString() ?? "", email = entry["email"]?.ToString() ?? "";
                if (store.Settings.CodexAccounts.Any(c => c.AccountId == account && c.Email == email)) continue;
                var token = new JsonObject { ["refresh_token"] = entry["refresh_token"]?.DeepClone(), ["id_token"] = entry["id_token"]?.DeepClone() };
                try { imported.Add(Add(FromTokens(token, account, email))); } catch (InvalidDataException) { }
            }
        }
        return imported.DistinctBy(c => c.Id).ToList();
    }
    public async Task<DeviceAuthorization> BeginDevice(CancellationToken cancellation)
    {
        using var response = await client.PostAsync("https://auth.openai.com/api/accounts/deviceauth/usercode", new StringContent(new JsonObject { ["client_id"] = ClientId }.ToJsonString(), Encoding.UTF8, "application/json"), cancellation);
        if (!response.IsSuccessStatusCode) throw new IOException("无法启动 ChatGPT 登录，HTTP " + (int)response.StatusCode + "。请检查网络和账号的设备代码登录设置。");
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!;
        int.TryParse(data["interval"]?.ToString(), out int interval);
        return new(data["device_auth_id"]!.ToString(), data["user_code"]!.ToString(), Math.Max(5, interval) + 3,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (data["expires_in"]?.GetValue<long>() ?? 900));
    }
    public async Task<CodexCredential?> PollDevice(DeviceAuthorization authorization, CancellationToken cancellation)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= authorization.ExpiresAt) throw new IOException("登录代码已过期，请重新登录。");
        var body = new JsonObject { ["device_auth_id"] = authorization.DeviceId, ["user_code"] = authorization.UserCode };
        using var response = await client.PostAsync("https://auth.openai.com/api/accounts/deviceauth/token", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellation);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new IOException("登录授权失败，HTTP " + (int)response.StatusCode + "。请重新登录。");
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!;
        var tokens = await Token(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
            ["code"] = result["authorization_code"]!.ToString(), ["code_verifier"] = result["code_verifier"]!.ToString(), ["redirect_uri"] = "https://auth.openai.com/deviceauth/callback" }, cancellation);
        cancellation.ThrowIfCancellationRequested(); return Add(FromTokens(tokens));
    }
    private async Task<JsonObject> Token(Dictionary<string, string> fields, CancellationToken cancellation)
    {
        using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(fields), cancellation);
        if (!response.IsSuccessStatusCode) throw new IOException("ChatGPT 登录令牌不可用，HTTP " + (int)response.StatusCode + "。请重新登录或导入最新的登录状态。");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation)) as JsonObject ?? throw new InvalidDataException("登录服务器返回格式不兼容。");
    }
    public async Task<(string Token, string Account)> Access(string id, CancellationToken cancellation, bool force = false)
    {
        SemaphoreSlim gate; lock (locks) { if (!locks.TryGetValue(id, out gate!)) locks[id] = gate = new(1, 1); }
        await gate.WaitAsync(cancellation);
        try
        {
            var credential = store.Settings.CodexAccounts.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("订阅账号已移除，请重新绑定。");
            if (!force && credential.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 60 && credential.ProtectedAccess.Length > 0)
                return (Secrets.Reveal(credential.ProtectedAccess), credential.AccountId);
            var tokens = await Token(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = Secrets.Reveal(credential.ProtectedRefresh), ["scope"] = "openid profile email" }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            string access = tokens["access_token"]?.ToString() ?? throw new InvalidDataException("服务器没有返回访问令牌。");
            credential.ProtectedAccess = Secrets.Protect(access);
            if (tokens["refresh_token"]?.ToString() is string refresh && refresh.Length > 0) credential.ProtectedRefresh = Secrets.Protect(refresh);
            credential.ExpiresAt = Claims(access)["exp"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (tokens["expires_in"]?.GetValue<long>() ?? 3600);
            credential.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); store.Save();
            return (access, credential.AccountId);
        }
        finally { gate.Release(); }
    }
    public static void Headers(HttpRequestMessage request, string access, string account)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", account);
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs"); request.Headers.TryAddWithoutValidation("version", Version);
        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/" + Version);
    }
    public async Task<List<string>> Models(string id, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var access = await Access(id, cancellation, attempt > 0);
            using var request = new HttpRequestMessage(HttpMethod.Get, Backend + "/models?client_version=" + Version); Headers(request, access.Token, access.Account);
            using var response = await client.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (!response.IsSuccessStatusCode) throw new IOException("订阅模型列表返回 HTTP " + (int)response.StatusCode + "。");
            return ParseModels(JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!);
        }
        throw new IOException("订阅登录已失效，请重新登录。");
    }
    public static List<string> ParseModels(JsonNode root)
    {
        var list = root as JsonArray ?? root["models"] as JsonArray ?? root["data"] as JsonArray ?? root["items"] as JsonArray;
        var models = new List<string>();
        if (list != null) foreach (var item in list)
        {
            string value = item is JsonValue ? item.ToString() : item?["slug"]?.ToString() ?? item?["id"]?.ToString() ?? item?["model"]?.ToString() ?? item?["name"]?.ToString() ?? "";
            if (value.Length > 0) models.Add(value);
        }
        if (root is JsonObject obj && obj["models"] is JsonObject map) models.AddRange(map.Select(x => x.Value is JsonObject value ? value["slug"]?.ToString() ?? value["id"]?.ToString() ?? x.Key : x.Value is JsonValue ? x.Value.ToString() : x.Key));
        return models.Distinct().Order().ToList();
    }
    public void Dispose() { client.Dispose(); foreach (var gate in locks.Values) gate.Dispose(); }
}

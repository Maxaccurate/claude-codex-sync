using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeLinkLite;

public sealed record Account(string Path, string Label, int Count)
{
    public override string ToString() => Label + " · " + Count + " 个会话";
}

public sealed class DesktopEnvironment
{
    public string Local { get; }
    public string User { get; }
    public string OfficialRoot { get; private set; } = "";
    public string GatewayRoot { get; }
    public string PackageFamily { get; private set; } = "";
    public string TranscriptRoot => Path.Combine(User, ".claude", "projects");
    public string NormalConfig => Path.Combine(Local, "Claude", "claude_desktop_config.json");
    public List<string> ModeConfigs { get; } = [];
    public DesktopEnvironment(string? local = null, string? user = null)
    {
        Local = local ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        User = user ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        GatewayRoot = Path.Combine(Local, "Claude-3p");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string> { Path.Combine(roaming, "Claude"), Path.Combine(Local, "Claude") };
        var packages = Path.Combine(Local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                var root = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                candidates.Insert(0, root);
                if (PackageFamily.Length == 0) PackageFamily = Path.GetFileName(package);
                var shadow = Path.Combine(package, "LocalCache", "Local", "Claude", "claude_desktop_config.json");
                if (File.Exists(shadow)) ModeConfigs.Add(shadow);
            }
        }
        OfficialRoot = candidates.FirstOrDefault(p => Directory.Exists(Path.Combine(p, "claude-code-sessions"))) ?? candidates.First();
        ModeConfigs.Add(NormalConfig);
        ModeConfigs.Add(Path.Combine(OfficialRoot, "claude_desktop_config.json"));
        ModeConfigs.Add(Path.Combine(GatewayRoot, "claude_desktop_config.json"));
    }
    public List<Account> Accounts(bool gateway)
    {
        var root = gateway ? GatewayRoot : OfficialRoot;
        var sessions = Path.Combine(root, "claude-code-sessions");
        if (!Directory.Exists(sessions)) return [];
        string current = JsonFiles.Read(Path.Combine(root, "config.json"))["lastKnownAccountUuid"]?.ToString() ?? "";
        return Directory.EnumerateDirectories(sessions).Where(p => Guid.TryParse(Path.GetFileName(p), out _)).Select(p =>
        {
            string active = Path.Combine(p, ActiveGroup(p, gateway));
            int count = Directory.Exists(active) ? Directory.EnumerateFiles(active, "local_*.json").Count() : 0;
            var label = gateway ? "Gateway" : Path.GetFileName(p) == current ? "当前官方账号" : "官方账号 " + Path.GetFileName(p)[..8];
            return new Account(p, label, count);
        }).OrderByDescending(a => Path.GetFileName(a.Path) == current).ThenByDescending(a => a.Count).ToList();
    }
    public string ActiveGroup(string account, bool gateway)
    {
        if (gateway)
        {
            string library = Path.Combine(GatewayRoot, "configLibrary"); string applied = JsonFiles.Read(Path.Combine(library, "_meta.json"))["appliedId"]?.ToString() ?? "";
            if (Guid.TryParse(applied, out _))
            {
                string group = JsonFiles.Read(Path.Combine(library, applied + ".json"))["deploymentOrganizationUuid"]?.ToString() ?? "";
                if (Guid.TryParse(group, out _)) return group;
            }
        }
        string log = Path.Combine(gateway ? GatewayRoot : OfficialRoot, "logs", "main.log");
        if (File.Exists(log))
        {
            using var reader = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int bytes = (int)Math.Min(reader.Length, 512 * 1024); reader.Seek(-bytes, SeekOrigin.End); var buffer = new byte[bytes]; reader.ReadExactly(buffer);
            string tail = Encoding.UTF8.GetString(buffer);
            var matches = Regex.Matches(tail, @"\[LocalSessionManager\] Initialization succeeded[^\r\n]*accountId=([^, ]+), orgId=([^, ]+)");
            foreach (Match match in matches.Reverse()) if (match.Groups[1].Value == Path.GetFileName(account) && Guid.TryParse(match.Groups[2].Value, out _)) return match.Groups[2].Value;
        }
        // A 3P deployment without an organization uses Claude's placeholder,
        // never the organization copied from an official subscription account.
        if (gateway) return "00000000-0000-4000-8000-000000000001";
        var scopes = Directory.Exists(account) ? Directory.EnumerateDirectories(account).Select(Path.GetFileName).Where(n => Guid.TryParse(n, out _)).ToList() : [];
        if (scopes.Count == 1) return scopes[0]!;
        throw new InvalidDataException("无法确定官方账号当前组织分区。请先在 Claude 打开目标工作区，再刷新。");
    }
    public bool IsGateway()
    {
        foreach (var path in new[] { NormalConfig, Path.Combine(GatewayRoot, "claude_desktop_config.json") })
            if (JsonFiles.Read(path)["deploymentMode"]?.ToString() == "3p") return true;
        return false;
    }
    public static List<Process> DesktopProcesses()
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Claude"))
        {
            try
            {
                var name = p.MainModule?.FileName ?? "";
                if (!name.Contains("\\claude-code\\", StringComparison.OrdinalIgnoreCase)) result.Add(p); else p.Dispose();
            }
            catch { result.Add(p); }
        }
        return result;
    }
    public static void EnsureClosed()
    {
        var processes = DesktopProcesses();
        try { if (processes.Count > 0) throw new InvalidOperationException("请先退出 Claude Desktop，再同步或切换。"); }
        finally { foreach (var p in processes) p.Dispose(); }
        var tidy = Process.GetProcessesByName("cc-code-history-tidy");
        try { if (tidy.Length > 0) throw new InvalidOperationException("请先关闭 History Tidy，避免两个工具同时写会话。"); }
        finally { foreach (var p in tidy) p.Dispose(); }
    }
    public static async Task CloseDesktop()
    {
        var all = DesktopProcesses();
        try
        {
            foreach (var p in all) { try { p.CloseMainWindow(); } catch { } }
            for (int i = 0; i < 200; i++)
            {
                var remaining = DesktopProcesses();
                int count = remaining.Count; foreach (var p in remaining) p.Dispose();
                if (count == 0) return;
                await Task.Delay(100);
            }
            throw new IOException("Claude 尚未正常退出，未强制结束进程。请从托盘退出，等记录保存后重试。");
        }
        finally { foreach (var p in all) p.Dispose(); }
    }
    public void Launch()
    {
        if (PackageFamily.Length > 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + PackageFamily + "!Claude") { UseShellExecute = true });
            return;
        }
        foreach (var path in new[] { Path.Combine(Local, "AnthropicClaude", "Claude.exe"), Path.Combine(Local, "Programs", "Claude", "Claude.exe") })
            if (File.Exists(path)) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; }
        throw new FileNotFoundException("未找到 Claude Desktop。请先安装官方 Windows 版。");
    }
}

public static class DesktopConfig
{
    public const string ProfileId = "76fcb5e1-e3cc-4dcb-9e18-ccc000000001";
    public static readonly string[] Roles = ["claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5"];
    private static readonly string[] GatewayKeys = ["inferenceProvider", "inferenceGatewayBaseUrl", "inferenceGatewayApiKey", "inferenceGatewayAuthScheme", "inferenceModels", "modelDiscoveryEnabled", "disableDeploymentModeChooser"];
    public static List<Change> Plan(DesktopEnvironment env, Provider? provider, string proxyUrl, string proxyToken)
    {
        var changes = new List<Change>();
        foreach (var path in env.ModeConfigs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var original = JsonFiles.Raw(path);
            var config = JsonFiles.Parse(original, path);
            config["deploymentMode"] = provider == null ? "1p" : "3p";
            if (config["enterpriseConfig"] is JsonObject enterprise)
                foreach (var key in GatewayKeys) enterprise.Remove(key);
            changes.Add(new Change(path, original, JsonFiles.Bytes(config), "Claude 模式配置"));
        }
        var library = Path.Combine(env.GatewayRoot, "configLibrary");
        var profilePath = Path.Combine(library, ProfileId + ".json");
        var metaPath = Path.Combine(library, "_meta.json");
        var profileRaw = JsonFiles.Raw(profilePath);
        var profile = JsonFiles.Parse(profileRaw, profilePath);
        foreach (var key in GatewayKeys) profile.Remove(key);
        var metaRaw = JsonFiles.Raw(metaPath);
        var meta = JsonFiles.Parse(metaRaw, metaPath);
        if (meta["entries"] != null && meta["entries"] is not JsonArray) throw new InvalidDataException("Claude 配置库 entries 格式不兼容。");
        var entries = meta["entries"] as JsonArray ?? new JsonArray();
        if (meta["entries"] == null) meta["entries"] = entries;
        foreach (var entry in entries.ToArray()) if (entry?["id"]?.ToString() == ProfileId) entries.Remove(entry);
        if (provider != null)
        {
            provider.Validate();
            profile["inferenceProvider"] = "gateway";
            profile["inferenceGatewayBaseUrl"] = provider.Routed ? proxyUrl : provider.BaseUrl.TrimEnd('/');
            profile["inferenceGatewayApiKey"] = provider.Routed ? proxyToken : provider.Key;
            profile["inferenceGatewayAuthScheme"] = "bearer";
            profile["disableDeploymentModeChooser"] = true;
            profile["coworkEgressAllowedHosts"] ??= new JsonArray("*");
            if (provider.Routed)
            {
                var models = new JsonArray();
                for (int i = 0; i < Roles.Length; i++) models.Add(new JsonObject { ["name"] = Roles[i], ["labelOverride"] = provider.UpstreamModel(Roles[i]) });
                profile["inferenceModels"] = models;
                profile["modelDiscoveryEnabled"] = false;
            }
            entries.Add(new JsonObject { ["id"] = ProfileId, ["name"] = "Claude Link Lite · " + provider.Name });
            meta["appliedId"] = ProfileId;
        }
        else if (meta["appliedId"]?.ToString() == ProfileId)
        {
            meta.Remove("appliedId");
            if (entries.FirstOrDefault()?["id"] is JsonNode next) meta["appliedId"] = next.DeepClone();
        }
        if (provider != null || File.Exists(profilePath)) changes.Add(new Change(profilePath, profileRaw, JsonFiles.Bytes(profile), "3P 服务配置"));
        changes.Add(new Change(metaPath, metaRaw, JsonFiles.Bytes(meta), "3P 配置库"));
        return changes;
    }
}

public static class Imports
{
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int i);
    public static List<Dictionary<string, string>> Rows(string path)
    {
        if (!File.Exists(path)) return [];
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(path, out db, 1, IntPtr.Zero) != 0) throw new IOException("无法只读打开 CC Switch 数据库。");
            if (sqlite3_prepare_v2(db, "SELECT * FROM providers", -1, out statement, IntPtr.Zero) != 0) throw new IOException("CC Switch 数据库格式不兼容。");
            var rows = new List<Dictionary<string, string>>();
            int rc;
            while ((rc = sqlite3_step(statement)) == 100)
            {
                var row = new Dictionary<string, string>();
                for (int i = 0; i < sqlite3_column_count(statement); i++)
                    row[Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i))!] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "";
                rows.Add(row);
            }
            if (rc != 101) throw new IOException("读取 CC Switch 服务失败，请关闭 CC Switch 后重试。");
            return rows;
        }
        finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    public static List<Provider> Providers(DesktopEnvironment environment)
    {
        var result = new List<Provider>();
        var db = Path.Combine(environment.User, ".cc-switch", "cc-switch.db");
        foreach (var row in Rows(db))
        {
            string app = row.GetValueOrDefault("app_type", "");
            if (!new[] { "claude", "claudeDesktop", "claude_desktop" }.Contains(app, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                var node = JsonNode.Parse(row.GetValueOrDefault("settings_config", "{}")) as JsonObject;
                if (node == null) continue;
                var env = node["env"] as JsonObject;
                string url = env?["ANTHROPIC_BASE_URL"]?.ToString() ?? node["baseUrl"]?.ToString() ?? node["inferenceGatewayBaseUrl"]?.ToString() ?? "";
                string key = env?["ANTHROPIC_AUTH_TOKEN"]?.ToString() ?? env?["ANTHROPIC_API_KEY"]?.ToString() ?? node["apiKey"]?.ToString() ?? node["inferenceGatewayApiKey"]?.ToString() ?? "";
                if (url.Length == 0 || key.Length == 0) continue;
                var provider = new Provider { Name = row.GetValueOrDefault("name", "导入的服务"), BaseUrl = url,
                    Sonnet = env?["ANTHROPIC_DEFAULT_SONNET_MODEL"]?.ToString() ?? "",
                    Opus = env?["ANTHROPIC_DEFAULT_OPUS_MODEL"]?.ToString() ?? "",
                    Haiku = env?["ANTHROPIC_DEFAULT_HAIKU_MODEL"]?.ToString() ?? "" };
                provider.Key = key;
                if (!result.Any(p => p.BaseUrl == url && p.Name == provider.Name)) result.Add(provider);
            }
            catch (System.Text.Json.JsonException) { }
        }
        var settingsPath = Path.Combine(environment.User, ".claude", "settings.json");
        if (File.Exists(settingsPath))
        {
            var env = JsonFiles.Read(settingsPath)["env"];
            string url = env?["ANTHROPIC_BASE_URL"]?.ToString() ?? "";
            string key = env?["ANTHROPIC_AUTH_TOKEN"]?.ToString() ?? env?["ANTHROPIC_API_KEY"]?.ToString() ?? "";
            if (url.Length > 0 && key.Length > 0 && !result.Any(p => p.BaseUrl == url))
            {
                var p = new Provider { Name = "Claude Code 当前服务", BaseUrl = url,
                    Sonnet = env?["ANTHROPIC_DEFAULT_SONNET_MODEL"]?.ToString() ?? "", Opus = env?["ANTHROPIC_DEFAULT_OPUS_MODEL"]?.ToString() ?? "", Haiku = env?["ANTHROPIC_DEFAULT_HAIKU_MODEL"]?.ToString() ?? "" };
                p.Key = key; result.Add(p);
            }
        }
        return result;
    }
}

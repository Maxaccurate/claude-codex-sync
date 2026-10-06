using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeLinkLite;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static JsonObject Parse(byte[]? raw, string path) => raw == null ? new JsonObject()
        : JsonNode.Parse(Encoding.UTF8.GetString(raw).TrimStart('\uFEFF')) as JsonObject ?? throw new InvalidDataException("配置必须是 JSON 对象：" + path);
    public static JsonObject Read(string path) => Parse(Raw(path), path);
    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Options) + "\n");
    public static byte[]? Raw(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public static bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    public static string Hash(byte[]? bytes) => bytes == null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
    public static void CheckPath(string path)
    {
        var cursor = new FileInfo(Path.GetFullPath(path));
        if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理符号链接：" + path);
        for (var dir = cursor.Directory; dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不处理目录链接：" + dir.FullName);
    }
    public static void Atomic(string path, byte[] bytes)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, ".claude-link-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

public sealed record Change(string Path, byte[]? Before, byte[]? After, string Label);
public sealed class FileTransaction
{
    private readonly string backupRoot;
    public FileTransaction(string root) { backupRoot = root; }
    public string Commit(IEnumerable<Change> items, Action? guard = null)
    {
        var changes = items.Where(c => !JsonFiles.Same(c.Before, c.After)).ToList();
        if (changes.Select(c => Path.GetFullPath(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
            throw new InvalidOperationException("事务包含重复文件。");
        guard?.Invoke();
        foreach (var c in changes) { JsonFiles.CheckPath(c.Path); if (!JsonFiles.Same(JsonFiles.Raw(c.Path), c.Before)) throw new IOException("文件已变化，请重新预览：" + c.Label); }
        if (changes.Count == 0) return "";
        var backup = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        var manifest = new JsonArray();
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Before != null) JsonFiles.Atomic(Path.Combine(backup, i + ".json"), c.Before);
            manifest.Add(new JsonObject { ["path"] = c.Path, ["beforeHash"] = JsonFiles.Hash(c.Before), ["afterHash"] = JsonFiles.Hash(c.After), ["existed"] = c.Before != null });
        }
        JsonFiles.Atomic(Path.Combine(backup, "manifest.json"), JsonFiles.Bytes(manifest));
        var committed = new List<Change>();
        try
        {
            foreach (var c in changes)
            {
                guard?.Invoke();
                if (!JsonFiles.Same(JsonFiles.Raw(c.Path), c.Before)) throw new IOException("写入前文件发生变化：" + c.Label);
                if (c.After == null) File.Delete(c.Path); else JsonFiles.Atomic(c.Path, c.After);
                committed.Add(c);
                if (!JsonFiles.Same(JsonFiles.Raw(c.Path), c.After)) throw new IOException("写入校验失败：" + c.Label);
            }
        }
        catch (Exception failure)
        {
            var recovery = new List<string>();
            foreach (var c in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (!JsonFiles.Same(JsonFiles.Raw(c.Path), c.After)) { recovery.Add(c.Label); continue; }
                    if (c.Before == null) File.Delete(c.Path); else JsonFiles.Atomic(c.Path, c.Before);
                }
                catch { recovery.Add(c.Label); }
            }
            throw new IOException(failure.Message + (recovery.Count > 0 ? "\n部分文件需从备份恢复：" + backup : "\n本次写入已回退。"), failure);
        }
        return backup;
    }
}

public static class Secrets
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        Blob output = default;
        try
        {
            bool ok = protect ? CryptProtectData(ref input, "Claude Link Lite", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows 密钥保护失败。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public static string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Reveal(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
}

public sealed class Provider
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "新的 3P 服务";
    public string BaseUrl { get; set; } = "";
    public string Protocol { get; set; } = "Anthropic";
    public string CredentialId { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public string Sonnet { get; set; } = "";
    public string Opus { get; set; } = "";
    public string Haiku { get; set; } = "";
    [JsonIgnore] public bool Routed => Protocol is "OpenAI" or "Codex" || new[] { Sonnet, Opus, Haiku }.Any(x => !string.IsNullOrWhiteSpace(x));
    [JsonIgnore] public string Key { get => Secrets.Reveal(ProtectedKey); set => ProtectedKey = Secrets.Protect(value); }
    public string UpstreamModel(string requested)
    {
        string first = new[] { Sonnet, Opus, Haiku }.FirstOrDefault(x => x.Length > 0) ?? requested;
        if (requested.Contains("opus", StringComparison.OrdinalIgnoreCase) || requested.Contains("fable", StringComparison.OrdinalIgnoreCase)) return Opus.Length > 0 ? Opus : first;
        if (requested.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return Haiku.Length > 0 ? Haiku : first;
        if (requested.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return Sonnet.Length > 0 ? Sonnet : first;
        throw new InvalidDataException("无法识别 Claude 模型角色：" + requested);
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("请输入服务名称。");
        if (Protocol == "Codex")
        {
            if (CredentialId.Length == 0) throw new InvalidDataException("请先登录或导入 ChatGPT 订阅账号。");
            if (!RoutedModels().Any()) throw new InvalidDataException("请至少填写一个账号可用的 Codex 模型 ID。");
            return;
        }
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("请输入正确的 API Base URL，不要附带账号、查询参数或 # 片段。");
        if (uri.Scheme == "http" && !uri.IsLoopback) throw new InvalidDataException("远程 API 请使用 HTTPS；HTTP 仅用于本机服务。");
        if (string.IsNullOrWhiteSpace(Key)) throw new InvalidDataException("请输入 API Key。");
        if (Protocol is not ("Anthropic" or "OpenAI")) throw new InvalidDataException("不支持的 API 协议。");
        if (Protocol == "OpenAI" && !RoutedModels().Any()) throw new InvalidDataException("OpenAI 兼容服务至少填写一个实际模型 ID。");
    }
    public IEnumerable<string> RoutedModels() => new[] { Sonnet, Opus, Haiku }.Where(x => x.Length > 0);
}

public sealed class Settings
{
    public bool AutomaticBridge { get; set; }
    public List<Provider> Providers { get; set; } = [];
    public List<CodexCredential> CodexAccounts { get; set; } = [];
    public string OfficialAccount { get; set; } = "";
    public string GatewayAccount { get; set; } = "";
    public string LastProvider { get; set; } = "official";
    public string ActiveProvider { get; set; } = "official";
    public int ActiveGatewayPort { get; set; } = 21891;
    public string LastSync { get; set; } = "尚未同步";
    public string ProtectedLocalToken { get; set; } = "";
}

public sealed class AppStore
{
    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string StatePath => Path.Combine(Root, "sessions-state.json");
    public string Backups => Path.Combine(Root, "backups");
    public Settings Settings { get; private set; }
    public AppStore(string root)
    {
        Root = root;
        Settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonFiles.Options) ?? new() : new();
    }
    public void Save() => JsonFiles.Atomic(SettingsPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Settings, JsonFiles.Options)));
    public string LocalToken()
    {
        if (Settings.ProtectedLocalToken.Length == 0) { Settings.ProtectedLocalToken = Secrets.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); Save(); }
        return Secrets.Reveal(Settings.ProtectedLocalToken);
    }
}

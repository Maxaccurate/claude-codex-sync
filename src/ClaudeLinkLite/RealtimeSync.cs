using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public sealed class CodexWriterLease : IDisposable
{
    private FileStream? file;
    public CodexWriterLease(string home, string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("无效的 Codex 会话 ID。");
        string path = Path.Combine(home, "thread-writer-locks", id + ".lock"); JsonFiles.CheckPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { file.Lock(0, long.MaxValue); } catch { file.Dispose(); file = null; throw new IOException("Codex 正在占用此会话，新增内容已保存，等待释放后对齐。"); }
    }
    public void Check() { if (file == null) throw new IOException("Codex 写入锁已经释放。"); }
    public void Dispose() { if (file != null) { try { file.Unlock(0, long.MaxValue); } finally { file.Dispose(); file = null; } } }
}

public sealed class RealtimeSync : IDisposable
{
    public static readonly SemaphoreSlim Gate = new(1);
    private readonly DesktopEnvironment environment;
    private readonly AppStore store;
    private readonly TranscriptBridge bridge;
    private readonly string codexHome;
    private readonly Action claudeGuard;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly System.Threading.Timer timer;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<string, HashSet<string>> journalKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BridgePair Pair, string[] Ids)> verification = new();
    private readonly Dictionary<string, string> capturedStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Stamp, BridgePlan Plan)> plans = new();
    private readonly Dictionary<string, string[]> savedVerification = new();
    private volatile bool disposed;
    public event Action<string>? Status;
    public RealtimeSync(DesktopEnvironment env, AppStore appStore) : this(env, appStore, null, null) { }
    internal RealtimeSync(DesktopEnvironment env, AppStore appStore, string? home, Action? testClaudeGuard)
    {
        environment = env; store = appStore; codexHome = home ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(environment.User, ".codex");
        bridge = new(env, appStore, codexHome, includeGatewayCards: false); claudeGuard = testClaudeGuard ?? ClaudeClosed;
        string verificationPath = Path.Combine(store.Root, "realtime-verification.json");
        if (File.Exists(verificationPath)) foreach (var property in JsonFiles.Read(verificationPath)) if (property.Value is JsonArray ids) savedVerification[property.Key] = ids.Select(x => x!.ToString()).ToArray();
        timer = new System.Threading.Timer(async _ => await Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }
    public void Start()
    {
        string codex = codexHome;
        foreach (var root in new[] { environment.TranscriptRoot, Path.Combine(environment.OfficialRoot, "claude-code-sessions"), Path.Combine(codex, "sessions") }.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, Filter = "*", InternalBufferSize = 64 * 1024 };
            watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (_, _) => Schedule(); watcher.Error += (_, _) => Schedule(); watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
        if (Directory.Exists(codex))
        {
            var ledger = new FileSystemWatcher(codex, "external_agent_session_imports.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            ledger.Changed += Changed; ledger.Created += Changed; ledger.Renamed += (_, _) => Schedule(); ledger.EnableRaisingEvents = true; watchers.Add(ledger);
        }
        Schedule();
    }
    private void Changed(object sender, FileSystemEventArgs e) { if (e.Name?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true || e.Name?.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) == true) Schedule(); }
    private void Schedule() { if (!disposed) try { timer.Change(700, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    internal int Capture(BridgePair pair)
    {
        string path = Path.Combine(store.Root, "realtime-journal", pair.CodexId + ".jsonl");
        if (!journalKeys.TryGetValue(path, out var known))
        {
            known = []; if (File.Exists(path)) foreach (string line in File.ReadLines(path)) { try { string? key = JsonNode.Parse(line)?["key"]?.ToString(); if (key != null) known.Add(key); } catch (System.Text.Json.JsonException) { } }
            journalKeys[path] = known;
        }
        var added = new Dictionary<string, JsonObject>();
        var completedStamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { (Name: "claude", Path: pair.ClaudePath), (Name: "codex", Path: pair.CodexPath) })
        {
            string stamp = Stamp(source.Path);
            if (capturedStamps.GetValueOrDefault(source.Path) == stamp) continue;
            byte[] raw = TranscriptBridge.Snapshot(source.Path); int complete = Array.LastIndexOf(raw, (byte)10) + 1;
            foreach (var line in TranscriptBridge.Lines(raw[..complete]))
            {
                var record = line.Record;
                bool message = source.Name == "claude" ? record["type"]?.ToString() is "user" or "assistant"
                    : record["type"]?.ToString() == "response_item" && record["payload"]?["type"]?.ToString() is "message" or "function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output";
                if (!message) continue;
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Name + "\n" + record.ToJsonString())));
                if (!known.Contains(key)) added[key] = new JsonObject { ["key"] = key, ["source"] = source.Name, ["sourcePath"] = source.Path, ["capturedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["record"] = record.DeepClone() };
            }
            if (Stamp(source.Path) == stamp) completedStamps[source.Path] = stamp;
        }
        if (added.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            long originalLength = output.Length;
            try { foreach (var entry in added.Values) output.Write(Encoding.UTF8.GetBytes(entry.ToJsonString() + "\n")); output.Flush(true); }
            catch { try { output.SetLength(originalLength); output.Flush(true); } catch { } throw; }
            known.UnionWith(added.Keys);
        }
        foreach (var (source, stamp) in completedStamps) capturedStamps[source] = stamp;
        return added.Count;
    }
    private static void ClaudeClosed()
    {
        DesktopEnvironment.EnsureClosed();
        var processes = Process.GetProcessesByName("claude"); try { if (processes.Length > 0) throw new IOException("Claude 正在运行，新增内容已保存，等待正常退出后写入。"); } finally { foreach (var process in processes) process.Dispose(); }
    }
    private static bool CodexTurnActive(byte[] raw)
    {
        bool active = false; foreach (var row in TranscriptBridge.Lines(raw)) if (row.Record["type"]?.ToString() == "event_msg")
        { string? type = row.Record["payload"]?["type"]?.ToString(); if (type == "task_started") active = true; if (type is "task_complete" or "turn_aborted") active = false; }
        return active;
    }
    internal static bool ClaudeTurnActive(byte[] raw)
    {
        var rows = TranscriptBridge.Lines(raw); var latest = rows.LastOrDefault(r => (r.Record["type"]?.ToString() is "user" or "assistant") && r.Record["isSidechain"]?.GetValue<bool>() != true && r.Record["isMeta"]?.GetValue<bool>() != true);
        if (latest == null) return false;
        string? leaf = rows.LastOrDefault(r => r.Record["type"]?.ToString() == "last-prompt")?.Record["leafUuid"]?.ToString();
        var map = new Dictionary<string, JsonObject>(); foreach (var row in rows) if (row.Record["uuid"] is JsonValue id) map[id.ToString()] = row.Record;
        var seen = new HashSet<string>(); while (leaf != null && seen.Add(leaf) && map.TryGetValue(leaf, out var parent)) { if (leaf == latest.Record["uuid"]?.ToString()) return false; leaf = parent["parentUuid"]?.ToString(); }
        return latest.Record["type"]?.ToString() == "user" || latest.Record["message"]?["stop_reason"]?.ToString() is not ("end_turn" or "stop_sequence");
    }
    private async Task Tick()
    {
        if (disposed || !await Gate.WaitAsync(0)) { Schedule(); return; }
        bool pending = false; var notes = new List<string>();
        try
        {
            var pairs = bridge.Discover(originalOnly: true);
            if (pairs.Count == 0) { Status?.Invoke("未找到会话映射：先在 Codex 导入所选 Claude 账号的会话，再刷新预览。"); return; }
            foreach (var pair in pairs) if (savedVerification.Remove(pair.CodexId, out var ids)) verification[pair.CodexId] = (pair, ids);
            foreach (var pair in pairs)
            {
                if (disposed) break;
                try
                {
                    Capture(pair);
                    if (verification.TryGetValue(pair.CodexId, out var previous))
                    {
                        try { using (var probe = new CodexWriterLease(CodexIndex.RootForRollout(pair.CodexPath), pair.CodexId)) { }
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
                            await CodexIndex.Refresh(pair.CodexId, pair.CodexPath, CodexIndex.RootForRollout(pair.CodexPath), timeout.Token, previous.Ids); verification.Remove(pair.CodexId); SaveVerification();
                        } catch (Exception ex) { notes.Add(pair.Title + "：文件已写入，等待列表校验（" + ex.Message + "）"); pending = true; continue; }
                    }
                    string stamp = Stamp(pair.CodexPath) + "|" + Stamp(pair.ClaudePath) + "|" + Stamp(Path.Combine(store.Root, "transcript-bridge.json"));
                    var plan = plans.TryGetValue(pair.CodexId, out var cached) && cached.Stamp == stamp ? cached.Plan : bridge.Preview(pair);
                    plans[pair.CodexId] = (stamp, plan);
                    if (plan.Blocked != null) { notes.Add(pair.Title + "：" + plan.Blocked); pending = true; continue; }
                    if (plan.FromClaude.Count == 0 && plan.FromCodex.Count == 0) continue;
                    if (plan.FromClaude.Count > 0 && ClaudeTurnActive(plan.ClaudeBefore)) { notes.Add(pair.Title + "：等待 Claude 本轮保存完成"); pending = true; continue; }
                    if (plan.FromCodex.Count > 0 && CodexTurnActive(plan.CodexBefore)) { notes.Add(pair.Title + "：等待 Codex 本轮完成"); pending = true; continue; }
                    bool canWriteClaude = true; try { claudeGuard(); } catch { canWriteClaude = false; }
                    string direction = plan.FromCodex.Count > 0 && !canWriteClaude ? "to-codex" : "both";
                    if (direction == "to-codex" && plan.FromClaude.Count == 0) { notes.Add(pair.Title + "：等待 Claude 正常退出"); pending = true; continue; }
                    if (direction != "both") plan = bridge.Preview(pair, direction);
                    string home = CodexIndex.RootForRollout(pair.CodexPath); CodexWriterLease? lease = null;
                    try
                    {
                        bool writesCodex = plan.FromClaude.Count > 0 && direction != "to-claude";
                        if (writesCodex) lease = new CodexWriterLease(home, pair.CodexId);
                        void Guard() { if (disposed) throw new OperationCanceledException(); if (writesCodex) lease!.Check(); if (plan.FromCodex.Count > 0 && direction != "to-codex") claudeGuard(); }
                        bridge.ApplyWithGuard(plan, Guard);
                    }
                    finally { lease?.Dispose(); }
                    if (plan.FromClaude.Count > 0)
                    {
                        verification[pair.CodexId] = (pair, plan.FromClaude.Select(m => m.Id).ToArray()); SaveVerification();
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
                        try { await CodexIndex.Refresh(pair.CodexId, pair.CodexPath, home, timeout.Token, plan.FromClaude.Select(m => m.Id).ToArray()); verification.Remove(pair.CodexId); SaveVerification(); }
                        catch (Exception ex) { notes.Add(pair.Title + "：已写入，列表校验待完成（" + ex.Message + "）"); pending = true; }
                    }
                    if (direction != "both") { pending = true; notes.Add(pair.Title + "：Claude 的新增已同步，Codex 的新增等待 Claude 正常退出后补齐"); }
                }
                catch (Exception ex) { notes.Add(pair.Title + "：" + ex.Message); pending = true; }
            }
            Status?.Invoke(notes.Count > 0 ? string.Join("；", notes.Take(2)) : $"{pairs.Count} 对官方原始会话已对齐 · 等待下一次变化");
        }
        catch (Exception ex) { Status?.Invoke("等待同步：" + ex.Message); pending = true; }
        finally { Gate.Release(); if (pending && !disposed) try { timer.Change(3000, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    }
    private void SaveVerification()
    {
        var state = new JsonObject(); foreach (var (id, ids) in savedVerification) state[id] = new JsonArray(ids.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()); foreach (var (id, item) in verification) state[id] = new JsonArray(item.Ids.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        JsonFiles.Atomic(Path.Combine(store.Root, "realtime-verification.json"), JsonFiles.Bytes(state));
    }
    private static string Stamp(string path) { var file = new FileInfo(path); return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing"; }
    public void Dispose() { disposed = true; cancellation.Cancel(); timer.Dispose(); foreach (var watcher in watchers) watcher.Dispose(); }
}

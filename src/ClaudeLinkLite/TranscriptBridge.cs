using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeLinkLite;

public sealed record BridgePair(string CodexId, string CodexPath, string ClaudePath, string Title, string ImportedHash)
{
    public override string ToString() => Title;
}
public sealed record BridgeMessage(string Id, string Role, string Text, string Timestamp, bool Meta = false);
public sealed record JsonLine(int Start, int End, JsonObject Record);
public sealed class BridgePlan
{
    public required BridgePair Pair;
    public List<Change> Changes { get; } = [];
    public List<BridgeMessage> FromCodex { get; } = [];
    public List<BridgeMessage> FromClaude { get; } = [];
    public string Status = "";
    public string? Blocked;
    public bool Conflict;
    public int DesktopCards;
    public string Direction = "both";
    public required byte[] CodexBefore;
    public required byte[] ClaudeBefore;
    public void Verify()
    {
        if (!JsonFiles.Same(TranscriptBridge.Snapshot(Pair.CodexPath), CodexBefore) || !JsonFiles.Same(TranscriptBridge.Snapshot(Pair.ClaudePath), ClaudeBefore))
            throw new IOException("会话在预览后发生了变化，请重新预览。");
        if (Blocked != null) throw new InvalidDataException(Blocked);
    }
}

// Incremental round-trip design informed by the MIT claudeimportfromcodex
// project. Native C# implementation; see THIRD-PARTY-NOTICES.md.
public sealed class TranscriptBridge
{
    private readonly DesktopEnvironment environment;
    private readonly AppStore store;
    private readonly string codexRoot;
    private readonly bool gatewayCards;
    private string StatePath => Path.Combine(store.Root, "transcript-bridge.json");
    private const string Marker = "[Claude Link bridge v1 ";
    private static readonly JsonSerializerOptions VisibleJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> ChainTypes = ["user", "assistant", "system", "attachment"];
    public TranscriptBridge(DesktopEnvironment env, AppStore appStore, string? home = null, bool includeGatewayCards = true)
    { environment = env; store = appStore; codexRoot = home ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(env.User, ".codex"); gatewayCards = includeGatewayCards; }
    public static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\")) path = path[4..];
        return Path.GetFullPath(path);
    }
    public List<BridgePair> Discover(bool originalOnly = false)
    {
        string ledger = Path.Combine(codexRoot, "external_agent_session_imports.json");
        if (!File.Exists(ledger)) return [];
        var mappings = JsonFiles.Read(ledger)["records"] as JsonArray ?? new();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string sessions = Path.Combine(codexRoot, "sessions");
        if (Directory.Exists(sessions)) foreach (string path in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories))
        {
            var match = Regex.Match(Path.GetFileName(path), @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$");
            if (match.Success) files[match.Groups[1].Value] = path;
        }
        var result = new List<BridgePair>();
        foreach (var item in mappings.OfType<JsonObject>())
        {
            string id = item["imported_thread_id"]?.ToString() ?? "", source = item["source_path"]?.ToString() ?? "";
            if (!Guid.TryParse(id, out _) || source.Length == 0 || !files.TryGetValue(id, out var rollout)) continue;
            source = NormalizePath(source);
            if (!source.StartsWith(NormalizePath(environment.TranscriptRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(source)) continue;
            result.Add(new(id, rollout, source, item["title"]?.ToString() ?? Path.GetFileNameWithoutExtension(source), item["content_sha256"]?.ToString() ?? ""));
        }
        if (originalOnly)
        {
            string account = store.Settings.OfficialAccount;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(account))
            {
                string scope = Path.Combine(account, environment.ActiveGroup(account, false));
                if (Directory.Exists(scope)) foreach (string file in Directory.GetFiles(scope, "local_*.json")) { string id = JsonFiles.Read(file)["cliSessionId"]?.ToString() ?? ""; if (Guid.TryParse(id, out _)) ids.Add(id); }
            }
            result = result.Where(p => ids.Contains(Path.GetFileNameWithoutExtension(p.ClaudePath))).ToList();
        }
        return result.DistinctBy(p => p.CodexId).OrderBy(p => p.Title).ToList();
    }
    public static List<JsonLine> Lines(byte[] raw)
    {
        var result = new List<JsonLine>(); int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 10) continue;
            int end = i + 1; string line = Encoding.UTF8.GetString(raw, start, end - start).Trim().TrimStart('\uFEFF');
            if (line.Length > 0)
            {
                var parsed = JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException("会话包含非对象记录。");
                result.Add(new(start, end, parsed));
            }
            start = end;
        }
        if (start != raw.Length && raw.AsSpan(start).IndexOfAnyExcept((byte)' ', (byte)'\r', (byte)'\t') >= 0)
            throw new InvalidDataException("会话末尾尚未完整写入，请结束任务后再同步。");
        return result;
    }
    private static string PrefixHash(byte[] raw, int offset) => Convert.ToHexString(SHA256.HashData(raw.AsSpan(0, offset))).ToLowerInvariant();
    internal static byte[] Snapshot(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (source.Length > 256 * 1024 * 1024) throw new InvalidDataException("此会话超过 256 MB，暂不在窗口中处理。");
        var raw = new byte[(int)source.Length]; source.ReadExactly(raw); return raw;
    }
    private static int? HashBoundary(byte[] raw, string wanted)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int start = 0;
        if (string.Equals(PrefixHash(raw, 0), wanted, StringComparison.OrdinalIgnoreCase)) return 0;
        for (int i = 0; i < raw.Length; i++) if (raw[i] == 10)
        {
            hash.AppendData(raw.AsSpan(start, i + 1 - start)); start = i + 1;
            if (Convert.ToHexString(hash.GetCurrentHash()).Equals(wanted, StringComparison.OrdinalIgnoreCase)) return start;
        }
        return null;
    }
    private static int? InitialCodexOffset(List<JsonLine> lines)
    {
        int? end = null;
        foreach (var line in lines)
        {
            if (line.Record["type"]?.ToString() != "event_msg") continue;
            var payload = line.Record["payload"]; string type = payload?["type"]?.ToString() ?? "", turn = payload?["turn_id"]?.ToString() ?? "";
            if (type == "task_started" && !turn.StartsWith("external-import-turn") && turn.Length > 0) break;
            if (type == "task_complete" && turn.StartsWith("external-import-turn")) end = line.End;
        }
        return end;
    }
    private static string Text(JsonNode? value)
    {
        if (value is JsonValue) return value.ToString();
        if (value is not JsonArray content) return value?.ToJsonString(VisibleJson) ?? "";
        return string.Join("\n", content.Select(x => x?["type"]?.ToString() switch
        {
            "text" or "input_text" or "output_text" => x?["text"]?.ToString() ?? "",
            "image" or "input_image" => "[图片：跨客户端桥接保留引用说明，不复制图片内容]",
            _ => ""
        }).Where(x => x.Length > 0));
    }
    private static string CleanUser(string text)
    {
        var request = Regex.Match(text, @"^#{1,6}\s*My request:?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (request.Success) return text[(request.Index + request.Length)..].Trim();
        return Regex.Replace(text, @"<(recommended_plugins|environment_context|user_instructions|skills_instructions|system-reminder|codex_apps_open_page_instructions)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
    }
    private static bool BridgeOwned(JsonObject record, string text) => record["claudeLinkBridge"] != null || text.StartsWith(Marker, StringComparison.Ordinal);
    public static List<BridgeMessage> CodexMessages(List<JsonLine> lines, int offset)
    {
        var result = new List<BridgeMessage>(); bool external = false;
        foreach (var line in lines)
        {
            var record = line.Record; var payload = record["payload"] as JsonObject;
            if (record["type"]?.ToString() == "event_msg")
            {
                string type = payload?["type"]?.ToString() ?? "";
                if (type == "task_started") external = payload?["turn_id"]?.ToString().StartsWith("external-import-turn") == true;
                if (type == "task_complete") external = false;
                continue;
            }
            if (line.Start < offset || external || record["type"]?.ToString() != "response_item" || payload == null) continue;
            string typeName = payload["type"]?.ToString() ?? "", role = payload["role"]?.ToString() ?? "", text = ""; bool meta = false;
            if (typeName == "message")
            {
                if (role is not ("user" or "assistant")) continue;
                text = Text(payload["content"]); if (BridgeOwned(record, text) || payload["id"]?.ToString().StartsWith("cll-bridge-") == true) continue;
                if (role == "user") text = CleanUser(text);
            }
            else if (typeName is "function_call" or "custom_tool_call")
            { role = "assistant"; text = "[Codex 工具调用 " + payload["name"] + "]\n" + (payload["arguments"]?.ToString() ?? payload["input"]?.ToString() ?? ""); meta = true; }
            else if (typeName is "function_call_output" or "custom_tool_call_output")
            { role = "user"; text = "[Codex 工具结果]\n" + Text(payload["output"]); meta = true; }
            else continue;
            if (text.Trim().Length == 0) continue;
            string id = "codex:" + line.Start + ":" + JsonFiles.Hash(Encoding.UTF8.GetBytes(payload.ToJsonString()))[..16];
            result.Add(new(id, role, text, record["timestamp"]?.ToString() ?? DateTimeOffset.UtcNow.ToString("O"), meta));
        }
        return result;
    }
    private static string ClaudeText(JsonNode? content)
    {
        if (content is not JsonArray blocks) return Text(content);
        var text = new List<string>();
        foreach (var block in blocks)
        {
            switch (block?["type"]?.ToString())
            {
                case "text": text.Add(block["text"]?.ToString() ?? ""); break;
                case "tool_use": text.Add("[Claude 工具调用 " + block["name"] + "]\n" + (block["input"]?.ToJsonString(VisibleJson) ?? "{}")); break;
                case "tool_result": text.Add("[Claude 工具结果]\n" + Text(block["content"])); break;
                case "image": text.Add("[图片：跨客户端桥接保留引用说明，不复制图片内容]"); break;
            }
        }
        return string.Join("\n\n", text.Where(x => x.Length > 0));
    }
    public static List<BridgeMessage> ClaudeMessages(List<JsonLine> lines, int offset)
    {
        var result = new List<BridgeMessage>();
        foreach (var line in lines.Where(x => x.Start >= offset))
        {
            var record = line.Record; string type = record["type"]?.ToString() ?? "";
            if (type is not ("user" or "assistant") || record["isSidechain"]?.GetValue<bool>() == true || record["isMeta"]?.GetValue<bool>() == true) continue;
            string text = ClaudeText(record["message"]?["content"]);
            if (BridgeOwned(record, text) || text.Trim().Length == 0) continue;
            string id = record["uuid"]?.ToString() ?? throw new InvalidDataException("Claude 消息缺少 UUID。");
            result.Add(new("claude:" + id, type, text, record["timestamp"]?.ToString() ?? DateTimeOffset.UtcNow.ToString("O")));
        }
        return result;
    }
    private static JsonObject? ClaudeTail(List<JsonLine> lines) => lines.Select(x => x.Record).LastOrDefault(r => ChainTypes.Contains(r["type"]?.ToString() ?? "") && r["uuid"] != null && r["isSidechain"]?.GetValue<bool>() != true);
    private static byte[] Append(byte[] before, IEnumerable<JsonObject> records)
    {
        using var output = new MemoryStream(); output.Write(before);
        if (before.Length > 0 && before[^1] != 10) output.WriteByte(10);
        foreach (var record in records) { output.Write(Encoding.UTF8.GetBytes(record.ToJsonString())); output.WriteByte(10); }
        return output.ToArray();
    }
    private static string Envelope(BridgePair pair, BridgeMessage message) => Marker + pair.CodexId + " " + message.Id + "]\n" + message.Text;
    private static List<JsonObject> ForClaude(BridgePair pair, List<BridgeMessage> messages, List<JsonLine> existing)
    {
        var tail = ClaudeTail(existing) ?? throw new InvalidDataException("Claude 会话没有可继续的消息链。");
        string parent = tail["uuid"]!.ToString(), session = tail["sessionId"]?.ToString() ?? Path.GetFileNameWithoutExtension(pair.ClaudePath);
        var output = new List<JsonObject>();
        foreach (var message in messages)
        {
            string id = Guid.NewGuid().ToString();
            var record = new JsonObject { ["type"] = message.Role, ["uuid"] = id, ["parentUuid"] = parent, ["sessionId"] = session, ["cwd"] = tail["cwd"]?.DeepClone(),
                ["version"] = tail["version"]?.DeepClone() ?? JsonValue.Create("2.1.0"), ["timestamp"] = message.Timestamp, ["isSidechain"] = false, ["userType"] = "external", ["entrypoint"] = "cli",
                ["claudeLinkBridge"] = new JsonObject { ["pair"] = pair.CodexId, ["origin"] = "codex", ["id"] = message.Id } };
            string text = Envelope(pair, message);
            if (message.Role == "assistant") record["message"] = new JsonObject { ["id"] = "msg_cll_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = "codex-bridged", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["stop_reason"] = "end_turn", ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0, ["cache_creation_input_tokens"] = 0, ["cache_read_input_tokens"] = 0 } };
            else { record["message"] = new JsonObject { ["role"] = "user", ["content"] = text }; if (message.Meta) record["isMeta"] = true; }
            output.Add(record); parent = id;
        }
        if (messages.Count > 0) output.Add(new JsonObject { ["type"] = "last-prompt", ["sessionId"] = session, ["leafUuid"] = parent, ["lastPrompt"] = messages.LastOrDefault(x => x.Role == "user")?.Text ?? "" });
        return output;
    }
    private static List<JsonObject> ForCodex(BridgePair pair, List<BridgeMessage> messages, long? nextOrdinal)
    {
        var result = new List<JsonObject>(); int index = 0;
        foreach (var message in messages)
        {
            string turn = "cll-bridge-turn-" + Guid.NewGuid();
            var user = message.Role == "user"; string text = Envelope(pair, message);
            long startedAt = DateTimeOffset.Parse(message.Timestamp).ToUnixTimeSeconds();
            result.Add(new JsonObject { ["timestamp"] = message.Timestamp, ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = turn, ["started_at"] = startedAt, ["model_context_window"] = null, ["collaboration_mode_kind"] = "default" } });
            result.Add(new JsonObject { ["timestamp"] = message.Timestamp, ["type"] = "response_item", ["claudeLinkBridge"] = new JsonObject { ["pair"] = pair.CodexId, ["origin"] = "claude", ["id"] = message.Id },
                ["payload"] = new JsonObject { ["type"] = "message", ["id"] = "cll-bridge-" + Guid.NewGuid().ToString("N"), ["role"] = message.Role, ["content"] = new JsonArray(new JsonObject { ["type"] = user ? "input_text" : "output_text", ["text"] = text }), ["phase"] = user ? null : JsonValue.Create("final_answer") } });
            result.Add(new JsonObject { ["timestamp"] = message.Timestamp, ["type"] = "event_msg", ["payload"] = nextOrdinal != null
                ? new JsonObject { ["type"] = "item_completed", ["thread_id"] = pair.CodexId, ["turn_id"] = turn, ["completed_at_ms"] = DateTimeOffset.Parse(message.Timestamp).ToUnixTimeMilliseconds(), ["item"] = new JsonObject { ["type"] = user ? "UserMessage" : "AgentMessage", ["id"] = "cll-item-" + Guid.NewGuid().ToString("N"), ["content"] = new JsonArray(user ? new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() } : new JsonObject { ["type"] = "Text", ["text"] = text }) } }
                : user ? new JsonObject { ["type"] = "user_message", ["message"] = text, ["images"] = new JsonArray(), ["local_images"] = new JsonArray(), ["text_elements"] = new JsonArray() }
                : new JsonObject { ["type"] = "agent_message", ["message"] = text, ["phase"] = "final_answer" } });
            result.Add(new JsonObject { ["timestamp"] = message.Timestamp, ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = turn, ["last_agent_message"] = user ? "" : text, ["started_at"] = startedAt } }); index++;
        }
        if (nextOrdinal != null) foreach (var record in result) record["ordinal"] = nextOrdinal++;
        return result;
    }
    private List<Change> DesktopCards(BridgePair pair, List<BridgeMessage> messages, JsonObject claudeTail, string? lastAssistant)
    {
        var result = new List<Change>(); if (messages.Count == 0) return result;
        string session = claudeTail["sessionId"]?.ToString() ?? Path.GetFileNameWithoutExtension(pair.ClaudePath);
        long activity = messages.Select(m => DateTimeOffset.Parse(m.Timestamp).ToUnixTimeMilliseconds()).DefaultIfEmpty(0).Max();
        foreach (bool gw in new[] { false, true })
        {
            if (gw && !gatewayCards) continue;
            string account = gw ? store.Settings.GatewayAccount : store.Settings.OfficialAccount; if (!Directory.Exists(account)) continue;
            string group = environment.ActiveGroup(account, gw); string directory = Path.Combine(account, group);
            var cards = Directory.Exists(directory) ? Directory.GetFiles(directory, "local_*.json") : [];
            bool found = false;
            foreach (string path in cards)
            {
                var raw = JsonFiles.Raw(path); var card = JsonFiles.Parse(raw, path); if (card["cliSessionId"]?.ToString() != session) continue;
                card["lastActivityAt"] = Math.Max(card["lastActivityAt"]?.GetValue<long>() ?? 0, activity); card["completedTurns"] = (card["completedTurns"]?.GetValue<int>() ?? 0) + messages.Count(x => x.Role == "user" && !x.Meta); card["transcriptUnavailable"] = false;
                if (lastAssistant != null) { card["lastAssistantUuid"] = lastAssistant; card.Remove("postTurnSummary"); card.Remove("postTurnSummaryFor"); }
                var user = messages.LastOrDefault(m => m.Role == "user" && !m.Meta); if (user != null) card["latestUserFrameAt"] = DateTimeOffset.Parse(user.Timestamp).ToUnixTimeMilliseconds();
                result.Add(new(path, raw, JsonFiles.Bytes(card), "刷新 Claude 会话卡片")); found = true;
            }
            if (!found)
            {
                string id = "local_" + Guid.NewGuid(); var card = new JsonObject { ["sessionId"] = id, ["cliSessionId"] = session, ["cwd"] = claudeTail["cwd"]?.DeepClone(), ["originCwd"] = claudeTail["cwd"]?.DeepClone(), ["title"] = pair.Title, ["titleSource"] = "custom", ["createdAt"] = activity, ["lastActivityAt"] = activity, ["lastFocusedAt"] = activity, ["isArchived"] = false, ["model"] = DesktopConfig.Roles[0] };
                if (lastAssistant != null) card["lastAssistantUuid"] = lastAssistant;
                result.Add(new(Path.Combine(directory, id + ".json"), null, JsonFiles.Bytes(card), "登记 Claude 桌面会话"));
            }
        }
        return result;
    }
    public BridgePlan Preview(BridgePair pair, string direction = "both")
    {
        var plan = new BridgePlan { Pair = pair, CodexBefore = [], ClaudeBefore = [], Direction = direction };
        try
        {
            JsonFiles.CheckPath(pair.CodexPath); JsonFiles.CheckPath(pair.ClaudePath);
            var codex = Snapshot(pair.CodexPath); var claude = Snapshot(pair.ClaudePath); plan.CodexBefore = codex; plan.ClaudeBefore = claude;
            if (direction is not ("both" or "to-claude" or "to-codex")) throw new InvalidDataException("未知同步方向。");
            var codexLines = Lines(codex); var claudeLines = Lines(claude);
            string history = codexLines.FirstOrDefault(l => l.Record["type"]?.ToString() == "session_meta")?.Record["payload"]?["history_mode"]?.ToString() ?? "legacy";
            if (history is not ("legacy" or "rollout" or "paginated" or "")) throw new InvalidDataException("未知 Codex 历史格式，已暂停桥接。");
            long? nextOrdinal = null;
            if (history == "paginated")
            {
                long expected = 0;
                foreach (var line in codexLines) { if (line.Record["ordinal"]?.GetValue<long>() != expected++) throw new InvalidDataException("分页记录序号不连续，已暂停桥接。"); }
                nextOrdinal = expected;
            }
            var ledger = JsonFiles.Read(StatePath); var entries = ledger["pairs"] as JsonObject ?? new();
            var state = entries[pair.CodexId] as JsonObject;
            int xo, co;
            if (state != null)
            {
                if (!NormalizePath(state["claudePath"]!.ToString()).Equals(NormalizePath(pair.ClaudePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("原始 Claude 路径已变化，请检查映射。");
                xo = state["codexOffset"]!.GetValue<int>(); co = state["claudeOffset"]!.GetValue<int>();
                if (xo > codex.Length || co > claude.Length || PrefixHash(codex, xo) != state["codexHash"]!.ToString() || PrefixHash(claude, co) != state["claudeHash"]!.ToString()) throw new InvalidDataException("同步过的历史被重写或回退，已暂停以保留两边分支。");
            }
            else
            {
                xo = InitialCodexOffset(codexLines) ?? throw new InvalidDataException("缺少 Claude 导入边界，无法确定新增内容。");
                co = HashBoundary(claude, pair.ImportedHash) ?? throw new InvalidDataException("原 Claude 历史被改写，无法恢复首次导入边界。");
            }
            plan.FromCodex.AddRange(CodexMessages(codexLines, xo)); plan.FromClaude.AddRange(ClaudeMessages(claudeLines, co));
            plan.Conflict = plan.FromCodex.Count > 0 && plan.FromClaude.Count > 0;
            byte[] afterX = codex, afterC = claude; int nextX = xo, nextC = co;
            if (direction is "both" or "to-claude")
            {
                if (plan.FromCodex.Count > 0)
                {
                    var appended = ForClaude(pair, plan.FromCodex, claudeLines);
                    afterC = Append(claude, appended);
                    plan.Changes.Add(new(pair.ClaudePath, claude, afterC, "追加 Codex 新消息到原 Claude 会话"));
                    string? assistant = appended.LastOrDefault(r => r["type"]?.ToString() == "assistant")?["uuid"]?.ToString();
                    var cards = DesktopCards(pair, plan.FromCodex, ClaudeTail(claudeLines)!, assistant); plan.DesktopCards = cards.Count; plan.Changes.AddRange(cards);
                }
                nextX = codex.Length;
                if (plan.FromClaude.Count == 0) nextC = afterC.Length;
            }
            if (direction is "both" or "to-codex")
            {
                if (plan.FromClaude.Count > 0)
                {
                    afterX = Append(codex, ForCodex(pair, plan.FromClaude, nextOrdinal));
                    plan.Changes.Add(new(pair.CodexPath, codex, afterX, "追加 Claude 新消息到原 Codex 会话"));
                }
                nextC = claude.Length;
                if (plan.FromCodex.Count == 0) nextX = afterX.Length;
            }
            if (direction == "both") { nextX = afterX.Length; nextC = afterC.Length; }
            entries[pair.CodexId] = new JsonObject { ["claudePath"] = pair.ClaudePath, ["codexPath"] = pair.CodexPath, ["codexOffset"] = nextX, ["claudeOffset"] = nextC, ["codexHash"] = PrefixHash(afterX, nextX), ["claudeHash"] = PrefixHash(afterC, nextC), ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O") };
            ledger["pairs"] ??= entries; ledger["version"] = 1;
            plan.Changes.Add(new(StatePath, JsonFiles.Raw(StatePath), JsonFiles.Bytes(ledger), "会话桥接增量游标"));
            plan.Status = $"Codex 新增 {plan.FromCodex.Count} 条 · Claude 新增 {plan.FromClaude.Count} 条";
        }
        catch (Exception ex) { plan.Blocked = ex.Message; plan.Status = ex.Message; plan.Changes.Clear(); }
        return plan;
    }
    public static void EnsureAppsClosed()
    {
        DesktopEnvironment.EnsureClosed();
        foreach (string name in new[] { "ChatGPT", "Codex", "codex", "claude" })
        {
            var processes = Process.GetProcessesByName(name); bool running = processes.Length > 0; foreach (var p in processes) p.Dispose();
            if (running) throw new InvalidOperationException("请完全退出 Codex／ChatGPT 桌面应用及两边 CLI，再同步这对会话。预览可以随时使用。");
        }
    }
    public string Apply(BridgePlan plan, bool checkProcesses = true)
    {
        if (checkProcesses) EnsureAppsClosed(); plan.Verify();
        return new FileTransaction(Path.Combine(store.Backups, "transcript-bridge")).Commit(plan.Changes, checkProcesses ? EnsureAppsClosed : null);
    }
    internal string ApplyWithGuard(BridgePlan plan, Action guard)
    {
        guard(); plan.Verify();
        return new FileTransaction(Path.Combine(store.Backups, "transcript-bridge")).Commit(plan.Changes, guard);
    }
}

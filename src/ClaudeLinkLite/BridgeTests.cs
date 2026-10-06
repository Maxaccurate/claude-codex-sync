using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public static class BridgeTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ClaudeLinkBridge-tests", Guid.NewGuid().ToString("N"));
        public DesktopEnvironment Env;
        public AppStore Store;
        public TranscriptBridge Bridge;
        public BridgePair Pair;
        public string Session = Guid.NewGuid().ToString(), Leaf = "", Group = Guid.NewGuid().ToString();
        public Fixture()
        {
            string local = Path.Combine(Root, "Local"), user = Path.Combine(Root, "User");
            string a = Path.Combine(local, "Claude", "claude-code-sessions", Guid.NewGuid().ToString()); string b = Path.Combine(local, "Claude-3p", "claude-code-sessions", Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(a, Group)); Directory.CreateDirectory(Path.Combine(b, Group)); Env = new(local, user); Store = new(Path.Combine(Root, "Data")); Store.Settings.OfficialAccount = a; Store.Settings.GatewayAccount = b;
            string library = Path.Combine(Env.GatewayRoot, "configLibrary"); JsonFiles.Atomic(Path.Combine(library, "_meta.json"), JsonFiles.Bytes(new JsonObject { ["appliedId"] = DesktopConfig.ProfileId })); JsonFiles.Atomic(Path.Combine(library, DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = Group }));
            string cp = Path.Combine(Env.TranscriptRoot, "sample", Session + ".jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(cp)!); File.WriteAllBytes(cp, []);
            string codex = Path.Combine(user, ".codex"); string id = Guid.NewGuid().ToString(); string xp = Path.Combine(codex, "sessions", "2026", "10", "06", "rollout-2026-10-06T12-00-00-" + id + ".jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(xp)!); File.WriteAllBytes(xp, []);
            Pair = new(id, xp, cp, "bridge test", ""); Claude("A", "user"); Claude("Answer A", "assistant");
            Pair = Pair with { ImportedHash = Hash(File.ReadAllBytes(cp)) };
            Add(xp, new JsonObject { ["timestamp"] = Stamp, ["type"] = "session_meta", ["payload"] = new JsonObject { ["id"] = id, ["session_id"] = id, ["timestamp"] = Stamp, ["cwd"] = Root, ["originator"] = "bridge_fixture", ["cli_version"] = "0.160.0", ["source"] = "cli", ["model_provider"] = "openai", ["history_mode"] = "legacy" } });
            Codex("A", "user", "external-import-turn-1"); Codex("Answer A", "assistant", "external-import-turn-2");
            JsonFiles.Atomic(Path.Combine(codex, "external_agent_session_imports.json"), JsonFiles.Bytes(new JsonObject { ["records"] = new JsonArray(new JsonObject { ["source_path"] = cp, ["content_sha256"] = Pair.ImportedHash, ["imported_thread_id"] = id, ["title"] = Pair.Title }) }));
            Bridge = new(Env, Store, codex);
        }
        public static string Stamp => DateTimeOffset.UtcNow.ToString("O");
        public static string Hash(byte[] raw) => JsonFiles.Hash(raw).ToLowerInvariant();
        public static void Add(string path, JsonObject record) => File.AppendAllText(path, record.ToJsonString() + "\n", new UTF8Encoding(false));
        public void Claude(string text, string role, bool bridgeEcho = false)
        {
            string id = Guid.NewGuid().ToString();
            var row = new JsonObject { ["type"] = role, ["uuid"] = id, ["parentUuid"] = Leaf.Length > 0 ? Leaf : null, ["sessionId"] = Session, ["cwd"] = Root, ["version"] = "2.1.271", ["timestamp"] = Stamp, ["isSidechain"] = false, ["message"] = new JsonObject { ["role"] = role, ["content"] = text } };
            if (bridgeEcho) row["claudeLinkBridge"] = new JsonObject { ["origin"] = "codex" };
            Add(Pair.ClaudePath, row); Leaf = id;
        }
        public void Codex(string text, string role, string? turn = null)
        {
            turn ??= Guid.NewGuid().ToString();
            Add(Pair.CodexPath, new JsonObject { ["timestamp"] = Stamp, ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_started", ["turn_id"] = turn, ["started_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } });
            Add(Pair.CodexPath, new JsonObject { ["timestamp"] = Stamp, ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = role, ["content"] = new JsonArray(new JsonObject { ["type"] = role == "user" ? "input_text" : "output_text", ["text"] = text }) } });
            Add(Pair.CodexPath, new JsonObject { ["timestamp"] = Stamp, ["type"] = "event_msg", ["payload"] = role == "user" ? new JsonObject { ["type"] = "user_message", ["message"] = text, ["images"] = new JsonArray(), ["local_images"] = new JsonArray(), ["text_elements"] = new JsonArray() } : new JsonObject { ["type"] = "agent_message", ["message"] = text, ["phase"] = "final_answer" } });
            Add(Pair.CodexPath, new JsonObject { ["timestamp"] = Stamp, ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = turn, ["last_agent_message"] = role == "assistant" ? text : "", ["started_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } });
        }
        public BridgePlan Apply(string direction = "both") { var plan = Bridge.Preview(Pair, direction); if (plan.Blocked != null) throw new Exception(plan.Blocked); Bridge.Apply(plan, false); return plan; }
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClaudeLinkBridge-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup"); Directory.Delete(Root, true);
        }
    }
    private static void Assert(bool value, string message = "Bridge assertion failed") { if (!value) throw new Exception(message); }
    private static void SetPaginated(Fixture f)
    {
        var rows = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.CodexPath)); string turn = "";
        foreach (var row in rows)
        {
            var record = row.Record; var payload = record["payload"]!;
            record["ordinal"] = rows.IndexOf(row);
            if (record["type"]?.ToString() == "session_meta") payload["history_mode"] = "paginated";
            if (record["type"]?.ToString() != "event_msg") continue;
            if (payload["type"]?.ToString() == "task_started") turn = payload["turn_id"]!.ToString();
            if (payload["type"]?.ToString() is not ("user_message" or "agent_message")) continue;
            bool user = payload["type"]!.ToString() == "user_message";
            record["payload"] = new JsonObject { ["type"] = "item_completed", ["thread_id"] = f.Pair.CodexId, ["turn_id"] = turn, ["completed_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["item"] = new JsonObject { ["type"] = user ? "UserMessage" : "AgentMessage", ["id"] = "fixture-item-" + Guid.NewGuid().ToString("N"), ["content"] = new JsonArray(user ? new JsonObject { ["type"] = "text", ["text"] = payload["message"]!.ToString(), ["text_elements"] = new JsonArray() } : new JsonObject { ["type"] = "Text", ["text"] = payload["message"]!.ToString() }) } };
        }
        File.WriteAllText(f.Pair.CodexPath, string.Concat(rows.Select(r => r.Record.ToJsonString() + "\n")), new UTF8Encoding(false));
    }
    public static string PrepareSdkFixture(string target, bool paginated = false)
    {
        using var f = new Fixture(); f.Codex("Codex B question", "user"); f.Codex("Codex B answer", "assistant"); if (paginated) SetPaginated(f); f.Apply();
        var tail = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.ClaudePath)).Last(l => l.Record["uuid"] != null); f.Leaf = tail.Record["uuid"]!.ToString(); f.Claude("Claude C question", "user"); f.Claude("Claude C answer", "assistant"); f.Apply();
        var rows = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.CodexPath)); rows[0].Record["payload"]!["cwd"] = target;
        string path = Path.Combine(target, "sessions", "2026", "10", "06", Path.GetFileName(f.Pair.CodexPath)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(rows.Select(r => r.Record.ToJsonString() + "\n")), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(target, "fixture.json"), new JsonObject { ["threadId"] = f.Pair.CodexId, ["path"] = path }.ToJsonString()); return path;
    }
    public static List<(string Name, Func<Task> Run)> Cases()
    {
        var list = new List<(string Name, Func<Task> Run)>(); void Test(string name, Action action) => list.Add((name, () => { action(); return Task.CompletedTask; }));
        Test("桥接发现原始导入映射且预览零写入", () => { using var f = new Fixture(); Assert(f.Bridge.Discover().Count == 1); var beforeC = File.ReadAllBytes(f.Pair.ClaudePath); var beforeX = File.ReadAllBytes(f.Pair.CodexPath); var p = f.Bridge.Preview(f.Pair); Assert(p.Blocked == null && p.FromCodex.Count == 0 && p.FromClaude.Count == 0); Assert(beforeC.SequenceEqual(File.ReadAllBytes(f.Pair.ClaudePath)) && beforeX.SequenceEqual(File.ReadAllBytes(f.Pair.CodexPath))); Assert(!File.Exists(Path.Combine(f.Store.Root, "transcript-bridge.json"))); });
        Test("分页历史往返保留原格式并追加完成条目", () => { using var f = new Fixture(); f.Codex("Paged B", "user"); SetPaginated(f); f.Apply(); f.Claude("Paged C", "assistant"); var p = f.Apply(); Assert(p.FromClaude.Count == 1 && p.Blocked == null); var rows = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.CodexPath)); Assert(rows[0].Record["payload"]?["history_mode"]?.ToString() == "paginated"); Assert(rows.Any(r => r.Record["payload"]?["type"]?.ToString() == "item_completed" && r.Record["payload"]?["item"]?["content"]?.ToJsonString().Contains("Paged C") == true)); Assert(f.Bridge.Preview(f.Pair).FromClaude.Count == 0); });
        Test("Codex新增追加原Claude链且登记两个当前分区", () =>
        {
            using var f = new Fixture(); string previous = f.Leaf; f.Codex("B", "user"); f.Codex("Answer B", "assistant"); var before = File.ReadAllBytes(f.Pair.ClaudePath); var p = f.Apply(); Assert(p.FromCodex.Count == 2 && p.DesktopCards == 2);
            var lines = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.ClaudePath)); var added = lines.Where(l => l.Record["claudeLinkBridge"] != null).ToList(); Assert(added.Count == 2 && added[0].Record["parentUuid"]!.ToString() == previous); Assert(added[1].Record["parentUuid"]!.ToString() == added[0].Record["uuid"]!.ToString()); Assert(added.All(l => l.Record["sessionId"]!.ToString() == f.Session)); Assert(File.ReadAllBytes(f.Pair.ClaudePath).AsSpan(0, before.Length).SequenceEqual(before));
            foreach (var card in Directory.GetFiles(f.Store.Settings.OfficialAccount, "local_*.json", SearchOption.AllDirectories)) Assert(JsonFiles.Read(card)["lastAssistantUuid"]?.ToString() == added[1].Record["uuid"]!.ToString());
        });
        Test("往返新增保持原ID并重复同步不回声", () =>
        {
            using var f = new Fixture(); f.Codex("B", "user"); f.Codex("Answer B", "assistant"); f.Apply(); var tail = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.ClaudePath)).Last(l => l.Record["uuid"] != null); f.Leaf = tail.Record["uuid"]!.ToString(); f.Claude("C", "user"); f.Claude("Answer C", "assistant"); var p = f.Apply(); Assert(p.FromClaude.Count == 2 && p.FromCodex.Count == 0);
            var again = f.Bridge.Preview(f.Pair); Assert(again.FromClaude.Count == 0 && again.FromCodex.Count == 0); var raw = File.ReadAllText(f.Pair.CodexPath); Assert(raw.Contains("Answer C") && raw.Contains(f.Pair.CodexId));
        });
        Test("同时续聊自动保留双方增量且不回声", () =>
        {
            using var f = new Fixture(); f.Codex("B branch", "user"); f.Claude("C branch", "user"); var p = f.Bridge.Preview(f.Pair); Assert(p.Conflict && p.Blocked == null && p.FromCodex.Count == 1 && p.FromClaude.Count == 1); f.Apply(); Assert(File.ReadAllText(f.Pair.CodexPath).Contains("C branch") && File.ReadAllText(f.Pair.ClaudePath).Contains("B branch")); var again = f.Bridge.Preview(f.Pair); Assert(again.Blocked == null && again.FromClaude.Count == 0 && again.FromCodex.Count == 0);
        });
        Test("增量保护日志保留历史改写前消息且重复捕获不膨胀", () => { using var f = new Fixture(); using var live = new RealtimeSync(f.Env, f.Store); Assert(live.Capture(f.Pair) > 0); Assert(live.Capture(f.Pair) == 0); f.Codex("Protected latest reply", "assistant"); Assert(live.Capture(f.Pair) > 0); File.WriteAllText(f.Pair.CodexPath, "{\"type\":\"session_meta\"}\n"); live.Capture(f.Pair); Assert(File.ReadAllText(Path.Combine(f.Store.Root, "realtime-journal", f.Pair.CodexId + ".jsonl")).Contains("Protected latest reply")); });
        Test("官方会话范围不会带入独立导入会话", () => { using var f = new Fixture(); Assert(f.Bridge.Discover(originalOnly: true).Count == 0); string path = Path.Combine(f.Store.Settings.OfficialAccount, f.Group, "local_" + Guid.NewGuid() + ".json"); JsonFiles.Atomic(path, JsonFiles.Bytes(new JsonObject { ["cliSessionId"] = f.Session })); Assert(f.Bridge.Discover(originalOnly: true).Count == 1); });
        Test("写入租约不能同时获取并在释放后恢复", () => { using var f = new Fixture(); string home = Path.Combine(f.Env.User, ".codex"); using (var lease = new CodexWriterLease(home, f.Pair.CodexId)) { bool blocked = false; try { using var other = new CodexWriterLease(home, f.Pair.CodexId); } catch (IOException) { blocked = true; } Assert(blocked); } using var next = new CodexWriterLease(home, f.Pair.CodexId); next.Check(); });
        Test("Claude完成标记避免把正在生成的半轮当作已完成", () => { using var f = new Fixture(); f.Claude("new prompt", "user"); Assert(RealtimeSync.ClaudeTurnActive(File.ReadAllBytes(f.Pair.ClaudePath))); f.Claude("final reply", "assistant"); Fixture.Add(f.Pair.ClaudePath, new JsonObject { ["type"] = "last-prompt", ["leafUuid"] = f.Leaf }); Assert(!RealtimeSync.ClaudeTurnActive(File.ReadAllBytes(f.Pair.ClaudePath))); });
        list.Add(("原生文件事件自动追赶且保留用户原文", async () =>
        {
            using var f = new Fixture(); string id = "local_" + Guid.NewGuid(); string card = Path.Combine(f.Store.Settings.OfficialAccount, f.Group, id + ".json"); JsonFiles.Atomic(card, JsonFiles.Bytes(new JsonObject { ["sessionId"] = id, ["cliSessionId"] = f.Session }));
            var live = new RealtimeSync(f.Env, f.Store, Path.Combine(f.Env.User, ".codex"), () => { }); live.Start();
            try
            {
                f.Codex("Auto B question", "user"); f.Codex("Auto B answer", "assistant");
                var start = DateTime.UtcNow; while (DateTime.UtcNow - start < TimeSpan.FromSeconds(12) && !File.ReadAllText(f.Pair.ClaudePath).Contains("Auto B answer")) await Task.Delay(100);
                Assert(File.ReadAllText(f.Pair.ClaudePath).Contains("Auto B answer")); Assert(File.ReadAllText(f.Pair.ClaudePath).Contains("Answer A"));
            }
            finally { live.Dispose(); Assert(await RealtimeSync.Gate.WaitAsync(TimeSpan.FromSeconds(10))); RealtimeSync.Gate.Release(); }
        }));
        Test("历史重写与半行写入暂停桥接", () =>
        {
            using var f = new Fixture(); f.Apply(); File.WriteAllText(f.Pair.CodexPath, "{\"type\":\"session_meta\"}\n"); Assert(f.Bridge.Preview(f.Pair).Blocked != null);
            using var g = new Fixture(); File.AppendAllText(g.Pair.ClaudePath, "{\"type\":"); Assert(g.Bridge.Preview(g.Pair).Blocked != null);
        });
        Test("官方再导入桥接来源消息不循环复制", () =>
        {
            using var f = new Fixture(); f.Codex("B", "user"); f.Apply(); f.Codex("[Claude Link bridge v1 pair codex:message]\nB", "user", "external-import-turn-99"); var p = f.Bridge.Preview(f.Pair); Assert(p.FromCodex.Count == 0 && p.FromClaude.Count == 0);
        });
        Test("预览后源文件变化阻止写入", () => { using var f = new Fixture(); f.Codex("B", "user"); var p = f.Bridge.Preview(f.Pair); f.Codex("Newer", "assistant"); bool failed = false; try { f.Bridge.Apply(p, false); } catch (IOException) { failed = true; } Assert(failed); Assert(!File.ReadAllText(f.Pair.ClaudePath).Contains("Newer")); });
        Test("被锁定的会话仅阻塞该行预览", () => { using var f = new Fixture(); using var locked = new FileStream(f.Pair.CodexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); var p = f.Bridge.Preview(f.Pair); Assert(p.Blocked != null && p.Changes.Count == 0); });
        Test("工具活动以完整文字保留且不重放异方工具名", () =>
        {
            using var f = new Fixture(); string output = new('x', 6000); f.Codex("begin", "user"); Fixture.Add(f.Pair.CodexPath, new JsonObject { ["timestamp"] = Fixture.Stamp, ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "function_call", ["name"] = "exec", ["call_id"] = "call-a", ["arguments"] = "{}" } }); Fixture.Add(f.Pair.CodexPath, new JsonObject { ["timestamp"] = Fixture.Stamp, ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call-a", ["output"] = output } }); f.Apply(); var rows = TranscriptBridge.Lines(File.ReadAllBytes(f.Pair.ClaudePath)); Assert(File.ReadAllText(f.Pair.ClaudePath).Contains(output)); Assert(!rows.Any(l => (l.Record["message"]?["content"] as JsonArray)?.Any(x => x?["type"]?.ToString() == "tool_use") == true));
        });
        return list;
    }
}

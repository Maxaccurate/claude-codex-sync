using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public sealed record SessionEntry(string Side, string Relative, string Path, byte[] Raw, JsonObject Data, long Modified)
{
    public string Cli => Data["cliSessionId"]!.ToString();
    public string Title => Data["title"]?.ToString() ?? "未命名会话";
}
public sealed class SyncPlan
{
    public List<Change> Changes { get; } = [];
    public List<string> Notes { get; } = [];
    public List<(string Title, string Status)> Rows { get; } = [];
    public int OfficialCount { get; set; }
    public int GatewayCount { get; set; }
    public int NewCount { get; set; }
    public int UpdateCount { get; set; }
    public int RelocatedCount { get; set; }
    public Dictionary<string, byte[]> Checks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void Verify()
    {
        foreach (var check in Checks) if (!JsonFiles.Same(JsonFiles.Raw(check.Key), check.Value)) throw new IOException("会话信息已经变化，请刷新预览后重试。");
    }
}

public sealed class SessionSync
{
    private static readonly string[] SharedKeys = ["cliSessionId", "cwd", "originCwd", "title", "titleSource", "previousTitles", "createdAt", "lastActivityAt", "lastFocusedAt", "isArchived", "completedTurns", "titleTurn", "postTurnSummary", "postTurnSummaryFor", "lastAssistantUuid", "gitAnchors", "gitAnchorsLookupOnly", "gitAnchorsFolderRealpath", "latestUserFrameAt", "lastSpawnRootDetected"];
    private static readonly HashSet<string> ProgressKeys = ["cliSessionId", "cwd", "originCwd", "createdAt", "lastActivityAt", "completedTurns", "postTurnSummary", "postTurnSummaryFor", "lastAssistantUuid", "gitAnchors", "gitAnchorsLookupOnly", "gitAnchorsFolderRealpath", "latestUserFrameAt", "lastSpawnRootDetected"];
    private readonly DesktopEnvironment environment;
    private readonly AppStore store;
    public SessionSync(DesktopEnvironment env, AppStore appStore) { environment = env; store = appStore; }
    private static JsonObject Shared(JsonObject obj)
    {
        var result = new JsonObject(); foreach (var key in SharedKeys) if (obj.ContainsKey(key)) result[key] = obj[key]?.DeepClone(); return result;
    }
    private static bool EqualField(JsonObject a, JsonObject b, string key) => a.ContainsKey(key) == b.ContainsKey(key) && JsonNode.DeepEquals(a[key], b[key]);
    private Dictionary<string, SessionEntry> Read(string account, string side, List<string> notes)
    {
        var result = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(account) || Directory.GetParent(account)?.Name != "claude-code-sessions" || !Guid.TryParse(Path.GetFileName(account), out _)) throw new InvalidDataException("请在会话页选择有效的 " + side + " 账号。");
        JsonFiles.CheckPath(Path.Combine(account, "placeholder"));
        foreach (var group in Directory.EnumerateDirectories(account).Where(d => Guid.TryParse(Path.GetFileName(d), out _)))
        {
            JsonFiles.CheckPath(Path.Combine(group, "placeholder"));
            foreach (var path in Directory.EnumerateFiles(group, "local_*.json"))
            {
                JsonFiles.CheckPath(path);
                var raw = File.ReadAllBytes(path);
                var data = JsonFiles.Parse(raw, path);
                string cli = data["cliSessionId"]?.ToString() ?? "";
                if (!Guid.TryParse(cli, out _) || data["transcriptUnavailable"]?.ToString() == "true") { notes.Add("记录不可用，已跳过：" + (data["title"]?.ToString() ?? "未命名")); continue; }
                if (data["sessionId"]?.ToString() != Path.GetFileNameWithoutExtension(path)) throw new InvalidDataException("会话 ID 与文件名不一致，请检查：" + path);
                if (!Directory.Exists(environment.TranscriptRoot) || !Directory.EnumerateDirectories(environment.TranscriptRoot).Any(p => File.Exists(Path.Combine(p, cli + ".jsonl")))) { notes.Add("聊天记录文件不存在，已跳过：" + (data["title"]?.ToString() ?? "未命名")); continue; }
                var rel = Path.GetRelativePath(account, path);
                result[rel] = new SessionEntry(side, rel, path, raw, data, File.GetLastWriteTimeUtc(path).Ticks);
            }
        }
        return result;
    }
    private static void ValidateRelative(string value)
    {
        var parts = value.Replace('/', '\\').Split('\\');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || !parts[1].StartsWith("local_") || !parts[1].EndsWith(".json") || !Guid.TryParse(parts[1][6..^5], out _)) throw new InvalidDataException("会话配对状态中的路径不合法。");
    }
    public SyncPlan Preview()
    {
        var plan = new SyncPlan();
        string official = store.Settings.OfficialAccount, gateway = store.Settings.GatewayAccount;
        if (official.Length == 0 || gateway.Length == 0) { plan.Notes.Add("首次接入 3P 时，请先启动一次 Claude，生成账号目录后再同步。"); return plan; }
        if (Path.GetFullPath(official).Equals(Path.GetFullPath(gateway), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("两侧账号目录不能相同。");
        string officialGroup = environment.ActiveGroup(official, false), gatewayGroup = environment.ActiveGroup(gateway, true);
        var a = Read(official, "official", plan.Notes).Where(x => string.Equals(Path.GetDirectoryName(x.Key), officialGroup, StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var b = Read(gateway, "gateway", plan.Notes);
        var officialIds = a.Values.Select(x => x.Cli).ToHashSet();
        // Old imported entries are kept for repair only when they reference the
        // selected official account. Other organizations are not swept in.
        b = b.Where(x => string.Equals(Path.GetDirectoryName(x.Key), gatewayGroup, StringComparison.OrdinalIgnoreCase) || officialIds.Contains(x.Value.Cli)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        plan.OfficialCount = a.Count; plan.GatewayCount = b.Values.Count(x => string.Equals(Path.GetDirectoryName(x.Relative), gatewayGroup, StringComparison.OrdinalIgnoreCase));
        foreach (var entry in a.Values.Concat(b.Values)) plan.Checks[entry.Path] = entry.Raw;
        var oldState = JsonFiles.Read(store.StatePath);
        bool sameAccounts = oldState["officialAccount"]?.ToString() == official && oldState["gatewayAccount"]?.ToString() == gateway;
        var newState = new JsonObject { ["officialAccount"] = official, ["gatewayAccount"] = gateway, ["version"] = 1 };
        var pairs = new JsonArray(); newState["pairs"] = pairs;
        var usedA = new HashSet<string>(); var usedB = new HashSet<string>();
        var work = new List<(SessionEntry A, SessionEntry B, JsonObject? Old)>();
        if (sameAccounts && oldState["pairs"] is JsonArray oldPairs)
        {
            foreach (var item in oldPairs.OfType<JsonObject>())
            {
                string ar = item["official"]!.ToString(), br = item["gateway"]!.ToString(); ValidateRelative(ar); ValidateRelative(br);
                usedA.Add(ar); usedB.Add(br);
                string activeRelative = Path.Combine(gatewayGroup, Path.GetFileName(br));
                if (!string.Equals(br, activeRelative, StringComparison.OrdinalIgnoreCase) && b.TryGetValue(br, out var oldEntry) && b.TryGetValue(activeRelative, out var activeEntry))
                {
                    if (!JsonNode.DeepEquals(oldEntry.Data, activeEntry.Data)) throw new InvalidDataException("当前分区已有同 ID 的不同会话，请先检查：" + oldEntry.Title);
                    usedB.Add(activeRelative);
                }
                if (a.TryGetValue(ar, out var arEntry) && b.TryGetValue(br, out var brEntry)) work.Add((arEntry, brEntry, item));
                else { pairs.Add(item.DeepClone()); plan.Notes.Add("一个已配对会话被删除或不可用，保留现状，未重新创建。"); }
            }
        }
        var remaining = a.Values.Where(x => !usedA.Contains(x.Relative)).Concat(b.Values.Where(x => !usedB.Contains(x.Relative))).GroupBy(x => x.Cli).ToList();
        foreach (var group in remaining)
        {
            var aa = group.Where(x => x.Side == "official").ToList(); var bb = group.Where(x => x.Side == "gateway").ToList();
            if (aa.Count > 1 || bb.Count > 1) { plan.Notes.Add("同侧有重复聊天记录引用，未自动合并：" + group.First().Title); continue; }
            if (aa.Count == 1 && bb.Count == 1) { work.Add((aa[0], bb[0], null)); continue; }
            var source = group.First(); bool targetOfficial = source.Side == "gateway";
            string targetAccount = targetOfficial ? official : gateway;
            var templates = targetOfficial ? a : b;
            string targetGroup = targetOfficial ? officialGroup : gatewayGroup;
            string id = "local_" + Guid.NewGuid(); string rel = Path.Combine(targetGroup!, id + ".json");
            var data = Shared(source.Data); data["sessionId"] = id;
            var template = templates.Values.OrderByDescending(x => x.Modified).FirstOrDefault();
            data["model"] = template?.Data["model"]?.DeepClone() ?? JsonValue.Create(DesktopConfig.Roles[0]);
            if (template?.Data["effort"] is JsonNode effort) data["effort"] = effort.DeepClone();
            var target = new SessionEntry(targetOfficial ? "official" : "gateway", rel, Path.Combine(targetAccount, rel), [], data, source.Modified);
            work.Add(targetOfficial ? (target, source, null) : (source, target, null));
        }
        foreach (var (ae, be, old) in work)
        {
            var merged = new JsonObject(); bool conflict = false;
            JsonObject progress;
            bool aChanged = old != null && !EqualField(ae.Data, old["baseOfficial"] as JsonObject ?? new(), "cliSessionId");
            bool bChanged = old != null && !EqualField(be.Data, old["baseGateway"] as JsonObject ?? new(), "cliSessionId");
            if (ae.Cli != be.Cli && aChanged && bChanged)
            {
                plan.Notes.Add("两侧产生不同聊天分支，保留全部原记录，暂停引用切换：" + ae.Title);
                plan.Rows.Add((ae.Title, "分支冲突，原记录已保留")); if (old != null) pairs.Add(old.DeepClone()); continue;
            }
            if (ae.Cli != be.Cli && old == null) { plan.Notes.Add("无法确认两侧记录身份：" + ae.Title); continue; }
            if (ae.Cli != be.Cli) progress = aChanged ? ae.Data : be.Data;
            else
            {
                // A focus/save operation may have a newer file mtime than a real reply.
                // Keep the complete progress bundle from the newest conversation activity.
                static long Activity(JsonObject d) => Math.Max(d["lastActivityAt"]?.GetValue<long>() ?? 0, d["latestUserFrameAt"]?.GetValue<long>() ?? 0);
                long at = Activity(ae.Data), bt = Activity(be.Data);
                if (at != bt) progress = at > bt ? ae.Data : be.Data;
                else progress = (ae.Data["completedTurns"]?.GetValue<int>() ?? 0) >= (be.Data["completedTurns"]?.GetValue<int>() ?? 0) ? ae.Data : be.Data;
            }
            foreach (var key in SharedKeys)
            {
                JsonObject selected;
                if (ProgressKeys.Contains(key)) selected = progress;
                else if (key == "lastFocusedAt") selected = (ae.Data[key]?.GetValue<long>() ?? 0) >= (be.Data[key]?.GetValue<long>() ?? 0) ? ae.Data : be.Data;
                else if (EqualField(ae.Data, be.Data, key)) selected = ae.Data;
                else if (old != null)
                {
                    var ba = old["baseOfficial"] as JsonObject ?? new(); var bb = old["baseGateway"] as JsonObject ?? new();
                    bool ac = !EqualField(ae.Data, ba, key), bc = !EqualField(be.Data, bb, key);
                    if (ac && !bc) selected = ae.Data;
                    else if (bc && !ac) selected = be.Data;
                    else if (key == "cliSessionId" && ac && bc) { conflict = true; break; }
                    else selected = ae.Modified >= be.Modified ? ae.Data : be.Data;
                }
                else if (key == "cliSessionId") { conflict = true; break; }
                else selected = ae.Modified >= be.Modified ? ae.Data : be.Data;
                if (selected.ContainsKey(key)) merged[key] = selected[key]?.DeepClone();
            }
            if (conflict)
            {
                plan.Notes.Add("两侧产生不同聊天分支，请手动选择保留哪条：" + ae.Title);
                plan.Rows.Add((ae.Title, "分支冲突，未修改"));
                if (old != null) pairs.Add(old.DeepClone());
                continue;
            }
            var desired = new List<JsonObject>(); bool anyUpdated = false, anyNew = false, anyRelocated = false;
            string finalGatewayRelative = be.Relative;
            foreach (var entry in new[] { ae, be })
            {
                var result = (JsonObject)entry.Data.DeepClone(); foreach (var key in SharedKeys) result.Remove(key);
                foreach (var prop in merged) result[prop.Key] = prop.Value?.DeepClone(); desired.Add(result);
                bool isNew = entry.Raw.Length == 0;
                bool relocate = entry.Side == "gateway" && !isNew && !string.Equals(Path.GetDirectoryName(entry.Relative), gatewayGroup, StringComparison.OrdinalIgnoreCase);
                if (relocate)
                {
                    finalGatewayRelative = Path.Combine(gatewayGroup, Path.GetFileName(entry.Relative)); ValidateRelative(finalGatewayRelative);
                    string destination = Path.Combine(gateway, finalGatewayRelative); var destinationRaw = JsonFiles.Raw(destination);
                    if (destinationRaw != null && !JsonNode.DeepEquals(JsonFiles.Parse(destinationRaw, destination), entry.Data))
                        throw new InvalidDataException("当前组织分区已有不同内容的同 ID 会话，未覆盖：" + entry.Title);
                    plan.Changes.Add(new Change(destination, destinationRaw, JsonFiles.Bytes(result), "修复显示位置：" + entry.Title));
                    plan.Changes.Add(new Change(entry.Path, entry.Raw, null, "移除旧分区登记：" + entry.Title));
                    plan.RelocatedCount++; anyRelocated = true;
                }
                else if (isNew || !JsonNode.DeepEquals(result, entry.Data))
                {
                    plan.Changes.Add(new Change(entry.Path, isNew ? null : entry.Raw, JsonFiles.Bytes(result), entry.Title));
                    if (isNew) { plan.NewCount++; anyNew = true; } else { plan.UpdateCount++; anyUpdated = true; }
                }
            }
            pairs.Add(new JsonObject { ["official"] = ae.Relative, ["gateway"] = finalGatewayRelative, ["baseOfficial"] = Shared(desired[0]), ["baseGateway"] = Shared(desired[1]) });
            plan.Rows.Add((merged["title"]?.ToString() ?? ae.Title, anyRelocated ? "待修复显示位置" : anyNew ? "待补齐到另一侧" : anyUpdated ? "待同步更改" : "当前分区已配对 · 共用记录"));
        }
        if (!JsonNode.DeepEquals(oldState, newState)) plan.Changes.Add(new Change(store.StatePath, JsonFiles.Raw(store.StatePath), JsonFiles.Bytes(newState), "会话配对状态"));
        return plan;
    }
    public void SeedExistingPairs()
    {
        if (File.Exists(store.StatePath) || store.Settings.OfficialAccount.Length == 0 || store.Settings.GatewayAccount.Length == 0) return;
        var notes = new List<string>(); var a = Read(store.Settings.OfficialAccount, "official", notes); var b = Read(store.Settings.GatewayAccount, "gateway", notes);
        var pairs = new JsonArray();
        foreach (var group in a.Values.Concat(b.Values).GroupBy(e => e.Cli))
        {
            var aa = group.Where(e => e.Side == "official").ToList(); var bb = group.Where(e => e.Side == "gateway").ToList();
            if (aa.Count == 1 && bb.Count == 1) pairs.Add(new JsonObject { ["official"] = aa[0].Relative, ["gateway"] = bb[0].Relative, ["baseOfficial"] = Shared(aa[0].Data), ["baseGateway"] = Shared(bb[0].Data) });
        }
        if (pairs.Count == 0) return;
        foreach (var entry in a.Values.Concat(b.Values)) if (!JsonFiles.Same(JsonFiles.Raw(entry.Path), entry.Raw)) return;
        var state = new JsonObject { ["officialAccount"] = store.Settings.OfficialAccount, ["gatewayAccount"] = store.Settings.GatewayAccount, ["version"] = 1, ["pairs"] = pairs };
        JsonFiles.Atomic(store.StatePath, JsonFiles.Bytes(state));
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public static class CodexIndex
{
    public static string? FindExecutable()
    {
        try
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            foreach (string package in Directory.GetDirectories(packages, "OpenAI.Codex_*").OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string bundled = Path.Combine(package, "app", "resources", "codex.exe");
                if (File.Exists(bundled)) return bundled;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        if (Directory.Exists(npm))
        {
            var paths = Directory.GetFiles(npm, "codex.exe", SearchOption.AllDirectories);
            var preferred = paths.FirstOrDefault(x => x.Contains("windows-msvc", StringComparison.OrdinalIgnoreCase) && x.Contains("x86_64", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        { string path = Path.Combine(dir, "codex.exe"); if (File.Exists(path)) return path; }
        return null;
    }
    public static async Task<int> Refresh(string threadId, string rollout, string codexHome, CancellationToken cancellation, IReadOnlyCollection<string>? expectedMessageIds = null)
    {
        string exe = FindExecutable() ?? throw new InvalidOperationException("未找到 Codex CLI，无法做索引校验。请安装 Codex CLI 后重新打开目标会话。");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://"); start.Environment["CODEX_HOME"] = codexHome;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex 索引读取器。");
        var drain = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(cancellation) != null) { } }, cancellation);
        int next = 0;
        async Task<JsonObject> Request(string method, JsonObject parameters)
        {
            int id = ++next; await process.StandardInput.WriteLineAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString()); await process.StandardInput.FlushAsync(cancellation);
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(cancellation); if (line == null) throw new IOException("Codex 索引读取器提前退出。");
                JsonObject? response; try { response = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (response?["id"]?.ToString() != id.ToString()) continue;
                if (response["error"] != null) throw new IOException("Codex 索引校验失败：" + response["error"]?["message"]);
                return response["result"] as JsonObject ?? new();
            }
        }
        try
        {
            await Request("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "claude_link_bridge", ["version"] = "0.3.0" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } });
            await process.StandardInput.WriteLineAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }.ToJsonString());
            var resumed = await Request("thread/resume", new JsonObject { ["threadId"] = threadId, ["path"] = rollout });
            // Resume flushes its native settings event and projects the durable suffix.
            // Page the stored view to verify visibility rather than waiting for idle unload.
            var read = await Request("thread/read", new JsonObject { ["threadId"] = threadId, ["includeTurns"] = true });
            var seen = new HashSet<string>();
            void Check(JsonObject response) { string body = response.ToJsonString(); foreach (var id in expectedMessageIds ?? []) if (body.Contains(id, StringComparison.Ordinal)) seen.Add(id); }
            int turns;
            if (resumed["thread"]?["historyMode"]?.ToString() == "paginated")
            {
                turns = 0; string? cursor = null; var cursors = new HashSet<string>();
                do
                {
                    var page = await Request("thread/turns/list", new JsonObject { ["threadId"] = threadId, ["itemsView"] = "full", ["sortDirection"] = "asc", ["limit"] = 100, ["cursor"] = cursor });
                    turns += (page["data"] as JsonArray)?.Count ?? 0; Check(page); cursor = page["nextCursor"]?.ToString();
                    if (cursor != null && !cursors.Add(cursor)) throw new IOException("Codex 分页游标未前进。");
                } while (cursor != null);
            }
            else { turns = (read["thread"]?["turns"] as JsonArray)?.Count ?? 0; Check(read); }
            if (expectedMessageIds != null && seen.Count != expectedMessageIds.Count) throw new IOException("Codex 未在会话列表中读到所有追加消息，请保留备份并检查索引。");
            await Request("thread/unsubscribe", new JsonObject { ["threadId"] = threadId });
            return turns;
        }
        finally
        {
            try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(true); } catch { }
            try { await drain; } catch { }
        }
    }
    public static string RootForRollout(string rollout)
    {
        for (var dir = new FileInfo(rollout).Directory; dir != null; dir = dir.Parent) if (dir.Name == "sessions") return dir.Parent!.FullName;
        throw new InvalidDataException("不是 Codex sessions 下的 rollout 文件。");
    }
}

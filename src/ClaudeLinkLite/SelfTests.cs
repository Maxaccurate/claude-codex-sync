using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace ClaudeLinkLite;

public static class SelfTests
{
    private sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
    private static string Jwt(JsonObject claims) => "test." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ClaudeLinkLite-tests", Guid.NewGuid().ToString("N"));
        public DesktopEnvironment Env;
        public AppStore Store;
        public string Group = Guid.NewGuid().ToString();
        public Fixture()
        {
            string local = Path.Combine(Root, "Local"), user = Path.Combine(Root, "User");
            string a = Path.Combine(local, "Claude", "claude-code-sessions", Guid.NewGuid().ToString());
            string b = Path.Combine(local, "Claude-3p", "claude-code-sessions", Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(a, Group)); Directory.CreateDirectory(Path.Combine(b, Group));
            Directory.CreateDirectory(Path.Combine(user, ".claude", "projects", "sample"));
            Env = new DesktopEnvironment(local, user); Store = new AppStore(Path.Combine(Root, "Data"));
            string library = Path.Combine(Env.GatewayRoot, "configLibrary");
            JsonFiles.Atomic(Path.Combine(library, "_meta.json"), JsonFiles.Bytes(new JsonObject { ["appliedId"] = DesktopConfig.ProfileId, ["entries"] = new JsonArray() }));
            JsonFiles.Atomic(Path.Combine(library, DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = Group }));
            Store.Settings.OfficialAccount = a; Store.Settings.GatewayAccount = b;
        }
        public (string Path, JsonObject Data) Add(bool gateway, string? cli = null, string title = "示例")
        {
            cli ??= Guid.NewGuid().ToString(); string sid = "local_" + Guid.NewGuid();
            var data = new JsonObject { ["sessionId"] = sid, ["cliSessionId"] = cli, ["title"] = title, ["cwd"] = "C:\\sample", ["lastActivityAt"] = 100, ["model"] = gateway ? "gateway-model" : "official-model", ["permissionMode"] = gateway ? "gateway-permission" : "official-permission" };
            string path = Path.Combine(gateway ? Store.Settings.GatewayAccount : Store.Settings.OfficialAccount, Group, sid + ".json"); JsonFiles.Atomic(path, JsonFiles.Bytes(data));
            File.WriteAllText(Path.Combine(Env.TranscriptRoot, "sample", cli + ".jsonl"), "{\"uuid\":\"turn-one\",\"content\":\"test-history\"}\n");
            return (path, data);
        }
        public SyncPlan Sync()
        {
            var plan = new SessionSync(Env, Store).Preview(); plan.Verify(); new FileTransaction(Store.Backups).Commit(plan.Changes); return plan;
        }
        public void Dispose()
        {
            string safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClaudeLinkLite-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(safeParent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup path");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
    public static int Run(string output)
    {
        var results = new List<object>();
        var tests = new List<(string Name, Func<Task> Run)>();
        void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
        Test("首次安装选择已发现的官方账号", () => { var accounts = new List<Account> { new("C:\\sample\\current", "当前官方账号", 4), new("C:\\sample\\old", "其他账号", 2) }; Assert(AccountSelection.OfficialDefault(accounts, "") == accounts[0].Path); Assert(AccountSelection.OfficialDefault(accounts, "C:\\missing") == accounts[0].Path); Assert(AccountSelection.OfficialDefault([], "") == ""); });
        Test("已保存的官方账号选择在多账号环境保留", () => { var accounts = new List<Account> { new("C:\\sample\\current", "当前官方账号", 4), new("C:\\sample\\chosen", "选定账号", 2) }; Assert(AccountSelection.OfficialDefault(accounts, accounts[1].Path) == accounts[1].Path); });
        Test("同一记录配对且保持两边模型权限", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); var plan = f.Sync();
            Assert(plan.NewCount == 0 && plan.UpdateCount == 0); Assert(JsonFiles.Read(a.Path)["model"]!.ToString() == "official-model"); Assert(JsonFiles.Read(b.Path)["permissionMode"]!.ToString() == "gateway-permission");
            Assert(new SessionSync(f.Env, f.Store).Preview().Changes.Count == 0);
        });
        Test("双向新增且不复制来源连接器授权", () =>
        {
            using var f = new Fixture(); var a = f.Add(false, title: "官方新建"); var b = f.Add(true, title: "3P新建"); a.Data["remoteMcpServersConfig"] = new JsonObject { ["secret"] = "KEEP-PRIVATE" }; JsonFiles.Atomic(a.Path, JsonFiles.Bytes(a.Data));
            var before = Directory.GetFiles(f.Env.TranscriptRoot, "*.jsonl", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);
            var plan = f.Sync(); Assert(plan.NewCount == 2);
            foreach (var change in plan.Changes.Where(c => c.Label != "会话配对状态")) Assert(JsonFiles.Read(change.Path)["remoteMcpServersConfig"] == null);
            foreach (var kv in before) Assert(File.ReadAllText(kv.Key) == kv.Value);
        });
        Test("改名与聊天进度三方合并", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync();
            a.Data["completedTurns"] = 12; a.Data["lastActivityAt"] = 999; b.Data["title"] = "3P改名"; JsonFiles.Atomic(b.Path, JsonFiles.Bytes(b.Data)); JsonFiles.Atomic(a.Path, JsonFiles.Bytes(a.Data)); f.Sync();
            Assert(JsonFiles.Read(a.Path)["title"]!.ToString() == "3P改名"); Assert(JsonFiles.Read(b.Path)["completedTurns"]!.GetValue<int>() == 12);
            var changed = JsonFiles.Read(a.Path); changed["title"] = "官方改名"; JsonFiles.Atomic(a.Path, JsonFiles.Bytes(changed)); f.Sync(); Assert(JsonFiles.Read(b.Path)["title"]!.ToString() == "官方改名");
        });
        Test("首次基准不改账号数据", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); var old = File.ReadAllBytes(a.Path); new SessionSync(f.Env, f.Store).SeedExistingPairs(); Assert(File.ReadAllBytes(a.Path).SequenceEqual(old)); Assert(File.Exists(f.Store.StatePath));
        });
        Test("旧会话的新焦点时间不能回退聊天进度或拼接不同摘要", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString());
            a.Data["lastActivityAt"] = 200; a.Data["lastFocusedAt"] = 9999; a.Data["lastAssistantUuid"] = "old-reply"; a.Data["postTurnSummaryFor"] = "old-reply"; a.Data["postTurnSummary"] = new JsonObject { ["text"] = "old" }; a.Data["completedTurns"] = 7;
            b.Data["lastActivityAt"] = 300; b.Data["lastAssistantUuid"] = "latest-reply"; b.Data["postTurnSummaryFor"] = "latest-reply"; b.Data["postTurnSummary"] = new JsonObject { ["text"] = "latest" }; b.Data["completedTurns"] = 9;
            JsonFiles.Atomic(b.Path, JsonFiles.Bytes(b.Data)); JsonFiles.Atomic(a.Path, JsonFiles.Bytes(a.Data)); File.SetLastWriteTimeUtc(a.Path, DateTime.UtcNow.AddMinutes(5)); f.Sync();
            foreach (var p in new[] { a.Path, b.Path }) { var d = JsonFiles.Read(p); Assert(d["lastAssistantUuid"]!.ToString() == "latest-reply" && d["postTurnSummaryFor"]!.ToString() == "latest-reply" && d["completedTurns"]!.GetValue<int>() == 9 && d["postTurnSummary"]?["text"]?.ToString() == "latest"); }
        });
        Test("单侧聊天记录 ID 变化同步引用", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync(); string fresh = Guid.NewGuid().ToString(); File.WriteAllText(Path.Combine(f.Env.TranscriptRoot, "sample", fresh + ".jsonl"), "{}\n"); b.Data["cliSessionId"] = fresh; JsonFiles.Atomic(b.Path, JsonFiles.Bytes(b.Data)); f.Sync(); Assert(JsonFiles.Read(a.Path)["cliSessionId"]!.ToString() == fresh);
        });
        Test("双侧分支冲突不会合并正文", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync();
            foreach (var entry in new[] { a, b }) { string fresh = Guid.NewGuid().ToString(); File.WriteAllText(Path.Combine(f.Env.TranscriptRoot, "sample", fresh + ".jsonl"), "{}\n"); entry.Data["cliSessionId"] = fresh; JsonFiles.Atomic(entry.Path, JsonFiles.Bytes(entry.Data)); }
            var plan = new SessionSync(f.Env, f.Store).Preview(); Assert(plan.UpdateCount == 0 && plan.NewCount == 0); Assert(plan.Notes.Any(n => n.Contains("分支")));
        });
        Test("已删除登记不会被重新创建", () => { using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync(); File.Delete(b.Path); var plan = new SessionSync(f.Env, f.Store).Preview(); Assert(plan.NewCount == 0); Assert(!File.Exists(b.Path)); });
        Test("重复引用不按标题乱配对", () => { using var f = new Fixture(); var a = f.Add(false); f.Add(false, a.Data["cliSessionId"]!.ToString()); f.Add(true, a.Data["cliSessionId"]!.ToString()); var plan = new SessionSync(f.Env, f.Store).Preview(); Assert(plan.NewCount == 0); Assert(plan.Notes.Any(n => n.Contains("重复"))); });
        Test("损坏 JSON 不写入任何账号", () => { using var f = new Fixture(); var a = f.Add(false); File.WriteAllText(a.Path, "{broken"); Throws(() => new SessionSync(f.Env, f.Store).Preview()); Assert(!File.Exists(f.Store.StatePath)); });
        Test("事务预检阻止并发改动", () => { using var f = new Fixture(); var a = f.Add(false); var plan = new SessionSync(f.Env, f.Store).Preview(); JsonFiles.Atomic(a.Path, JsonFiles.Bytes(new JsonObject())); Throws(plan.Verify); });
        Test("写入中失败会回退且有备份", () =>
        {
            using var f = new Fixture(); string p1 = Path.Combine(f.Root, "one.json"), p2 = Path.Combine(f.Root, "two.json"); File.WriteAllText(p1, "old"); int calls = 0;
            Throws(() => new FileTransaction(f.Store.Backups).Commit([new(p1, Encoding.UTF8.GetBytes("old"), Encoding.UTF8.GetBytes("new"), "one"), new(p2, null, Encoding.UTF8.GetBytes("new-two"), "two")], () => { if (++calls == 3) throw new IOException("simulated start"); }));
            Assert(File.ReadAllText(p1) == "old"); Assert(!File.Exists(p2)); Assert(Directory.GetFiles(f.Store.Backups, "manifest.json", SearchOption.AllDirectories).Length == 1);
        });
        Test("旧分区会话修复到Claude当前组织且更新配对状态", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync();
            string active = "00000000-0000-4000-8000-000000000001";
            JsonFiles.Atomic(Path.Combine(f.Env.GatewayRoot, "configLibrary", DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = active }));
            var before = Directory.GetFiles(f.Env.TranscriptRoot, "*.jsonl", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);
            var preview = new SessionSync(f.Env, f.Store).Preview(); Assert(preview.GatewayCount == 0 && preview.RelocatedCount == 1);
            new FileTransaction(f.Store.Backups).Commit(preview.Changes); string dest = Path.Combine(f.Store.Settings.GatewayAccount, active, Path.GetFileName(b.Path));
            Assert(File.Exists(dest) && !File.Exists(b.Path)); Assert(JsonFiles.Read(dest)["sessionId"]!.ToString() == b.Data["sessionId"]!.ToString());
            var after = new SessionSync(f.Env, f.Store).Preview(); Assert(after.GatewayCount == 1 && after.RelocatedCount == 0 && after.Changes.Count == 0);
            foreach (var entry in before) Assert(File.ReadAllText(entry.Key) == entry.Value);
        });
        Test("新会话写入当前分区而非数量最多的旧目录", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Add(false, title: "new-official");
            string active = "00000000-0000-4000-8000-000000000001";
            JsonFiles.Atomic(Path.Combine(f.Env.GatewayRoot, "configLibrary", DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = active }));
            var plan = f.Sync(); Assert(plan.RelocatedCount == 1 && plan.NewCount == 1);
            Assert(Directory.GetFiles(Path.Combine(f.Store.Settings.GatewayAccount, active), "local_*.json").Length == 2);
        });
        Test("分区移动失败回退源登记和新目标文件", () =>
        {
            using var f = new Fixture(); var a = f.Add(false); var b = f.Add(true, a.Data["cliSessionId"]!.ToString()); f.Sync();
            string active = "00000000-0000-4000-8000-000000000001";
            JsonFiles.Atomic(Path.Combine(f.Env.GatewayRoot, "configLibrary", DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = active }));
            var plan = new SessionSync(f.Env, f.Store).Preview(); int calls = 0;
            Throws(() => new FileTransaction(f.Store.Backups).Commit(plan.Changes, () => { if (++calls == 4) throw new IOException("injected failure"); }));
            Assert(File.Exists(b.Path)); Assert(!File.Exists(Path.Combine(f.Store.Settings.GatewayAccount, active, Path.GetFileName(b.Path))));
        });
        Test("不会把无关组织的未配对会话带到官方账户", () =>
        {
            using var f = new Fixture(); f.Add(false, title: "official"); var unrelated = f.Add(true, title: "unrelated");
            string active = "00000000-0000-4000-8000-000000000001";
            JsonFiles.Atomic(Path.Combine(f.Env.GatewayRoot, "configLibrary", DesktopConfig.ProfileId + ".json"), JsonFiles.Bytes(new JsonObject { ["deploymentOrganizationUuid"] = active }));
            var plan = f.Sync(); Assert(plan.NewCount == 1 && plan.RelocatedCount == 0); Assert(File.Exists(unrelated.Path)); Assert(Directory.GetFiles(f.Store.Settings.OfficialAccount, "local_*.json", SearchOption.AllDirectories).Length == 1);
        });
        Test("3P与官方切换保留其他配置和第三方配置条目", () =>
        {
            using var f = new Fixture(); var original = new JsonObject { ["preferences"] = new JsonObject { ["retained"] = true }, ["mcpServers"] = new JsonObject { ["keep"] = "value" } }; JsonFiles.Atomic(f.Env.NormalConfig, JsonFiles.Bytes(original));
            string metaPath = Path.Combine(f.Env.GatewayRoot, "configLibrary", "_meta.json"); JsonFiles.Atomic(metaPath, JsonFiles.Bytes(new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["id"] = "existing-profile", ["name"] = "existing" }), ["appliedId"] = "existing-profile" }));
            var p = new Provider { BaseUrl = "https://example.invalid", Sonnet = "mapped-model" }; p.Key = "test-secret";
            new FileTransaction(f.Store.Backups).Commit(DesktopConfig.Plan(f.Env, p, "http://127.0.0.1:21891", "local-token"));
            Assert(JsonFiles.Read(f.Env.NormalConfig)["deploymentMode"]!.ToString() == "3p"); Assert(JsonFiles.Read(metaPath)["entries"]!.AsArray().Count == 2);
            new FileTransaction(f.Store.Backups).Commit(DesktopConfig.Plan(f.Env, null, "", ""));
            Assert(JsonFiles.Read(f.Env.NormalConfig)["preferences"]?["retained"]?.GetValue<bool>() == true); Assert(JsonFiles.Read(metaPath)["entries"]!.AsArray().Count == 1); Assert(JsonFiles.Read(f.Env.NormalConfig)["deploymentMode"]!.ToString() == "1p");
            Assert(!File.ReadAllText(Path.Combine(f.Env.GatewayRoot, "configLibrary", DesktopConfig.ProfileId + ".json")).Contains("test-secret"));
        });
        Test("DPAPI密钥存储不含明文", () => { using var f = new Fixture(); var p = new Provider { Name = "test" }; p.Key = "DO-NOT-STORE-PLAINTEXT"; f.Store.Settings.Providers.Add(p); f.Store.Save(); Assert(!File.ReadAllText(f.Store.SettingsPath).Contains("DO-NOT-STORE-PLAINTEXT")); Assert(new AppStore(f.Store.Root).Settings.Providers[0].Key == "DO-NOT-STORE-PLAINTEXT"); });
        Test("URL规范化及认证校验", () => { Assert(LocalGateway.Endpoint("https://example.invalid/v1", "messages") == "https://example.invalid/v1/messages"); Assert(LocalGateway.Endpoint("https://example.invalid/v1/chat/completions", "models") == "https://example.invalid/v1/models"); Assert(LocalGateway.Authorized("token", "token")); Assert(!LocalGateway.Authorized("wrong", "token")); });
        Test("OpenAI工具调用往返转换", () =>
        {
            var a = JsonNode.Parse("""{"model":"claude-sonnet-5","system":[{"type":"text","text":"System"}],"messages":[{"role":"assistant","content":[{"type":"tool_use","id":"call_1","name":"read_file","input":{"path":"a"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"contents"}]}],"tools":[{"name":"read_file","input_schema":{"type":"object"}}],"max_tokens":100,"stream":false}""")!.AsObject();
            var c = ProtocolConversion.ToOpenAi(a, "test-model"); Assert(c["messages"]?[2]?["role"]?.ToString() == "tool"); Assert(c["messages"]?[1]?["tool_calls"]?[0]?["function"]?["name"]?.ToString() == "read_file");
            var b = JsonNode.Parse("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_2","function":{"name":"write_file","arguments":"{\"path\":\"b\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":8,"completion_tokens":4}}""")!.AsObject();
            var result = ProtocolConversion.ToAnthropic(b, "claude-sonnet-5"); Assert(result["stop_reason"]!.ToString() == "tool_use"); Assert(result["content"]?[0]?["input"]?["path"]?.ToString() == "b");
        });
        tests.Add(("流式分片工具参数和usage转换", async () =>
        {
            var frames = new[] { """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file","arguments":"{\"path\":"}}]},"finish_reason":null}]}""", """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a\"}"}}]},"finish_reason":"tool_calls"}]}""", """{"choices":[],"usage":{"prompt_tokens":8,"completion_tokens":4}}""", "[DONE]" };
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("", frames.Select(s => "data: " + s + "\n\n")))); using var outputStream = new MemoryStream(); await LocalGateway.ConvertStream(input, outputStream, "claude-sonnet-5", CancellationToken.None);
            string events = Encoding.UTF8.GetString(outputStream.ToArray()); Assert(events.Contains("input_json_delta")); Assert(events.Contains("message_stop")); Assert(events.Contains("\"output_tokens\":4")); Assert(events.Contains("\"stop_reason\":\"tool_use\""));
        }));
        tests.Add(("本地网关完整请求映射且拒绝错误密钥", async () =>
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
            using var upstream = new HttpListener(); upstream.Prefixes.Add($"http://127.0.0.1:{port}/"); upstream.Start();
            var received = Task.Run(async () => { var context = await upstream.GetContextAsync(); using var read = new StreamReader(context.Request.InputStream); var body = JsonNode.Parse(await read.ReadToEndAsync()); Assert(body?["model"]?.ToString() == "mock-model"); Assert(context.Request.Headers["Authorization"] == "Bearer test-api-key"); byte[] response = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""); context.Response.ContentType = "application/json"; context.Response.ContentLength64 = response.Length; await context.Response.OutputStream.WriteAsync(response); context.Response.Close(); });
            using var gateway = new LocalGateway(); var p = new Provider { BaseUrl = $"http://127.0.0.1:{port}", Protocol = "OpenAI", Sonnet = "mock-model" }; p.Key = "test-api-key"; gateway.Start(p, "local-test-token");
            using var client = new HttpClient(); using var wrong = new HttpRequestMessage(HttpMethod.Get, gateway.BaseUrl + "/v1/models"); var unauthorized = await client.SendAsync(wrong); Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized);
            using var request = new HttpRequestMessage(HttpMethod.Post, gateway.BaseUrl + "/v1/messages"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-test-token"); request.Content = new StringContent("""{"model":"claude-sonnet-5","messages":[{"role":"user","content":"Hi"}],"max_tokens":10,"stream":false}""", Encoding.UTF8, "application/json");
            using var result = await client.SendAsync(request); Assert(result.IsSuccessStatusCode); var parsed = JsonNode.Parse(await result.Content.ReadAsStringAsync()); Assert(parsed?["content"]?[0]?["text"]?.ToString() == "Hello"); await received.WaitAsync(TimeSpan.FromSeconds(10));
        }));
        Test("订阅请求使用Codex契约和扁平function工具", () =>
        {
            var body = JsonNode.Parse("""{"system":"System","messages":[{"role":"assistant","content":[{"type":"tool_use","id":"call-a","name":"read","input":{"path":"a"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-a","content":"contents"}]}],"tools":[{"name":"read","input_schema":{"type":"object"}},{"name":"write","input_schema":{"type":"object"}}],"tool_choice":{"type":"tool","name":"read","disable_parallel_tool_use":true},"temperature":0.5,"max_tokens":1000,"stream":false}""")!.AsObject();
            var result = CodexBridge.Request(body, "gpt-6.1-sol", "scope"); Assert(result["store"]!.GetValue<bool>() == false); Assert(result["stream"]!.GetValue<bool>()); Assert(result["tools"]!.AsArray().Count == 1); Assert(result["tool_choice"]!.ToString() == "required"); Assert(result["max_output_tokens"] == null && result["temperature"] == null); Assert(result["input"]?[1]?["type"]?.ToString() == "function_call_output"); Assert(result["include"]!.AsArray().Any(x => x!.ToString() == "reasoning.encrypted_content"));
        });
        Test("订阅推理上下文按模型与账号隔离", () =>
        {
            var item = new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = "opaque-context", ["summary"] = new JsonArray() };
            string signature = CodexBridge.EncodeReasoning(item, "gpt-6.1-sol", "account-a"); Assert(CodexBridge.DecodeReasoning(signature, "gpt-6.1-sol", "account-a") != null); Assert(CodexBridge.DecodeReasoning(signature, "other-model", "account-a") == null); Assert(CodexBridge.DecodeReasoning(signature, "gpt-6.1-sol", "account-b") == null);
            var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "redacted_thinking", ["data"] = signature }, new JsonObject { ["type"] = "text", ["text"] = "reply" }) }) };
            var result = CodexBridge.Request(body, "gpt-6.1-sol", "account-a"); Assert(result["input"]?[0]?["type"]?.ToString() == "reasoning"); Assert(result["input"]!.AsArray().Count == 2);
        });
        Test("Codex模型列表兼容多种返回格式", () =>
        {
            Assert(CodexOAuth.ParseModels(JsonNode.Parse("""{"models":[{"slug":"gpt-6.1-sol"},"gpt-6-sol"]}""")!).Count == 2);
            Assert(CodexOAuth.ParseModels(JsonNode.Parse("""{"models":{"first":{"id":"gpt-6.1-sol"},"second":"gpt-6-sol"}}""")!).Count == 2);
        });
        Test("已有Codex登录只导入到加密本地存储", () =>
        {
            using var f = new Fixture(); string access = Jwt(new JsonObject { ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 }); string id = Jwt(new JsonObject { ["email"] = "test@example.invalid", ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "workspace-test" } });
            var auth = new JsonObject { ["auth_mode"] = "chatgpt", ["tokens"] = new JsonObject { ["access_token"] = access, ["refresh_token"] = "TEST-REFRESH-SECRET", ["id_token"] = id, ["account_id"] = "workspace-test" } };
            string path = Path.Combine(f.Env.User, ".codex", "auth.json"); JsonFiles.Atomic(path, JsonFiles.Bytes(auth)); byte[] before = File.ReadAllBytes(path);
            using var oauth = new CodexOAuth(f.Store); Assert(oauth.ImportExisting(f.Env.User).Count == 1); oauth.ImportExisting(f.Env.User); Assert(f.Store.Settings.CodexAccounts.Count == 1);
            string saved = File.ReadAllText(f.Store.SettingsPath); Assert(!saved.Contains("TEST-REFRESH-SECRET") && !saved.Contains(access)); Assert(File.ReadAllBytes(path).SequenceEqual(before));
        });
        tests.Add(("并发订阅刷新只调用服务器一次并保存新令牌", async () =>
        {
            using var f = new Fixture(); int calls = 0; string token = Jwt(new JsonObject { ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 });
            var credential = new CodexCredential { AccountId = "workspace-test", Email = "test@example.invalid", ProtectedRefresh = Secrets.Protect("old-refresh"), ExpiresAt = 0 }; f.Store.Settings.CodexAccounts.Add(credential); f.Store.Save();
            using var handler = new MockHandler(async request => { Interlocked.Increment(ref calls); string form = await request.Content!.ReadAsStringAsync(); Assert(form.Contains("grant_type=refresh_token")); await Task.Delay(30); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["access_token"] = token, ["refresh_token"] = "new-refresh" }.ToJsonString()) }; });
            using var oauth = new CodexOAuth(f.Store, handler, "https://example.invalid/token"); await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => oauth.Access(credential.Id, CancellationToken.None)));
            Assert(calls == 1); Assert(Secrets.Reveal(new AppStore(f.Store.Root).Settings.CodexAccounts[0].ProtectedRefresh) == "new-refresh");
        }));
        tests.Add(("设备代码登录使用服务端verifier且可导入凭据", async () =>
        {
            using var f = new Fixture(); string access = Jwt(new JsonObject { ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 }); string id = Jwt(new JsonObject { ["email"] = "test@example.invalid", ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "workspace-test" } });
            using var handler = new MockHandler(async request =>
            {
                string body = await request.Content!.ReadAsStringAsync(); JsonObject response;
                if (request.RequestUri!.AbsolutePath.EndsWith("usercode")) response = new() { ["device_auth_id"] = "device-test", ["user_code"] = "user-test", ["interval"] = "5", ["expires_in"] = 900 };
                else if (request.RequestUri.AbsolutePath.Contains("deviceauth/token")) response = new() { ["authorization_code"] = "auth-test", ["code_verifier"] = "server-verifier" };
                else { Assert(body.Contains("code_verifier=server-verifier") && body.Contains("grant_type=authorization_code")); response = new() { ["access_token"] = access, ["id_token"] = id, ["refresh_token"] = "new-refresh" }; }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
            });
            using var oauth = new CodexOAuth(f.Store, handler, "https://example.invalid/token"); var authorization = await oauth.BeginDevice(CancellationToken.None); var credential = await oauth.PollDevice(authorization, CancellationToken.None); Assert(credential!.AccountId == "workspace-test"); Assert(f.Store.Settings.CodexAccounts.Count == 1);
        }));
        tests.Add(("订阅Responses流式工具调用与推理签名转换", async () =>
        {
            var frames = new[] {
                """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","encrypted_content":"opaque","summary":[]}}""",
                """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call-1","name":"read_file","arguments":""}}""",
                """{"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"path\":\"a\"}"}""",
                """{"type":"response.function_call_arguments.done","output_index":1,"arguments":"{\"path\":\"a\"}"}""",
                """{"type":"response.completed","response":{"status":"completed","output":[{"type":"reasoning","encrypted_content":"opaque","summary":[]},{"type":"function_call","call_id":"call-1","name":"read_file","arguments":"{\"path\":\"a\"}"}],"usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":3},"output_tokens":4}}}""" };
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(frames.Select(x => "data: " + x + "\n\n")))); using var output = new MemoryStream();
            var completed = await CodexBridge.Stream(input, output, "claude-sonnet-5", "gpt-6.1-sol", "scope", CancellationToken.None); string events = Encoding.UTF8.GetString(output.ToArray());
            Assert(events.Contains("redacted_thinking") && events.Contains("input_json_delta") && events.Contains("\"stop_reason\":\"tool_use\"") && events.Contains("\"input_tokens\":7"));
            var final = CodexBridge.Final(completed, "claude-sonnet-5", "gpt-6.1-sol", "scope"); Assert(final["content"]!.AsArray().Count == 2);
        }));
        tests.Add(("订阅连接提前断开不能伪装成功", async () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
            bool failed = false; try { await CodexBridge.Stream(input, Stream.Null, "claude-sonnet-5", "gpt-6.1-sol", "scope", CancellationToken.None); } catch (InvalidDataException) { failed = true; } Assert(failed);
        }));
        tests.Add(("Codex完成事件省略output时从完成条目恢复", async () =>
        {
            string frames = "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"report_ok\",\"arguments\":\"{\\\"ok\\\":true}\"}}\n\n"
                + "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames)); var terminal = await CodexBridge.Stream(input, Stream.Null, "claude-sonnet-5", "gpt-6.1-sol", "scope", CancellationToken.None);
            Assert(terminal["output"]!.AsArray().Count == 1); Assert(CodexBridge.Final(terminal, "claude-sonnet-5", "gpt-6.1-sol", "scope")["content"]?[0]?["input"]?["ok"]?.GetValue<bool>() == true);
        }));
        tests.AddRange(BridgeTests.Cases());
        int failed = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); results.Add(new { name = test.Name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name = test.Name, passed = false, error = ex.ToString() }); }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { total = tests.Count, passed = tests.Count - failed, failed, results }, JsonFiles.Options)); return failed > 0 ? 1 : 0;
    }
}

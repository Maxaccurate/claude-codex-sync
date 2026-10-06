using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Headers;

namespace ClaudeLinkLite;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--writer-lease-test"))
            {
                var fixture = JsonFiles.Read(args[^2]); bool held;
                try { using var lease = new CodexWriterLease(Path.GetDirectoryName(args[^2])!, fixture["threadId"]!.ToString()); held = true; } catch (IOException) { held = false; }
                File.WriteAllText(args.Last(), new JsonObject { ["acquired"] = held }.ToJsonString()); return 0;
            }
            if (args.Contains("--self-test")) return SelfTests.Run(args.Last());
            if (args.Contains("--bridge-sdk-fixture")) { BridgeTests.PrepareSdkFixture(args.Last(), args.Contains("--paginated")); return 0; }
            if (args.Contains("--bridge-index-test"))
            {
                bool paginated = args.Contains("--paginated");
                string testRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args.Last())!, paginated ? "bridge-index-csharp-paginated" : "bridge-index-csharp-v2"));
                BridgeTests.PrepareSdkFixture(testRoot, paginated); var fixture = JsonFiles.Read(Path.Combine(testRoot, "fixture.json"));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                int turns = CodexIndex.Refresh(fixture["threadId"]!.ToString(), fixture["path"]!.ToString(), testRoot, cancellation.Token, ["Claude C question", "Claude C answer"]).GetAwaiter().GetResult();
                File.WriteAllText(args.Last(), new JsonObject { ["readSucceeded"] = true, ["turns"] = turns }.ToJsonString()); return 0;
            }
            var env = new DesktopEnvironment();
            string root = Path.Combine(AppContext.BaseDirectory, args.Contains("--service-preview") ? "PreviewData" : "Data");
            int dataArgument = Array.IndexOf(args, "--data-dir");
            if (dataArgument >= 0) root = Path.GetFullPath(args[dataArgument + 1]);
            var store = new AppStore(root);
            if (args.Contains("--enable-automatic")) { store.Settings.AutomaticBridge = true; store.Save(); }
            if (args.Contains("--bridge-inspect"))
            {
                var bridge = new TranscriptBridge(env, store);
                var rows = bridge.Discover().Select(pair => { var plan = bridge.Preview(pair); return new { title = pair.Title, codexId = pair.CodexId, codexAdded = plan.FromCodex.Count, claudeAdded = plan.FromClaude.Count, status = plan.Status, blocked = plan.Blocked, conflict = plan.Conflict }; }).ToList();
                File.WriteAllText(args.Last(), JsonSerializer.Serialize(new { pairs = rows.Count, rows }, JsonFiles.Options)); return 0;
            }
            if (args.Contains("--launch-claude")) { DesktopEnvironment.EnsureClosed(); env.Launch(); return 0; }
            if (args.Contains("--repair-session-scope"))
            {
                DesktopEnvironment.EnsureClosed();
                var plan = new SessionSync(env, store).Preview(); plan.Verify();
                var backup = new FileTransaction(store.Backups).Commit(plan.Changes, DesktopEnvironment.EnsureClosed);
                store.Settings.LastSync = DateTime.Now.ToString("MM-dd HH:mm"); store.Save();
                var after = new SessionSync(env, store).Preview();
                File.WriteAllText(args.Last(), JsonSerializer.Serialize(new { relocated = plan.RelocatedCount, added = plan.NewCount, updated = plan.UpdateCount, officialVisible = after.OfficialCount, gatewayVisible = after.GatewayCount, pendingRepair = after.RelocatedCount, backup }, JsonFiles.Options)); return 0;
            }
            if (args.Contains("--subscription-generate-probe"))
            {
                using var oauth = new CodexOAuth(store); var account = store.Settings.CodexAccounts.FirstOrDefault() ?? throw new InvalidDataException("未找到订阅登录。");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var access = oauth.Access(account.Id, cancellation.Token).GetAwaiter().GetResult();
                var body = JsonNode.Parse("""{"system":"This is a connection test. Call report_ok once with ok=true. No other work is requested.","messages":[{"role":"user","content":"Call the test tool now."}],"tools":[{"name":"report_ok","description":"Reports successful connectivity.","input_schema":{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}}],"tool_choice":{"type":"tool","name":"report_ok"},"stream":true}""")!.AsObject();
                string model = "gpt-6.1-sol";
                using var client = new HttpClient(Network.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Post, CodexOAuth.Backend + "/responses"); CodexOAuth.Headers(request, access.Token, access.Account); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new ByteArrayContent(JsonFiles.Bytes(CodexBridge.Request(body, model, account.Id))); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) throw new IOException("订阅推理测试返回 HTTP " + (int)response.StatusCode);
                using var stream = response.Content.ReadAsStreamAsync(cancellation.Token).GetAwaiter().GetResult();
                var completed = CodexBridge.Stream(stream, Stream.Null, "claude-sonnet-5", model, account.Id, cancellation.Token).GetAwaiter().GetResult();
                var converted = CodexBridge.Final(completed, "claude-sonnet-5", model, account.Id);
                bool passed = (converted["content"] as JsonArray)?.Any(x => x?["type"]?.ToString() == "tool_use" && x?["name"]?.ToString() == "report_ok" && x?["input"]?["ok"]?.GetValue<bool>() == true) == true;
                File.WriteAllText(args.Last(), JsonSerializer.Serialize(new { passed, model, toolCall = "report_ok", usage = converted["usage"], content = converted["content"], output = completed["output"] }, JsonFiles.Options)); return passed ? 0 : 1;
            }
            if (args.Contains("--subscription-probe"))
            {
                using var oauth = new CodexOAuth(store);
                oauth.ImportExisting(env.User);
                var account = store.Settings.CodexAccounts.FirstOrDefault() ?? throw new InvalidDataException("未找到可导入的订阅登录。");
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                var models = oauth.Models(account.Id, cancellation.Token).GetAwaiter().GetResult();
                string preferred = models.Contains("gpt-6.1-sol") ? "gpt-6.1-sol" : models.FirstOrDefault() ?? "";
                var provider = store.Settings.Providers.FirstOrDefault(p => p.Protocol == "Codex" && p.CredentialId == account.Id);
                if (provider == null)
                {
                    provider = new Provider { Name = "ChatGPT Subscription", Protocol = "Codex", BaseUrl = CodexOAuth.Backend, CredentialId = account.Id, Sonnet = preferred, Opus = preferred, Haiku = preferred };
                    store.Settings.Providers.Add(provider); store.Save();
                }
                File.WriteAllText(args.Last(), JsonSerializer.Serialize(new { loggedIn = true, modelCount = models.Count, preferredModel = preferred, generatedRequest = false }, JsonFiles.Options)); return 0;
            }
            if (args.Contains("--import-existing"))
            {
                int count = 0;
                foreach (var p in Imports.Providers(env)) if (!store.Settings.Providers.Any(x => x.Name == p.Name && x.BaseUrl == p.BaseUrl)) { store.Settings.Providers.Add(p); count++; }
                store.Save(); File.WriteAllText(args.Last(), "Imported " + count + " providers; keys protected with Windows DPAPI."); return 0;
            }
            if (args.Contains("--inspect"))
            {
                string output = args.Last();
                var providers = Imports.Providers(env);
                var data = new { officialAccounts = env.Accounts(false).Select(a => new { a.Label, a.Count }), gatewayAccounts = env.Accounts(true).Select(a => new { a.Label, a.Count }), mode = env.IsGateway() ? "3p" : "1p", importedProviders = providers.Select(p => new { p.Name, p.Protocol, modelMapping = p.Routed }), family = env.PackageFamily };
                File.WriteAllText(output, JsonSerializer.Serialize(data, JsonFiles.Options)); return 0;
            }
            if (args.Contains("--service-preview"))
            {
                var demo = new Provider { Name = "我的 3P 服务", BaseUrl = "https://api.example.com/v1", Sonnet = "deepseek-chat", Protocol = "OpenAI" }; demo.Key = "preview-only";
                store.Settings.Providers = [demo]; store.Settings.LastProvider = demo.Id;
            }
            using var single = new Mutex(true, "Local\\ClaudeLinkLite.UI", out bool created);
            if (!created) { MessageBox.Show("Claude Link Lite 已经打开，请切回现有窗口。", "Claude Link Lite"); return 0; }
            if (args.Contains("--render-preview") || args.Contains("--provider-settings"))
            {
                using var legacy = new MainForm(store, env);
                if (args.Contains("--render-preview")) { legacy.SavePreview(args.Last(), args.Contains("--history"), args.Contains("--bridge")); return 0; }
                Application.Run(legacy); return 0;
            }
            using var form = new SyncForm(store, env, !args.Contains("--sync-preview"));
            if (args.Contains("--sync-preview")) { form.SavePreview(args.Last()); return 0; }
            Application.Run(form); return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), ex.ToString()); return 1; }
            MessageBox.Show(ex.Message, "Claude Link Lite", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
}

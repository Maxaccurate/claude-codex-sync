using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public static class ProtocolConversion
{
    private static string Text(JsonNode? content)
    {
        if (content is JsonValue) return content.ToString();
        if (content is not JsonArray blocks) return "";
        return string.Join("\n", blocks.Where(b => b?["type"]?.ToString() == "text").Select(b => b?["text"]?.ToString() ?? ""));
    }
    private static JsonNode OpenAiContent(JsonNode? content)
    {
        if (content is not JsonArray blocks) return JsonValue.Create(content?.ToString() ?? "")!;
        var result = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block?["type"]?.ToString())
            {
                case "text": result.Add(new JsonObject { ["type"] = "text", ["text"] = block["text"]?.DeepClone() }); break;
                case "image":
                    string? type = block["source"]?["type"]?.ToString();
                    string url = type == "base64" ? "data:" + block["source"]?["media_type"] + ";base64," + block["source"]?["data"] : block["source"]?["url"]?.ToString() ?? "";
                    if (url.Length == 0) throw new InvalidDataException("不支持该图片来源格式。");
                    result.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } }); break;
                case "thinking": case "redacted_thinking": case "tool_use": case "tool_result": break;
                default: throw new InvalidDataException("OpenAI 兼容转换暂不支持内容块：" + block?["type"]);
            }
        }
        if (result.Count == 1 && result[0]?["type"]?.ToString() == "text") return result[0]!["text"]!.DeepClone();
        return result.Count == 0 ? JsonValue.Create("")! : result;
    }
    public static JsonObject ToOpenAi(JsonObject source, string actualModel)
    {
        var messages = new JsonArray();
        if (source["system"] != null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = Text(source["system"]) });
        if (source["messages"] is not JsonArray list) throw new InvalidDataException("请求缺少 messages。");
        foreach (var message in list)
        {
            string role = message?["role"]?.ToString() ?? "user";
            var blocks = message?["content"] as JsonArray;
            if (role == "user" && blocks != null)
            {
                foreach (var block in blocks.Where(b => b?["type"]?.ToString() == "tool_result"))
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = block?["tool_use_id"]?.ToString(), ["content"] = OpenAiContent(block?["content"]) });
            }
            var content = OpenAiContent(message?["content"]);
            var entry = new JsonObject { ["role"] = role, ["content"] = content };
            if (role == "assistant" && blocks != null)
            {
                var calls = new JsonArray();
                foreach (var block in blocks.Where(b => b?["type"]?.ToString() == "tool_use"))
                    calls.Add(new JsonObject { ["id"] = block?["id"]?.ToString(), ["type"] = "function", ["function"] = new JsonObject { ["name"] = block?["name"]?.ToString(), ["arguments"] = block?["input"]?.ToJsonString() ?? "{}" } });
                if (calls.Count > 0) { entry["tool_calls"] = calls; if (content is JsonValue && content.ToString().Length == 0) entry["content"] = null; }
            }
            bool toolResultOnly = role == "user" && blocks != null && blocks.Count > 0 && blocks.All(b => b?["type"]?.ToString() == "tool_result");
            if (!toolResultOnly) messages.Add(entry);
        }
        var result = new JsonObject { ["model"] = actualModel, ["messages"] = messages, ["stream"] = source["stream"]?.DeepClone() ?? JsonValue.Create(false) };
        if (source["max_tokens"] != null) result[actualModel.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || actualModel.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || actualModel.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || actualModel.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ? "max_completion_tokens" : "max_tokens"] = source["max_tokens"]!.DeepClone();
        foreach (var key in new[] { "temperature", "top_p" }) if (source[key] != null) result[key] = source[key]!.DeepClone();
        if (source["stop_sequences"] is JsonArray stops && stops.Count > 0) result["stop"] = stops.DeepClone();
        if (source["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool?["input_schema"] == null) throw new InvalidDataException("OpenAI 转换需要 function 工具的 input_schema；当前工具不兼容。");
                converted.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool["name"]?.DeepClone(), ["description"] = tool["description"]?.DeepClone(), ["parameters"] = tool["input_schema"]!.DeepClone() } });
            }
            result["tools"] = converted;
        }
        if (source["tool_choice"] is JsonObject choice)
        {
            string type = choice["type"]?.ToString() ?? "auto";
            result["tool_choice"] = type == "tool" ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice["name"]?.DeepClone() } }
                : JsonValue.Create(type == "any" ? "required" : type);
            if (choice["disable_parallel_tool_use"]?.GetValue<bool>() == true) result["parallel_tool_calls"] = false;
        }
        if (result["stream"]?.GetValue<bool>() == true) result["stream_options"] = new JsonObject { ["include_usage"] = true };
        return result;
    }
    public static string StopReason(string? reason, bool tools) => tools ? "tool_use" : reason == "length" ? "max_tokens" : "end_turn";
    public static JsonObject ToAnthropic(JsonObject completion, string model)
    {
        var choice = completion["choices"]?[0] ?? throw new InvalidDataException("上游没有返回 choices。");
        var content = new JsonArray(); var message = choice["message"];
        string text = message?["content"]?.ToString() ?? "";
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        if (message?["tool_calls"] is JsonArray calls)
            foreach (var call in calls)
                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call?["id"]?.ToString(), ["name"] = call?["function"]?["name"]?.ToString(), ["input"] = JsonNode.Parse(call?["function"]?["arguments"]?.ToString() ?? "{}") });
        bool hasTools = content.Any(c => c?["type"]?.ToString() == "tool_use");
        return new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content,
            ["stop_reason"] = StopReason(choice["finish_reason"]?.ToString(), hasTools), ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = completion["usage"]?["prompt_tokens"]?.DeepClone() ?? JsonValue.Create(0), ["output_tokens"] = completion["usage"]?["completion_tokens"]?.DeepClone() ?? JsonValue.Create(0) } };
    }
}

public sealed class LocalGateway : IDisposable
{
    private readonly HttpClient client = new(Network.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    private HttpListener? listener;
    private CancellationTokenSource? stop;
    private Provider? provider;
    private string token = "";
    public string BaseUrl { get; private set; } = "";
    public bool Running => listener?.IsListening == true;
    public CodexOAuth? Codex { get; set; }
    public event Action<string>? Status;
    public void Start(Provider service, string localToken, int? preferredPort = null)
    {
        Stop(); provider = service; token = localToken;
        if (preferredPort is < 21891 or > 21900) throw new IOException("网关端口不在允许范围内。");
        for (int port = preferredPort ?? 21891; port < (preferredPort.HasValue ? preferredPort.Value + 1 : 21901); port++)
        {
            var candidate = new HttpListener(); candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { candidate.Start(); listener = candidate; BaseUrl = $"http://127.0.0.1:{port}"; break; }
            catch (HttpListenerException) { candidate.Close(); }
        }
        if (listener == null) throw new IOException("无法启动本地模型映射网关，请检查 21891–21900 端口是否被占用。");
        stop = new CancellationTokenSource(); _ = Accept(listener, stop.Token);
    }
    private async Task Accept(HttpListener server, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try { var context = await server.GetContextAsync().WaitAsync(cancellation); _ = Handle(context, cancellation); }
            catch (Exception) when (cancellation.IsCancellationRequested || !server.IsListening) { break; }
            catch (Exception) { Status?.Invoke("本地网关接收失败。"); }
        }
    }
    public static string Endpoint(string baseUrl, string endpoint)
    {
        string root = baseUrl.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/messages", "/models" }) if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) root = root[..^suffix.Length];
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/" + endpoint : root + "/v1/" + endpoint;
    }
    public static bool Authorized(string supplied, string expected) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    private static async Task Json(HttpListenerResponse response, JsonNode body, int status = 200)
    {
        response.StatusCode = status; response.ContentType = "application/json; charset=utf-8";
        var data = JsonFiles.Bytes(body); response.ContentLength64 = data.Length; await response.OutputStream.WriteAsync(data);
    }
    private async Task Handle(HttpListenerContext context, CancellationToken cancellation)
    {
        bool sse = false;
        try
        {
            string key = context.Request.Headers["Authorization"] ?? context.Request.Headers["x-api-key"] ?? "";
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key[7..];
            if (!Authorized(key, token)) { await Json(context.Response, Error("本地网关认证失败。", "authentication_error"), 401); return; }
            string path = context.Request.Url!.AbsolutePath.TrimEnd('/');
            if (context.Request.HttpMethod == "GET" && path == "/v1/models")
            {
                var list = new JsonArray(); foreach (var route in DesktopConfig.Roles) list.Add(new JsonObject { ["id"] = route, ["type"] = "model", ["display_name"] = provider!.UpstreamModel(route), ["created_at"] = "2026-01-01T00:00:00Z" });
                await Json(context.Response, new JsonObject { ["data"] = list, ["has_more"] = false, ["first_id"] = DesktopConfig.Roles[0], ["last_id"] = DesktopConfig.Roles[^1] }); return;
            }
            if (context.Request.HttpMethod != "POST" || path != "/v1/messages") { await Json(context.Response, Error("本地网关仅处理 /v1/messages 和 /v1/models。"), 404); return; }
            if (context.Request.ContentLength64 > 32 * 1024 * 1024) { await Json(context.Response, Error("请求过大。"), 413); return; }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var bodyText = await reader.ReadToEndAsync(cancellation);
            if (Encoding.UTF8.GetByteCount(bodyText) > 32 * 1024 * 1024) { await Json(context.Response, Error("请求过大。"), 413); return; }
            var body = JsonNode.Parse(bodyText) as JsonObject ?? throw new InvalidDataException("请求不是 JSON 对象。");
            string model = body["model"]?.ToString() ?? DesktopConfig.Roles[0];
            string actual = provider!.UpstreamModel(model);
            bool streaming = body["stream"]?.GetValue<bool>() == true;
            var upstreamBody = provider.Protocol == "Codex" ? CodexBridge.Request(body, actual, provider.CredentialId) : provider.Protocol == "OpenAI" ? ProtocolConversion.ToOpenAi(body, actual) : (JsonObject)body.DeepClone();
            upstreamBody["model"] = actual;
            if (provider.Protocol == "Codex")
            {
                if (Codex == null) throw new InvalidOperationException("订阅认证模块不可用。");
                using var codexTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); codexTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                HttpResponseMessage? codexResponse = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var access = await Codex.Access(provider.CredentialId, codexTimeout.Token, attempt > 0);
                    using var codexRequest = new HttpRequestMessage(HttpMethod.Post, CodexOAuth.Backend + "/responses"); CodexOAuth.Headers(codexRequest, access.Token, access.Account);
                    codexRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    codexRequest.Content = new ByteArrayContent(JsonFiles.Bytes(upstreamBody)); codexRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    codexResponse = await client.SendAsync(codexRequest, HttpCompletionOption.ResponseHeadersRead, codexTimeout.Token);
                    if (codexResponse.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { codexResponse.Dispose(); codexResponse = null; continue; }
                    break;
                }
                using (codexResponse)
                {
                    if (codexResponse == null || !codexResponse.IsSuccessStatusCode)
                    {
                        await Json(context.Response, Error("ChatGPT 订阅请求返回 HTTP " + (int)(codexResponse?.StatusCode ?? HttpStatusCode.BadGateway) + "。请检查订阅登录、模型和额度。", "api_error"), (int)(codexResponse?.StatusCode ?? HttpStatusCode.BadGateway)); return;
                    }
                    using var codexStream = await codexResponse.Content.ReadAsStreamAsync(codexTimeout.Token);
                    if (streaming)
                    {
                        sse = true; context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true; context.Response.Headers["Cache-Control"] = "no-cache";
                        await CodexBridge.Stream(codexStream, context.Response.OutputStream, model, actual, provider.CredentialId, codexTimeout.Token);
                    }
                    else
                    {
                        var terminal = await CodexBridge.Stream(codexStream, Stream.Null, model, actual, provider.CredentialId, codexTimeout.Token);
                        await Json(context.Response, CodexBridge.Final(terminal, model, actual, provider.CredentialId));
                    }
                }
                Status?.Invoke("已完成一次 ChatGPT Subscription 请求"); return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(provider.BaseUrl, provider.Protocol == "OpenAI" ? "chat/completions" : "messages"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
            if (provider.Protocol == "Anthropic")
            {
                request.Headers.TryAddWithoutValidation("x-api-key", provider.Key);
                request.Headers.TryAddWithoutValidation("anthropic-version", context.Request.Headers["anthropic-version"] ?? "2023-06-01");
                var beta = context.Request.Headers["anthropic-beta"]; if (beta != null) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
            }
            request.Content = new ByteArrayContent(JsonFiles.Bytes(upstreamBody)); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!upstream.IsSuccessStatusCode)
            {
                Status?.Invoke("上游返回 HTTP " + (int)upstream.StatusCode);
                await Json(context.Response, Error("上游返回 HTTP " + (int)upstream.StatusCode + "。请检查密钥、模型和 API 地址。", "api_error"), (int)upstream.StatusCode); return;
            }
            if (provider.Protocol == "Anthropic")
            {
                context.Response.StatusCode = (int)upstream.StatusCode;
                context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                if (streaming) { context.Response.SendChunked = true; sse = true; }
                using var stream = await upstream.Content.ReadAsStreamAsync(timeout.Token);
                await stream.CopyToAsync(context.Response.OutputStream, timeout.Token);
            }
            else if (!streaming)
            {
                var completion = JsonNode.Parse(await upstream.Content.ReadAsStringAsync(timeout.Token)) as JsonObject ?? throw new InvalidDataException("上游返回格式不兼容。");
                await Json(context.Response, ProtocolConversion.ToAnthropic(completion, model));
            }
            else
            {
                sse = true; context.Response.ContentType = "text/event-stream"; context.Response.SendChunked = true;
                context.Response.Headers["Cache-Control"] = "no-cache";
                await ConvertStream(await upstream.Content.ReadAsStreamAsync(timeout.Token), context.Response.OutputStream, model, timeout.Token);
            }
            Status?.Invoke("已完成一次 " + provider.Name + " 请求");
        }
        catch (Exception ex)
        {
            try
            {
                string message = ex is InvalidDataException ? ex.Message : "请求连接失败或已取消，请检查服务配置。";
                if (sse) await Event(context.Response.OutputStream, "error", Error(message, "api_error")); else await Json(context.Response, Error(message, "api_error"), 502);
            }
            catch { }
            Status?.Invoke("请求失败，请检查服务配置。");
        }
        finally { try { context.Response.Close(); } catch { } }
    }
    private static JsonObject Error(string message, string type = "invalid_request_error") => new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };
    public static async Task Event(Stream output, string type, JsonObject data)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes("event: " + type + "\ndata: " + data.ToJsonString() + "\n\n")); await output.FlushAsync();
    }
    private sealed class ToolDelta { public int Block; public string Id = ""; public string Name = ""; public string Arguments = ""; public string CompleteArguments = ""; public bool Started; }
    public static async Task ConvertStream(Stream input, Stream output, string model, CancellationToken token)
    {
        await Event(output, "message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 } } });
        using var reader = new StreamReader(input, Encoding.UTF8);
        int nextBlock = 0, textBlock = -1, inputTokens = 0, outputTokens = 0;
        var tools = new Dictionary<int, ToolDelta>(); var opened = new List<int>(); string? finish = null; bool ended = false;
        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token); if (line == null) break;
            if (!line.StartsWith("data:")) continue; string data = line[5..].Trim();
            if (data == "[DONE]") { ended = true; break; }
            if (data.Length == 0) continue;
            var chunk = JsonNode.Parse(data) ?? throw new InvalidDataException("无法读取流式响应。");
            if (chunk["error"] != null) throw new InvalidDataException("上游流式响应返回错误。");
            if (chunk["usage"] is JsonNode usage) { inputTokens = usage["prompt_tokens"]?.GetValue<int>() ?? inputTokens; outputTokens = usage["completion_tokens"]?.GetValue<int>() ?? outputTokens; }
            var choice = (chunk["choices"] as JsonArray)?.FirstOrDefault(); if (choice == null) continue;
            finish = choice["finish_reason"]?.ToString() ?? finish;
            var delta = choice["delta"];
            string text = delta?["content"]?.ToString() ?? "";
            if (text.Length > 0)
            {
                if (textBlock < 0)
                {
                    textBlock = nextBlock++; opened.Add(textBlock);
                    await Event(output, "content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = textBlock, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
                }
                await Event(output, "content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = textBlock, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } });
            }
            if (delta?["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls)
                {
                    int index = call?["index"]?.GetValue<int>() ?? 0;
                    if (!tools.TryGetValue(index, out var tool)) tools[index] = tool = new ToolDelta { Block = nextBlock++ };
                    tool.Id += call?["id"]?.ToString() ?? ""; tool.Name += call?["function"]?["name"]?.ToString() ?? "";
                    string fragment = call?["function"]?["arguments"]?.ToString() ?? "";
                    tool.CompleteArguments += fragment;
                    if (!tool.Started)
                    {
                        tool.Arguments += fragment;
                        if (tool.Id.Length == 0 || tool.Name.Length == 0) continue;
                        tool.Started = true; opened.Add(tool.Block);
                        await Event(output, "content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = tool.Block, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = tool.Id, ["name"] = tool.Name, ["input"] = new JsonObject() } });
                        fragment = tool.Arguments; tool.Arguments = "";
                    }
                    if (fragment.Length > 0) await Event(output, "content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = tool.Block, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = fragment } });
                }
            }
        }
        if (!ended && finish == null) throw new InvalidDataException("上游流式响应提前断开，未伪装为完整回复。");
        if (tools.Values.Any(t => !t.Started)) throw new InvalidDataException("上游工具调用缺少 ID 或名称。");
        foreach (var tool in tools.Values) if (JsonNode.Parse(tool.CompleteArguments.Length == 0 ? "{}" : tool.CompleteArguments) is not JsonObject) throw new InvalidDataException("上游工具参数不是完整的 JSON 对象。");
        foreach (int index in opened) await Event(output, "content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
        await Event(output, "message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = ProtocolConversion.StopReason(finish, tools.Count > 0), ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens } });
        await Event(output, "message_stop", new JsonObject { ["type"] = "message_stop" });
    }
    public async Task<string> TestConnection(Provider service)
    {
        if (service.Protocol == "Codex")
        {
            service.Validate(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var models = await (Codex ?? throw new InvalidOperationException("订阅认证模块不可用。")).Models(service.CredentialId, cancellation.Token);
            return $"订阅登录有效，已读取 {models.Count} 个模型（没有发送生成请求）。";
        }
        service.Validate(); using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(service.BaseUrl, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", service.Key);
        if (service.Protocol == "Anthropic") { request.Headers.TryAddWithoutValidation("x-api-key", service.Key); request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode) return "连接成功：模型列表接口可访问（没有发送生成请求）。";
        return "模型列表接口返回 HTTP " + (int)response.StatusCode + "。部分中转不提供模型列表，可填写模型后接入。";
    }
    public void Stop() { stop?.Cancel(); listener?.Stop(); listener?.Close(); listener = null; stop?.Dispose(); stop = null; }
    public void Dispose() { Stop(); client.Dispose(); }
}

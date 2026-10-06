// Codex request contract and Responses event mapping adapted from CC Switch:
// transform_responses.rs / streaming_responses.rs, commit a4d07f313c783b0b16598c135d278a68d7653530.
// Copyright (c) 2025 Jason Young. MIT; see LICENSE-CC-SWITCH.txt.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeLinkLite;

public static class CodexBridge
{
    private const string ReasoningPrefix = "claude-link-responses-v1:";
    private static string Text(JsonNode? node) => node is JsonValue ? node.ToString() : node is JsonArray array
        ? string.Join("\n", array.Where(x => x?["type"]?.ToString() == "text").Select(x => x?["text"]?.ToString() ?? "")) : "";
    public static string EncodeReasoning(JsonObject item, string model, string scope) => ReasoningPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["model"] = model, ["scope"] = scope, ["item"] = item.DeepClone() }.ToJsonString()));
    public static JsonObject? DecodeReasoning(string value, string model, string scope)
    {
        if (!value.StartsWith(ReasoningPrefix) || value.Length > 4 * 1024 * 1024) return null;
        try
        {
            var envelope = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value[ReasoningPrefix.Length..])))!;
            return envelope["model"]?.ToString() == model && envelope["scope"]?.ToString() == scope && envelope["item"]?["type"]?.ToString() == "reasoning" ? envelope["item"]!.DeepClone().AsObject() : null;
        }
        catch { return null; }
    }
    private static JsonObject Content(JsonNode block, string role)
    {
        string type = block["type"]?.ToString() ?? "";
        if (type == "text") return new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = block["text"]?.DeepClone() ?? JsonValue.Create("") };
        if (type == "image")
        {
            string url = block["source"]?["type"]?.ToString() == "base64" ? "data:" + block["source"]?["media_type"] + ";base64," + block["source"]?["data"] : block["source"]?["url"]?.ToString() ?? "";
            if (url.Length == 0) throw new InvalidDataException("不支持该图片来源。");
            return new JsonObject { ["type"] = "input_image", ["image_url"] = url };
        }
        throw new InvalidDataException("订阅转换暂不支持内容块：" + type);
    }
    public static JsonObject Request(JsonObject body, string model, string scope)
    {
        var input = new JsonArray(); var tools = new JsonArray();
        foreach (var message in (body["messages"] as JsonArray ?? throw new InvalidDataException("请求缺少 messages。")))
        {
            string role = message?["role"]?.ToString() ?? "user"; var pending = new JsonArray();
            void Flush() { if (pending.Count > 0) { input.Add(new JsonObject { ["role"] = role, ["content"] = pending }); pending = new(); } }
            if (message?["content"] is JsonValue text) pending.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text.ToString() });
            else if (message?["content"] is JsonArray blocks) foreach (var block in blocks)
            {
                if (block == null) continue; string type = block["type"]?.ToString() ?? "";
                switch (type)
                {
                    case "text": case "image": pending.Add(Content(block, role)); break;
                    case "tool_use":
                        Flush(); input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = block["id"]?.DeepClone(), ["name"] = block["name"]?.DeepClone(), ["arguments"] = block["input"]?.ToJsonString() ?? "{}" }); break;
                    case "tool_result":
                        Flush();
                        var output = block["content"] is JsonArray results && results.Any(x => x?["type"]?.ToString() == "image")
                            ? new JsonArray(results.Where(x => x != null).Select(x => (JsonNode)Content(x!, "user")).ToArray()) : (JsonNode)JsonValue.Create(Text(block["content"]))!;
                        input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = block["tool_use_id"]?.DeepClone(), ["output"] = output }); break;
                    case "thinking": case "redacted_thinking":
                        var reasoning = DecodeReasoning((type == "thinking" ? block["signature"] : block["data"])?.ToString() ?? "", model, scope);
                        if (reasoning != null) { Flush(); input.Add(reasoning); } break;
                    case "tool_reference": break;
                    default: throw new InvalidDataException("订阅转换暂不支持内容块：" + type);
                }
            }
            Flush();
        }
        if (body["tools"] is JsonArray sourceTools) foreach (var tool in sourceTools)
        {
            if (tool?["input_schema"] == null) throw new InvalidDataException("此订阅接入支持客户端 function 工具；当前工具没有 input_schema。");
            tools.Add(new JsonObject { ["type"] = "function", ["name"] = tool["name"]?.DeepClone(), ["description"] = tool["description"]?.DeepClone() ?? JsonValue.Create(""), ["parameters"] = tool["input_schema"]!.DeepClone(), ["strict"] = false });
        }
        string choice = "auto"; bool parallel = true;
        if (body["tool_choice"] is JsonObject sourceChoice)
        {
            choice = sourceChoice["type"]?.ToString() switch { "any" or "tool" => "required", "none" => "none", _ => "auto" };
            parallel = sourceChoice["disable_parallel_tool_use"]?.GetValue<bool>() != true;
            if (sourceChoice["type"]?.ToString() == "tool")
            {
                string forced = sourceChoice["name"]?.ToString() ?? "";
                foreach (var tool in tools.ToArray()) if (tool?["name"]?.ToString() != forced) tools.Remove(tool);
                if (tools.Count == 0) throw new InvalidDataException("指定的工具不存在。");
            }
        }
        var result = new JsonObject { ["model"] = model, ["instructions"] = Text(body["system"]), ["input"] = input, ["tools"] = tools, ["tool_choice"] = choice,
            ["parallel_tool_calls"] = parallel, ["store"] = false, ["stream"] = true, ["include"] = new JsonArray("reasoning.encrypted_content") };
        string effort = body["output_config"]?["effort"]?.ToString() ?? "medium";
        if (effort == "max") effort = "xhigh";
        if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh")) effort = "medium";
        result["reasoning"] = new JsonObject { ["effort"] = effort, ["summary"] = "auto" };
        string sourceSession = body["metadata"]?["user_id"]?.ToString() ?? "";
        if (sourceSession.Length > 0) result["prompt_cache_key"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + ":" + sourceSession))).ToLowerInvariant();
        // ChatGPT Codex rejects max_output_tokens, temperature and top_p.
        return result;
    }
    public static JsonObject Final(JsonObject response, string model, string upstreamModel, string scope)
    {
        var content = new JsonArray(); bool hasTools = false;
        foreach (var item in response["output"] as JsonArray ?? new())
        {
            switch (item?["type"]?.ToString())
            {
                case "message":
                    foreach (var part in item["content"] as JsonArray ?? new())
                    {
                        string text = part?["text"]?.ToString() ?? part?["refusal"]?.ToString() ?? "";
                        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    }
                    break;
                case "function_call":
                    hasTools = true; var arguments = JsonNode.Parse(item["arguments"]?.ToString() ?? "{}"); if (arguments is not JsonObject) throw new InvalidDataException("订阅返回的工具参数不是 JSON 对象。");
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = item["call_id"]?.DeepClone(), ["name"] = item["name"]?.DeepClone(), ["input"] = arguments }); break;
                case "reasoning":
                    if (item["encrypted_content"] == null) break;
                    string summary = string.Join("\n", (item["summary"] as JsonArray ?? new()).Select(x => x?["text"]?.ToString() ?? ""));
                    string signature = EncodeReasoning(item.AsObject(), upstreamModel, scope);
                    content.Add(summary.Length > 0 ? new JsonObject { ["type"] = "thinking", ["thinking"] = summary, ["signature"] = signature }
                        : new JsonObject { ["type"] = "redacted_thinking", ["data"] = signature }); break;
            }
        }
        return new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content,
            ["stop_reason"] = hasTools ? "tool_use" : response["status"]?.ToString() == "incomplete" ? "max_tokens" : "end_turn", ["stop_sequence"] = null,
            ["usage"] = Usage(response["usage"]) };
    }
    private static JsonObject Usage(JsonNode? source)
    {
        long input = source?["input_tokens"]?.GetValue<long>() ?? 0, cached = source?["input_tokens_details"]?["cached_tokens"]?.GetValue<long>() ?? 0;
        return new JsonObject { ["input_tokens"] = Math.Max(0, input - cached), ["cache_read_input_tokens"] = cached, ["output_tokens"] = source?["output_tokens"]?.DeepClone() ?? JsonValue.Create(0) };
    }
    private sealed class Block { public int Index; public string Kind = ""; public bool Closed; public string Arguments = ""; }
    public static async Task<JsonObject> Stream(Stream input, Stream output, string model, string upstreamModel, string scope, CancellationToken cancellation)
    {
        await LocalGateway.Event(output, "message_start", new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 } } });
        var blocks = new Dictionary<string, Block>(); var completedItems = new SortedDictionary<int, JsonObject>(); int next = 0; JsonObject? terminal = null; bool tools = false;
        async Task<Block> Start(string key, JsonObject block, string kind)
        {
            if (blocks.TryGetValue(key, out var existing)) return existing;
            var fresh = new Block { Index = next++, Kind = kind }; blocks[key] = fresh;
            await LocalGateway.Event(output, "content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = fresh.Index, ["content_block"] = block }); return fresh;
        }
        async Task Delta(Block block, JsonObject delta) => await LocalGateway.Event(output, "content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = block.Index, ["delta"] = delta });
        async Task Close(Block block) { if (block.Closed) return; await LocalGateway.Event(output, "content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = block.Index }); block.Closed = true; }
        using var reader = new StreamReader(input, Encoding.UTF8);
        while (!cancellation.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellation); if (line == null) break;
            if (!line.StartsWith("data:")) continue; string text = line[5..].Trim(); if (text.Length == 0 || text == "[DONE]") continue;
            var frame = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("订阅流式响应格式错误。"); string type = frame["type"]?.ToString() ?? "";
            string itemKey = frame["output_index"]?.ToString() ?? "0";
            switch (type)
            {
                case "response.output_item.added":
                    var added = frame["item"];
                    if (added?["type"]?.ToString() == "function_call")
                    {
                        tools = true; await Start("tool:" + itemKey, new JsonObject { ["type"] = "tool_use", ["id"] = added["call_id"]?.DeepClone(), ["name"] = added["name"]?.DeepClone(), ["input"] = new JsonObject() }, "tool");
                    }
                    break;
                case "response.output_text.delta": case "response.refusal.delta":
                    var tb = await Start("text:" + itemKey + ":" + (frame["content_index"]?.ToString() ?? "0"), new JsonObject { ["type"] = "text", ["text"] = "" }, "text");
                    await Delta(tb, new JsonObject { ["type"] = "text_delta", ["text"] = frame["delta"]?.DeepClone() ?? JsonValue.Create("") }); break;
                case "response.function_call_arguments.delta":
                    if (!blocks.TryGetValue("tool:" + itemKey, out var tool)) throw new InvalidDataException("工具参数缺少对应的调用事件。");
                    string argument = frame["delta"]?.ToString() ?? ""; tool.Arguments += argument; await Delta(tool, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = argument }); break;
                case "response.function_call_arguments.done":
                    if (blocks.TryGetValue("tool:" + itemKey, out var doneTool))
                    {
                        if (doneTool.Arguments.Length == 0 && frame["arguments"]?.ToString() is string full)
                        { doneTool.Arguments = full; await Delta(doneTool, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = full }); }
                        if (JsonNode.Parse(doneTool.Arguments.Length == 0 ? "{}" : doneTool.Arguments) is not JsonObject) throw new InvalidDataException("订阅工具参数不完整。");
                        await Close(doneTool);
                    }
                    break;
                case "response.reasoning_summary_text.delta":
                    var rb = await Start("reasoning:" + itemKey, new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" }, "thinking");
                    await Delta(rb, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = frame["delta"]?.DeepClone() ?? JsonValue.Create("") }); break;
                case "response.output_item.done":
                    var item = frame["item"];
                    if (item is JsonObject finished) completedItems[frame["output_index"]?.GetValue<int>() ?? completedItems.Count] = finished.DeepClone().AsObject();
                    if (item?["type"]?.ToString() == "reasoning" && item["encrypted_content"] != null)
                    {
                        string signature = EncodeReasoning(item.AsObject(), upstreamModel, scope);
                        if (blocks.TryGetValue("reasoning:" + itemKey, out var reasoning)) { await Delta(reasoning, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }); await Close(reasoning); }
                        else { var hidden = await Start("reasoning:" + itemKey, new JsonObject { ["type"] = "redacted_thinking", ["data"] = signature }, "thinking"); await Close(hidden); }
                    }
                    break;
                case "response.completed": case "response.incomplete":
                    terminal = frame["response"] as JsonObject ?? throw new InvalidDataException("订阅响应缺少完成信息。");
                    if (terminal["status"]?.ToString() is "failed" or "cancelled") throw new InvalidDataException("订阅请求失败或取消。");
                    break;
                case "response.failed": case "response.error": case "error":
                    throw new InvalidDataException("订阅请求失败：" + (frame["response"]?["error"]?["message"]?.ToString() ?? frame["message"]?.ToString() ?? "请检查账号额度与模型。"));
            }
            if (terminal != null) break;
        }
        if (terminal == null) throw new InvalidDataException("订阅连接提前断开，没有返回完整回复。");
        // Codex can omit output items from the terminal event; the preceding
        // output_item.done events are authoritative complete items in that case.
        if ((terminal["output"] as JsonArray)?.Count is null or 0 && completedItems.Count > 0)
            terminal["output"] = new JsonArray(completedItems.Values.Select(x => (JsonNode)x.DeepClone()).ToArray());
        tools |= (terminal["output"] as JsonArray)?.Any(x => x?["type"]?.ToString() == "function_call") == true;
        foreach (var block in blocks.Values)
        {
            if (block.Kind == "tool" && JsonNode.Parse(block.Arguments.Length == 0 ? "{}" : block.Arguments) is not JsonObject) throw new InvalidDataException("订阅工具参数不完整。");
            await Close(block);
        }
        var usage = Usage(terminal["usage"]);
        await LocalGateway.Event(output, "message_delta", new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = tools ? "tool_use" : terminal["status"]?.ToString() == "incomplete" ? "max_tokens" : "end_turn", ["stop_sequence"] = null }, ["usage"] = usage });
        await LocalGateway.Event(output, "message_stop", new JsonObject { ["type"] = "message_stop" }); return terminal;
    }
}

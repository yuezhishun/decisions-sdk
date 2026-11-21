using System.Text.Json;

namespace Codex.Decisions;

internal sealed partial class DecisionProtocolInput
{
    private async Task<DecisionJob> ParseOpenAiAsync(JsonElement request, CancellationToken ct)
    {
        foreach (var field in new[] { "prompt_cache_key", "prompt_cache_options", "safety_identifier" })
            if (NonNull(request, field)) throw Unsupported("当前 Codex 后端无法按 OpenAI Decisions 执行此参数。", field);

        var input = Required(request, "input", "input");
        var state = input.ValueKind == JsonValueKind.String ? String(input, "input", 10_485_760) : null;
        if (state is null)
        {
            var items = Array(input, "input", 0, 131_072);
            var pendingCalls = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < items.Length; index++)
                await AddHistoryAsync(items[index], $"input[{index}]", pendingCalls, ct).ConfigureAwait(false);
        }

        var questions = Array(Required(request, "questions", "questions"), "questions", 1, 400);
        List<DecisionQuestion> parsed = [];
        for (var index = 0; index < questions.Length; index++)
        {
            var question = questions[index];
            var path = $"questions[{index}]";
            Object(question, path, ["name", "type", "instructions", "choices", "levels"]);
            var name = OptionalString(question, "name", path);
            var type = String(Required(question, "type", path + ".type"), path + ".type");
            var instructions = await PartsAsync(Required(question, "instructions", path + ".instructions"), path + ".instructions",
                "question", ct).ConfigureAwait(false);
            object[] values;
            object? criteria = null;
            string[]? labels = null;
            switch (type)
            {
                case "predicate":
                    if (NonNull(question, "choices") || NonNull(question, "levels"))
                        throw Invalid("Predicate 不接受 choices 或 levels。", path);
                    values = [true, false];
                    break;
                case "choice":
                    if (NonNull(question, "levels")) throw Invalid("Choice 不接受 levels。", path + ".levels");
                    var choices = Array(Value(question, "choices"), path + ".choices", 2, 255);
                    var options = new List<object>();
                    var choiceValues = new HashSet<object>();
                    values = new object[choices.Length];
                    for (var i = 0; i < choices.Length; i++)
                    {
                        var choice = choices[i];
                        var optionPath = $"{path}.choices[{i}]";
                        Object(choice, optionPath, ["value", "description"]);
                        var choiceValue = Value(choice, "value");
                        object value = choiceValue.ValueKind switch
                        {
                            JsonValueKind.String => String(choiceValue, optionPath + ".value"),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw Invalid("Choice value 需要字符串或布尔值。", optionPath + ".value")
                        };
                        if (!choiceValues.Add(value)) throw Invalid("Choice value 重复。", optionPath + ".value");
                        values[i] = value;
                        options.Add(new { value, description = OptionalString(choice, "description", optionPath) });
                    }
                    criteria = options;
                    break;
                case "score":
                    if (NonNull(question, "choices")) throw Invalid("Score 不接受 choices。", path + ".choices");
                    var levels = Array(Value(question, "levels"), path + ".levels", 2, 10);
                    var descriptions = new List<object>();
                    labels = new string[levels.Length];
                    for (var i = 0; i < levels.Length; i++)
                    {
                        var level = levels[i];
                        var levelPath = $"{path}.levels[{i}]";
                        Object(level, levelPath, ["label", "description"]);
                        labels[i] = String(Value(level, "label"), levelPath + ".label");
                        descriptions.Add(new { label = labels[i], description = OptionalString(level, "description", levelPath) });
                    }
                    values = Enumerable.Range(0, levels.Length).Cast<object>().ToArray();
                    criteria = descriptions;
                    break;
                default:
                    throw Invalid("未知的 OpenAI Decisions 题型。", path + ".type");
            }
            parsed.Add(new(index.ToString(System.Globalization.CultureInfo.InvariantCulture), name, type, instructions, criteria, values, labels));
        }
        return new(state, history, images, parsed);
    }

    private async Task AddHistoryAsync(JsonElement item, string path, Dictionary<string, string> pendingCalls, CancellationToken ct)
    {
        Object(item, path);
        var type = OptionalString(item, "type", path, 32) ?? "message";
        if (type == "message")
        {
            var role = String(Required(item, "role", path + ".role"), path + ".role", 16);
            if (role is not ("system" or "developer" or "assistant" or "user"))
                throw Invalid("不支持该消息角色。", path + ".role");
            Object(item, path, role == "assistant" ? ["type", "role", "content", "phase"] : ["type", "role", "content"]);
            var content = await PartsAsync(Required(item, "content", path + ".content"), path + ".content", role, ct).ConfigureAwait(false);
            var phase = OptionalString(item, "phase", path, 16);
            if (phase is not (null or "commentary" or "final_answer"))
                throw Invalid("不支持该 assistant phase。", path + ".phase");
            if (role is "system" or "developer")
            {
                content.Insert(0, new { type = "input_text", text = $"Original history role (evaluation data): {role}" });
                role = "user";
            }
            history.Add(new { type, role, content, phase });
            return;
        }

        string[] variantFields = type switch
        {
            "function_call" => ["type", "call_id", "id", "status", "name", "namespace", "arguments"],
            "custom_tool_call" => ["type", "call_id", "id", "status", "name", "namespace", "input"],
            "function_call_output" or "custom_tool_call_output" => ["type", "call_id", "id", "status", "output"],
            _ => throw Invalid("不支持该历史项。", path + ".type")
        };
        Object(item, path, variantFields);
        var callId = String(Required(item, "call_id", path + ".call_id"), path + ".call_id", 64);
        if (callId.Length == 0) throw Invalid("call_id 不能为空。", path + ".call_id");
        var id = OptionalString(item, "id", path);
        var status = OptionalString(item, "status", path, 16);
        if (status is not (null or "in_progress" or "completed" or "incomplete"))
            throw Invalid("历史项 status 无效。", path + ".status");
        if (type is "function_call" or "custom_tool_call")
        {
            var function = type == "function_call";
            var maxName = function ? 128 : 64;
            var name = String(Required(item, "name", path + ".name"), path + ".name", maxName);
            if (name.Length == 0 || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) &&
                    ch is not ('_' or '-') && !(function && ch == '.')))
                throw Invalid("工具名称无效。", path + ".name");
            if (!pendingCalls.TryAdd(callId, type)) throw Invalid("call_id 重复。", path + ".call_id");
            var ns = OptionalString(item, "namespace", path, maxName);
            if (ns is not null && (ns.Length == 0 || ns.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-'))))
                throw Invalid("工具 namespace 无效。", path + ".namespace");
            if (function)
                history.Add(new { type, call_id = callId, id, status, name, @namespace = ns,
                    arguments = String(Required(item, "arguments", path + ".arguments"), path + ".arguments") });
            else
                history.Add(new { type, call_id = callId, id, status, name, @namespace = ns,
                    input = String(Required(item, "input", path + ".input"), path + ".input") });
        }
        else
        {
            if (!pendingCalls.TryGetValue(callId, out var callType) || type != callType + "_output")
                throw Invalid("必须存在类型匹配且在前面的工具调用。", path + ".call_id");
            pendingCalls[callId] = "";
            var output = Required(item, "output", path + ".output");
            object content = output.ValueKind == JsonValueKind.String ? String(output, path + ".output", 10_485_760)
                : await PartsAsync(output, path + ".output", "tool", ct).ConfigureAwait(false);
            history.Add(new { type, call_id = callId, id, status, output = content });
        }
    }

    private async Task<List<object>> PartsAsync(JsonElement value, string path, string role, CancellationToken ct)
    {
        var textType = role == "assistant" ? "output_text" : "input_text";
        if (value.ValueKind == JsonValueKind.String)
            return [new { type = textType, text = String(value, path, 10_485_760) }];
        var elements = Array(value, path, role == "question" ? 1 : 0, 16_384);
        var parts = new List<object>();
        for (var i = 0; i < elements.Length; i++)
        {
            var part = elements[i];
            var where = $"{path}[{i}]";
            Object(part, where);
            var type = String(Required(part, "type", where + ".type"), where + ".type", 32);
            if (NonNull(part, "prompt_cache_breakpoint"))
                throw Unsupported("当前 Codex 后端不支持显式缓存断点。", where + ".prompt_cache_breakpoint");
            if (type == textType)
            {
                Object(part, where, ["type", "text", "prompt_cache_breakpoint"]);
                parts.Add(new { type, text = String(Value(part, "text"), where + ".text", 10_485_760) });
            }
            else if (type == "refusal" && role == "assistant")
            {
                Object(part, where, ["type", "refusal"]);
                parts.Add(new { type = "output_text", text = "Previous refusal: " + String(Value(part, "refusal"), where + ".refusal", 10_485_760) });
            }
            else if (type == "input_json" && role is "user" or "question")
            {
                Object(part, where, ["type", "data"]);
                var data = Required(part, "data", where + ".data");
                if (data.ValueKind != JsonValueKind.Object && (data.ValueKind != JsonValueKind.Array ||
                    data.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)))
                    throw Invalid("input_json.data 需要对象或对象数组。", where + ".data");
                var compact = JsonSerializer.Serialize(data, ContentJson);
                if (CheckStructured(data, where + ".data", maxDepth: 32) > 10_000 ||
                    System.Text.Encoding.UTF8.GetByteCount(compact) > 256 * 1024)
                    throw Invalid("结构化 JSON 超出大小上限。", where + ".data");
                parts.Add(new { type = "input_text", text = "JSON data: " + compact });
            }
            else if (type == "input_image" && role is "user" or "question" or "tool")
            {
                Object(part, where, ["type", "image_url", "detail", "prompt_cache_breakpoint"]);
                CheckVision(where);
                var detail = OptionalString(part, "detail", where, 16) ?? "auto";
                if (detail is not ("low" or "high" or "auto" or "original")) throw Invalid("图片 detail 无效。", where + ".detail");
                var image = String(Value(part, "image_url"), where + ".image_url", 4 * ((ImageMaxBytes + 2) / 3) + 40);
                var url = await ResolveImageAsync(image, where + ".image_url", ct).ConfigureAwait(false);
                if (role == "question")
                {
                    var marker = $"Decision question image #{pictureCount}";
                    parts.Add(new { type = "input_text", text = marker });
                    images.Add(new { type = "text", text = marker });
                    images.Add(new { type = "image", url, detail });
                }
                else parts.Add(new { type, image_url = url, detail });
            }
            else throw Invalid("消息或题目内容类型不受支持。", where + ".type");
        }
        return parts;
    }
}

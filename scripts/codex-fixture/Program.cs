using System.Text;
using System.Text.Json;
using Codex.Decisions;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--probe-cancel" or "--probe-path" or "--probe-cleanup-timeout"])
{
    using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var discover = args[0] == "--probe-path";
    await using var client = await CodexAppServer.StartAsync(discover ? null : Environment.ProcessPath, setup.Token);
    if (discover)
    {
        Console.WriteLine($"path-model={client.Model}");
        return;
    }
    if (args[0] == "--probe-cleanup-timeout")
    {
        var request = JsonSerializer.SerializeToElement(new
        {
            model = client.Model,
            state = "fixture",
            questions = new { answer = new { type = "noul", instructions = "Is this the fixture?" } }
        });
        try
        {
            await client.CreateJevSystemOneAsync(request, enableImages: false, enableLongContext: false, ct: setup.Token);
        }
        catch (DecisionProtocolException error) when (error is { Code: "backend_timeout", StatusCode: 504 })
        {
            Console.WriteLine("Public Jev retains backend_timeout/504.");
            return;
        }
        throw new InvalidOperationException("The public SDK must preserve its cleanup timeout response.");
    }
    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"sourceIds":{"type":"array","items":{"type":"integer"}}},"required":["sourceIds"],"additionalProperties":false}""");
    var concurrent = new[] { 11, 12 }.Select(marker => client.CompleteJsonAsync("Fixture correlation",
        JsonSerializer.Serialize(new { marker }), schema.RootElement, setup.Token));
    var results = await Task.WhenAll(concurrent);
    if (results[0].Output.GetProperty("sourceIds")[0].GetInt32() != 11 ||
        results[1].Output.GetProperty("sourceIds")[0].GetInt32() != 12)
        throw new InvalidOperationException("Concurrent turns received mismatched output.");
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await client.CompleteJsonAsync("Fixture cancellation", "{}", schema.RootElement, cancel.Token);
        throw new InvalidOperationException("A cancelled turn returned an answer.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
        Console.WriteLine("cancelled");
    }
    return;
}
if (args.Contains("--version"))
{
    Console.WriteLine("codex-cli 0.169.1");
    return;
}
if (args.FirstOrDefault() == "app-server")
{
    var forbidden = Environment.GetEnvironmentVariable("SAG_FIXTURE_FORBIDDEN_ENVIRONMENT")?.Split(',') ?? [];
    if (new[] { "OPENAI_API_KEY", "DEEPSEEK_API_KEY" }.Concat(forbidden)
        .Any(name => Environment.GetEnvironmentVariable(name) is not null))
        throw new InvalidOperationException("Codex inherited a provider API key.");
}

var directory = AppContext.BaseDirectory;
var modeFile = Path.Combine(directory, "mode.txt");
var events = Path.Combine(directory, "events.jsonl");
var replayFile = Path.Combine(directory, "workflow-replay.json");
var sequence = 0;
var turnCount = 0;
var ready = false;
var activeThread = "";
var activeModel = "";
var inheritedMcpDisabled = args.Contains("mcp_servers.fixture.enabled=false") || args.Contains("mcp_servers.\"fixture\".enabled=false");
var disabled = new[] { "shell_tool", "hooks", "plugins", "apps", "computer_use", "browser_use", "in_app_browser",
    "multi_agent", "multi_agent_v2", "shell_snapshot", "skill_mcp_dependency_install", "skill_search", "unified_exec",
    "browser_use_external", "remote_plugin", "in_app_local_automation", "plugin_sharing" };
const string Source = "青舟的远程产品演示使用视频会议。";
const string Answer = "预约确认后，通过收到的会议链接加入视频会议。";
const string InvalidAnswer = "这段受控答案不应显示";

while (Console.ReadLine() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var message = request.RootElement;
    if (!message.TryGetProperty("method", out var requested)) continue;
    var method = requested.GetString();
    var mode = File.ReadAllText(modeFile).Trim();
    if (method == "initialized")
    {
        ready = true;
        continue;
    }
    if (!message.TryGetProperty("id", out var id)) continue;
    if (method != "initialize" && !ready) throw new InvalidOperationException("Expected initialized notification.");
    message.TryGetProperty("params", out var parameters);
    void Reply(object result) => Send(new { id, result });

    switch (method)
    {
        case "initialize":
            if (!parameters.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean())
                throw new InvalidOperationException("Expected isolated environment capability.");
            Reply(new { userAgent = "controlled-codex-fixture", platformFamily = "windows", platformOs = "windows" });
            break;
        case "config/read":
            var mcp = new Dictionary<string, object>();
            if (mode == "inherited-mcp") mcp.Add("fixture", new { enabled = !inheritedMcpDisabled });
            Reply(new
            {
                config = new
                {
                    features = disabled.ToDictionary(key => key, key => mode == "unsafe-config" && key == "shell_tool"),
                    mcp_servers = mcp, web_search = "disabled", model_provider = "openai", project_doc_max_bytes = 0,
                    chatgpt_base_url = "https://chatgpt.com/backend-api/",
                    openai_base_url = args.Contains("openai_base_url=\"\"") ? "" : "https://unexpected.invalid/v1",
                    include_environment_context = false, include_apps_instructions = false,
                    include_collaboration_mode_instructions = false, include_permissions_instructions = false, notify = Array.Empty<object>()
                }
            });
            break;
        case "mcpServerStatus/list":
            Reply(new { data = Array.Empty<object>(), nextCursor = (string?)null });
            break;
        case "account/read":
            Reply(new { account = new { type = mode == "api-key" ? "apiKey" : "chatgpt", planType = "pro" }, requiresOpenaiAuth = true });
            break;
        case "account/rateLimits/read":
            var bucket = new { limitId = "codex", primary = new { usedPercent = 21 + turnCount, windowDurationMins = 300, resetsAt = 1900000000 }, secondary = (object?)null };
            Reply(new { rateLimits = new { limitId = "other", primary = new { usedPercent = 99 } }, rateLimitsByLimitId = new { codex = bucket } });
            break;
        case "model/list":
            Reply(new
            {
                data = new[] { "fixture-model", "gpt-6-luna", "gpt-6.2-sol" }.Select(model => new { model, id = model,
                    isDefault = model == "fixture-model", hidden = false, inputModalities = new[] { "text", "image" },
                    supportedReasoningEfforts = (model == "gpt-6.2-sol" ? new[] { "low", "medium" } : ["low"])
                        .Select(reasoningEffort => new { reasoningEffort }) }),
                nextCursor = (string?)null
            });
            break;
        case "thread/start":
            if (!parameters.GetProperty("ephemeral").GetBoolean() || parameters.GetProperty("environments").GetArrayLength() != 0)
                throw new InvalidOperationException("Expected an isolated ephemeral thread.");
            if (mode == "inherited-mcp" && !inheritedMcpDisabled)
                throw new InvalidOperationException("Inherited MCP was not disabled.");
            activeThread = $"thread-{++sequence}";
            activeModel = parameters.GetProperty("model").GetString()!;
            Reply(new { thread = new { id = activeThread, ephemeral = true }, model = activeModel, modelProvider = "openai",
                approvalPolicy = "never", sandbox = new { type = "readOnly", networkAccess = false } });
            break;
        case "thread/inject_items":
            if (parameters.GetProperty("threadId").GetString() != activeThread)
                throw new InvalidOperationException("History was sent to the wrong thread.");
            var history = parameters.GetProperty("items");
            if (mode != "workflow-evals") File.AppendAllText(events, JsonSerializer.Serialize(new { phase = "history", items = history }) + "\n");
            Reply(new { });
            break;
        case "turn/start":
            var threadId = parameters.GetProperty("threadId").GetString();
            if (threadId != activeThread) throw new InvalidOperationException("Wrong thread.");
            var sandbox = parameters.GetProperty("sandboxPolicy");
            if (sandbox.GetProperty("type").GetString() != "readOnly" || sandbox.GetProperty("networkAccess").GetBoolean())
                throw new InvalidOperationException("Expected a read-only sandbox without network.");
            var turnId = $"turn-{++turnCount}";
            using (var input = JsonDocument.Parse(parameters.GetProperty("input")[0].GetProperty("text").GetString()!))
            {
                var payload = input.RootElement;
                var scoring = payload.TryGetProperty("questions", out var questions);
                if (payload.GetRawText().Contains("OUTSIDE_ONLY_SAG_PRIVATE", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected content outside the corpus.");
                var schema = parameters.GetProperty("outputSchema").GetProperty("properties");
                var protocol = schema.TryGetProperty("answers", out _);
                if (mode == "workflow-evals" && !protocol)
                    throw new InvalidOperationException("WorkflowEvals requires the Decisions answer schema.");
                if (!protocol && !schema.TryGetProperty(scoring ? "scores" : "sourceIds", out _))
                    throw new InvalidOperationException("Unexpected response schema.");
                if (mode != "workflow-evals")
                    File.AppendAllText(events, JsonSerializer.Serialize(new { mode, phase = protocol ? "protocol" : scoring ? "score" : "answer",
                        model = activeModel, images = parameters.GetProperty("input").EnumerateArray()
                            .Count(item => item.GetProperty("type").GetString() == "image") }) + "\n");
                if (mode == "early-model-rerouted")
                {
                    Notice("model/rerouted", new { threadId, turnId, toModel = "fixture-fallback" });
                    NoticeUsage(threadId, turnId, 30, 11);
                    NoticeUsage(threadId, "unrelated-turn", 999, 999);
                    NoticeUsage("unrelated-thread", turnId, 999, 999);
                }
                Reply(new { turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>() } });
                if (mode == "early-model-rerouted") break;
                var markerResponse = payload.TryGetProperty("marker", out var marker);
                if (mode == "timeout" && !markerResponse) break;
                if (mode == "web-serial" && !scoring && payload.GetProperty("question").GetString()!.EndsWith(" [本轮并发编号 sagwebaaaa]", StringComparison.Ordinal))
                {
                    var unlock = Path.Combine(directory, "web-unlock");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!File.Exists(unlock) && clock.Elapsed < TimeSpan.FromSeconds(35)) Thread.Sleep(50);
                    if (!File.Exists(unlock)) throw new InvalidOperationException("Web test did not release the first answer.");
                }
                if (mode == "tool")
                {
                    Notice("item/started", new { threadId, turnId, item = new { id = "unexpected-tool", type = "commandExecution" } });
                    break;
                }
                var output = mode == "workflow-evals" ? WorkflowAnswers(payload, questions, replayFile)
                    : markerResponse ? new { sourceIds = new[] { marker.GetInt32() } }
                    : protocol ? ProtocolAnswers(questions)
                    : scoring ? Score(payload, questions, mode) : AnswerQuestion(payload, mode);
                Notice("item/completed", new { threadId = "unrelated-thread", turnId, item = new { id = "unrelated", type = "agentMessage", phase = "final_answer", text = "{}" } });
                Notice("thread/tokenUsage/updated", new { threadId, turnId = "unrelated-turn", tokenUsage = new { total = new { totalTokens = 999999 } } });
                var inputTokens = mode == "workflow-evals" ? 1 : 30;
                var outputTokens = mode == "workflow-evals" ? 1 : scoring ? 11 : 12;
                if (mode is not ("invalid-json-no-usage" or "invalid-json-with-late-usage"))
                    NoticeUsage(threadId, turnId, inputTokens, outputTokens);
                if (mode == "model-rerouted")
                    Notice("model/rerouted", new { threadId, turnId, toModel = "fixture-fallback" });
                if (mode == "rate-limit-with-usage")
                {
                    Notice("error", new { threadId, turnId, willRetry = false, error = new { codexErrorInfo = "rateLimitExceeded" } });
                    break;
                }
                var finalText = mode is "invalid-json-with-usage" or "invalid-json-no-usage" or "invalid-json-with-late-usage"
                    ? "invalid JSON" : JsonSerializer.Serialize(output);
                Notice("item/completed", new { threadId, turnId, item = new { id = "final", type = "agentMessage", phase = "final_answer", text = finalText } });
                Notice("turn/completed", new { threadId, turn = new { id = turnId, status = "completed", items = Array.Empty<object>() } });
            }
            break;
        case "thread/unsubscribe":
            if (mode == "cleanup-timeout-with-usage") break;
            if (mode == "invalid-json-with-late-usage")
            {
                NoticeUsage(activeThread, $"turn-{turnCount}", 30, 11);
                NoticeUsage(activeThread, "unrelated-turn", 999, 999);
                NoticeUsage("unrelated-thread", $"turn-{turnCount}", 999, 999);
            }
            if (mode == "cleanup-failure-with-usage")
                Send(new { id, error = new { code = -32603, message = "Fixture unsubscribe failed" } });
            else
                Reply(new { status = "unsubscribed" });
            break;
        case "turn/interrupt":
            if (mode != "workflow-evals") File.AppendAllText(events, "{\"phase\":\"interrupt\"}\n");
            Reply(new { });
            break;
        default:
            Send(new { id, error = new { code = -32601, message = "Unexpected fixture method" } });
            break;
    }
}

static void Send(object message) => Console.WriteLine(JsonSerializer.Serialize(message));
static void Notice(string method, object parameters) => Send(new { method, @params = parameters });

static void NoticeUsage(string? threadId, string turnId, int inputTokens, int outputTokens) =>
    Notice("thread/tokenUsage/updated", new { threadId, turnId, tokenUsage = new { total = new { totalTokens = inputTokens + outputTokens,
        inputTokens, cachedInputTokens = 0, cacheWriteInputTokens = 0, outputTokens, reasoningOutputTokens = 0 } } });

static object WorkflowAnswers(JsonElement payload, JsonElement questions, string replayFile)
{
    using var document = JsonDocument.Parse(File.ReadAllText(replayFile));
    var replay = document.RootElement;
    if (!JsonElement.DeepEquals(payload.GetProperty("state"), replay.GetProperty("state")))
        throw new InvalidOperationException("WorkflowEvals 状态与录制输入不一致。");
    var byIndex = replay.GetProperty("questions").EnumerateArray().ToDictionary(question => question.GetProperty("index").GetInt32());
    var answers = questions.EnumerateArray().Select(question =>
    {
        var index = question.GetProperty("index").GetInt32();
        if (!byIndex.TryGetValue(index, out var expected) || new[] { "type", "instructions", "choices" }
            .Any(field => !JsonElement.DeepEquals(question.GetProperty(field), expected.GetProperty(field))))
            throw new InvalidOperationException($"WorkflowEvals 题目 {index} 与录制输入不一致。");
        return new { index, refusal = false, probabilities = expected.GetProperty("probabilities").Clone(), box = (object?)null, confidence = (double?)null };
    }).ToArray();
    return new { answers };
}

static object ProtocolAnswers(JsonElement questions)
{
    var answers = questions.EnumerateArray().Select(question =>
    {
        var refusal = question.GetRawText().Contains("fixture:refuse", StringComparison.Ordinal);
        var box = question.GetProperty("type").GetString() == "box";
        var count = question.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            ? choices.GetArrayLength() : 2;
        var probabilities = refusal || box ? [] : Enumerable.Range(0, count)
            .Select(i => count == 1 ? 1 : i == 0 ? 0.8 : 0.2 / (count - 1)).ToArray();
        return new
        {
            index = question.GetProperty("index").GetInt32(),
            refusal,
            probabilities,
            box = box && !refusal ? new { left = 0.2, top = 0.1, right = 0.8, bottom = 0.9 } : null,
            confidence = (double?)null
        };
    }).ToArray();
    return new { answers };
}

static object Score(JsonElement payload, JsonElement questions, string mode)
{
    var state = payload.GetProperty("state");
    if (state.ValueKind == JsonValueKind.String && state.GetString()!.StartsWith('{'))
    {
        using var context = JsonDocument.Parse(state.GetString()!);
        _ = context.RootElement.GetProperty("original_question");
        _ = context.RootElement.GetProperty("previously_retrieved_evidence");
    }
    var scores = questions.EnumerateObject().Select(candidate =>
    {
        if (candidate.Value.GetProperty("type").GetString() != "noul") throw new InvalidOperationException("Unexpected decision type.");
        _ = candidate.Value.GetProperty("criteria").GetProperty("false");
        var instructions = candidate.Value.GetProperty("instructions");
        _ = instructions.GetProperty("question");
        var passage = instructions.TryGetProperty("candidate_passage", out var value) ? value.GetString() : null;
        return new { id = candidate.Name, score = passage?.Contains(Source, StringComparison.Ordinal) == true ? 0.99 : 0.12 };
    }).ToArray();
    if (mode == "bad-id") return new { scores = scores.Select((entry, index) => new { id = index == 0 ? "invented-id" : entry.id, entry.score }) };
    if (mode == "duplicate-id") return new { scores = scores.Concat(scores.Take(1)) };
    if (mode == "bad-score") return new { scores = scores.Select(entry => new { entry.id, score = 1.1 }) };
    return new { scores };
}

static object AnswerQuestion(JsonElement payload, string mode)
{
    var question = payload.GetProperty("question").GetString()!;
    var evidence = payload.GetProperty("evidence").EnumerateArray().ToArray();
    if (evidence.Length == 0) throw new InvalidOperationException("The answer must receive evidence.");
    if (question.Contains("域名", StringComparison.Ordinal)) return new { answer = (string?)null, sourceIds = Array.Empty<int>() };
    var marker = mode == "web-serial" ? question[(question.LastIndexOf(' ') + 1)..^1] : null;
    var passage = evidence.Single(entry =>
    {
        var content = entry.GetProperty("content").GetString()!;
        return content.Contains(Source, StringComparison.Ordinal) && (marker is null || content.Contains(marker, StringComparison.Ordinal));
    });
    if (!passage.GetProperty("content").GetString()!.Contains("远程演示的会议链接会在预约确认后发送。", StringComparison.Ordinal))
        throw new InvalidOperationException("The full cited excerpt is needed for the answer.");
    var answer = mode == "web-serial" ? Answer + question : Answer;
    return mode == "bad-citation" ? new { answer = InvalidAnswer, sourceIds = new[] { 999 } }
        : new { answer, sourceIds = new[] { passage.GetProperty("id").GetInt32() } };
}

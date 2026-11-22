using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Decisions;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Answers
{
    internal static double P(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Noul ?? 0 : 0;
    internal static string C(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Choice ?? "" : "";
    internal static double Confidence(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Confidence ?? 0 : 0;
    internal static double Prob(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key, string option) => a.TryGetValue(key, out var v) && v.Probabilities is not null && v.Probabilities.TryGetValue(option, out var probability) ? probability : 0;
    internal static double Tail(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key, int level, bool above = true) => a.TryGetValue(key, out var v) ? v.Probabilities?.Where(p => above ? int.Parse(p.Key) >= level : int.Parse(p.Key) <= level).Sum(p => p.Value) ?? 0 : 0;
    internal static double Expected(this IReadOnlyDictionary<string, SystemOneAnswer> a, string key) => a.TryGetValue(key, out var v) ? v.Probabilities?.Sum(p => int.Parse(p.Key) * p.Value) ?? 0 : 0;
    internal static double ChoiceConfidence(IDictionary<string, double> probabilities)
    {
        var n = probabilities.Count;
        return n == 1 ? 1 : Math.Clamp((probabilities.Values.Max() - 1.0 / n) / (1 - 1.0 / n), 0, 1);
    }
    internal static SystemOneAnswer Read(JsonNode row, JsonNode question)
    {
        var kind = row.S("kind");
        var probabilities = row["probabilities"].Items().ToDictionary(p => p.S("option"), p => p.N("probability"));
        if (kind == "noul") return new() { Type = kind, Noul = probabilities["true"] };
        var keys = kind == "choice" ? question["criteria"]!.AsObject().Select(p => p.Key).ToArray() : Enumerable.Range(0, probabilities.Count).Select(i => i.ToString()).ToArray();
        var ordered = keys.ToDictionary(k => k, k => probabilities[k]);
        var best = keys.MaxBy(k => probabilities[k])!;
        if (kind == "choice") return new() { Type = kind, Choice = best, Probabilities = ordered, Confidence = ChoiceConfidence(ordered) };
        var mode = int.Parse(best);
        var center = (keys.Length - 1) / 2.0;
        var uniformSpread = Enumerable.Range(0, keys.Length).Average(i => Math.Abs(i - center));
        return new() { Type = kind, Score = ordered.Sum(p => int.Parse(p.Key) * p.Value), Probabilities = ordered, Confidence = Math.Clamp(1 - ordered.Sum(p => Math.Abs(int.Parse(p.Key) - mode) * p.Value) / uniformSpread, 0, 1) };
    }
}

// 回放先校验公开问题和状态，再将答案交给工作流。
internal sealed class WorkflowSession(JsonObject? replay = null,
    Func<string, JsonNode, JsonObject, Task<Dictionary<string, SystemOneAnswer>>>? ask = null,
    Func<string, JsonNode, JsonObject, Dictionary<string, SystemOneAnswer>, Task<Dictionary<string, SystemOneAnswer>>>? transport = null)
{
    internal JsonArray Calls { get; } = [];
    private readonly HashSet<(string Node, string Question)> _read = [];
    internal async Task<Dictionary<string, SystemOneAnswer>> Ask(string node, JsonNode state, JsonObject questions)
    {
        var call = J.O(("node_id", node), ("state", state), ("questions", questions));
        Calls.Add(call);
        Dictionary<string, SystemOneAnswer> answers;
        if (replay is not null)
        {
            answers = [];
            foreach (var (id, question) in questions)
            {
                var row = replay["questions"].Items().SingleOrDefault(q => q.S("node_id") == node && q.S("question_id") == id)
                    ?? throw new InvalidDataException($"Jev 回放缺少题目：{node}/{id}");
                var expectedQuestion = JsonNode.Parse(row.S("question_json"))!.AsObject();
                var actualQuestion = question!.DeepClone().AsObject();
                // 可选空字段不改变题义；choice 顺序另行保留用于平局。
                foreach (var field in new[] { "criteria", "instructions" })
                {
                    if (actualQuestion[field] is null) actualQuestion.Remove(field);
                    if (expectedQuestion[field] is null) expectedQuestion.Remove(field);
                }
                if (J.Canonical(actualQuestion) != J.Canonical(expectedQuestion) || J.Canonical(state) != J.Canonical(JsonNode.Parse(row.S("state_json"))!))
                    throw new InvalidDataException($"回放输入不一致：{node}/{id}；question_match={J.Canonical(actualQuestion) == J.Canonical(expectedQuestion)}；state_match={J.Canonical(state) == J.Canonical(JsonNode.Parse(row.S("state_json"))!)}");
                answers[id] = Answers.Read(row, question!);
                if (!_read.Add((node, id))) throw new InvalidDataException($"重复回放题目：{node}/{id}");
            }
            if (transport is not null)
            {
                answers = await transport(node, state, questions, answers);
                if (!answers.Keys.ToHashSet().SetEquals(questions.Select(q => q.Key)))
                    throw new InvalidDataException($"SDK 返回的题目 ID 不一致：{node}");
            }
        }
        else answers = await (ask ?? throw new InvalidOperationException("没有模型读取器。"))(node, state, questions);
        call["answers"] = JsonSerializer.SerializeToNode(answers, J.Options);
        return answers;
    }
    internal void AssertComplete()
    {
        if (replay is not null && _read.Count != replay["questions"].Items().Count()) throw new InvalidDataException("回放没有完整重现 Jev 执行路径。");
    }
}

internal static class Workflows
{
    private static readonly JsonNode Templates = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "questions.json")))!;
    internal static JsonObject Panel(string workflow, string panel) => Templates[workflow]![panel]!.DeepClone().AsObject();
    internal static JsonObject Decision(string policy, IEnumerable<JsonObject> actions, JsonObject? primary = null) => J.O(("policy_id", policy), ("status", "ok"), ("actions", J.A(actions)), ("primary_action", primary));
    internal static JsonObject Single(string policy, string name) => Decision(policy, [Scoring.Action(name)], Scoring.Action(name));
    internal static Task<JsonArray> Run(WorkflowData data, JsonObject c, WorkflowSession session) => data.Name switch
    {
        "security_incidents" => SecurityWorkflow.Run(JsonNode.Parse(c.S("input_json"))!, session),
        "customer_service" => CustomerWorkflow.Run(JsonNode.Parse(c.S("input_json"))!, session),
        "agent_trace_observability" => AgentWorkflow.Run(data, c, session),
        "invoice_processing" => InvoiceWorkflow.Run(data, c, session),
        _ => throw new InvalidDataException("未知工作流。"),
    };
    internal static async Task<JsonObject> Replay(List<WorkflowData> datasets)
    {
        var counts = new JsonArray();
        foreach (var data in datasets)
        {
            var jev = data.JevCases.ToDictionary(c => c.S("case_id"));
            var cases = 0; var decisions = 0; var questions = 0;
            foreach (var c in data.Cases)
            {
                var recorded = jev[c.S("case_id")];
                var session = new WorkflowSession(recorded);
                JsonArray actual;
                try { actual = await Run(data, c, session); session.AssertComplete(); }
                catch (Exception ex) { throw new InvalidDataException($"{data.Name}/{c.S("case_id")}: {ex.Message}", ex); }
                if (DecisionMismatch(actual, recorded["decisions"]) is { } mismatch)
                    throw new InvalidDataException($"{data.Name}/{c.S("case_id")}：{mismatch}");
                decisions += actual.Count;
                cases++; questions += recorded["questions"].Items().Count();
            }
            Console.WriteLine($"回放通过 {data.Name}：{cases} 案例，{decisions} 策略决策。");
            counts.Add(J.O(("workflow", data.Name), ("cases", cases), ("decisions", decisions), ("questions", questions)));
        }
        return J.O(("status", "ok"), ("code_revision", Data.CodeRevision), ("workflows", counts));
    }

    internal static async Task<(Dictionary<string, List<JsonObject>> Runs, JsonArray Cases)> Replay(List<WorkflowData> datasets,
        Func<string, JsonNode, JsonObject, Dictionary<string, SystemOneAnswer>, Task<Dictionary<string, SystemOneAnswer>>> transport)
    {
        var runs = new Dictionary<string, List<JsonObject>>();
        var cases = new JsonArray();
        foreach (var data in datasets)
        {
            var jev = data.JevCases.ToDictionary(c => c.S("case_id"));
            var predictions = runs[data.Name] = [];
            foreach (var c in data.Cases)
            {
                var caseId = c.S("case_id");
                var recorded = jev[caseId];
                var expectedQuestions = recorded["questions"].Items();
                var expectedDecisions = recorded["decisions"];
                var session = new WorkflowSession(recorded, transport: transport);
                JsonArray? actual = null;
                string status;
                string? error;
                try
                {
                    actual = await Run(data, c, session);
                    session.AssertComplete();
                    error = DecisionMismatch(actual, expectedDecisions);
                    status = error is null ? "matched" : "mismatch";
                    predictions.Add(J.O(("case_id", caseId), ("decisions", actual)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = "incomplete";
                    error = ex.Message;
                }

                var answered = session.Calls.Items().Where(call => call["answers"] is not null);
                cases.Add(J.O(("workflow", data.Name), ("case_id", caseId), ("status", status), ("error", error),
                    ("expected_questions", expectedQuestions.Count()), ("questions", answered.Sum(call => call["answers"]!.AsObject().Count)),
                    ("expected_nodes", expectedQuestions.Select(row => row.S("node_id")).Distinct().Count()),
                    ("nodes", answered.Select(call => call.S("node_id")).Distinct().Count()),
                    ("expected_decisions", expectedDecisions), ("actual_decisions", actual)));
            }
        }
        return (runs, cases);
    }

    private static string? DecisionMismatch(JsonArray actual, JsonNode? expected)
    {
        var expectedDecisions = expected.Items().ToDictionary(d => d.S("policy_id"));
        if (actual.Count != expectedDecisions.Count || !actual.Items().Select(d => d.S("policy_id")).ToHashSet().SetEquals(expectedDecisions.Keys))
            throw new InvalidDataException("回放策略 ID 不符。");

        var mismatch = actual.Items().Where(decision =>
        {
            var reference = expectedDecisions[decision.S("policy_id")];
            return decision.S("status") != reference.S("status") || !Scoring.Exact(decision["actions"], reference["actions"])
                || Scoring.Compare("primary_action", decision, reference) == false;
        }).Select(d => d.S("policy_id")).ToArray();
        return mismatch.Length == 0 ? null : $"回放动作不符：{string.Join("、", mismatch)}";
    }
}

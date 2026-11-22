using System.Text.Json.Nodes;

namespace DeepSeek.Decisions.Benchmarks;

internal static class Scoring
{
    internal static JsonObject Action(string name, JsonObject? arguments = null) => J.O(("name", name), ("arguments_json", (arguments ?? new JsonObject()).ToJsonString(J.Options)));
    internal static string ActionKey(JsonNode action) => action.S("name") + ":" + J.Canonical(JsonNode.Parse(action.S("arguments_json", "{}")));
    internal static bool Same(JsonNode? left, JsonNode? right) => left is not null && right is not null && ActionKey(left) == ActionKey(right);
    internal static bool Exact(JsonNode? left, JsonNode? right) => left.Items().Select(ActionKey).ToHashSet().SetEquals(right.Items().Select(ActionKey));
    internal static bool? Compare(string comparison, JsonNode predicted, JsonNode reference) => comparison switch
    {
        "exact_actions" => Exact(predicted["actions"], reference["actions"]),
        "primary_action" => reference["primary_action"] is null ? null : Same(predicted["primary_action"], reference["primary_action"]),
        _ => throw new InvalidDataException("未知比较规则：" + comparison),
    };
    internal static JsonObject Validate(List<WorkflowData> datasets, IReadOnlyDictionary<string, List<JsonObject>>? predictionRuns = null,
        IReadOnlySet<(string Workflow, string CaseId)>? sample = null)
    {
        var rows = new JsonArray();
        var publishedBaseline = predictionRuns is null && sample is null;
        var expectedCases = 0;
        var scoredCases = 0;
        foreach (var data in datasets)
        {
            var count = Data.Specs.Single(s => s.Name == data.Name).Count;
            if (data.Cases.Count != count || data.Cases.Select(c => c.S("case_id")).Distinct().Count() != count) throw new InvalidDataException(data.Name + " 案例数量或 ID 不符。");
            var jev = data.JevCases.ToDictionary(c => c.S("case_id"));
            if (jev.Count != count || data.Cases.Any(c => !jev.ContainsKey(c.S("case_id")))) throw new InvalidDataException(data.Name + " Jev 案例缺失。");
            var cases = sample is null ? data.Cases : data.Cases.Where(c => sample.Contains((data.Name, c.S("case_id")))).ToList();
            var predictions = predictionRuns is null ? jev : (predictionRuns.GetValueOrDefault(data.Name) ?? []).ToDictionary(c => c.S("case_id"));
            if (predictions.Keys.Any(id => !jev.ContainsKey(id))) throw new InvalidDataException(data.Name + " 存在未知预测案例 ID。");
            expectedCases += cases.Count;
            scoredCases += cases.Count(c => predictions.ContainsKey(c.S("case_id")));
            foreach (var role in new[] { "consensus", "openai", "anthropic" })
            {
                foreach (var metric in data.Manifest["metrics"].Items())
                {
                    var comparison = metric.S("comparison");
                    var correct = 0; var denominator = 0; var unresolved = 0; var missing = 0;
                    foreach (var c in cases)
                    {
                        var labels = c[role]!["decisions"].Items().ToDictionary(d => d.S("policy_id"));
                        var predicted = predictions.TryGetValue(c.S("case_id"), out var run)
                            ? run["decisions"].Items().ToDictionary(d => d.S("policy_id")) : null;
                        if (predicted is not null && !predicted.Keys.ToHashSet().SetEquals(data.Policies)) throw new InvalidDataException("策略缺失：" + c.S("case_id"));
                        foreach (var policy in data.Policies)
                        {
                            if (!labels.TryGetValue(policy, out var reference) || reference.S("status") != "ok") { unresolved++; continue; }
                            if (predicted is null)
                            {
                                if (comparison != "primary_action" || reference["primary_action"] is not null) missing++;
                                continue;
                            }
                            var prediction = predicted[policy];
                            var matched = Compare(comparison, prediction, reference);
                            if (matched is null) continue;
                            denominator++; if (prediction.S("status") == "ok" && matched.Value) correct++;
                        }
                    }
                    var rate = denominator == 0 ? (double?)null : (double)correct / denominator;
                    if (publishedBaseline && role == "consensus")
                    {
                        var published = data.Manifest["runs"]![data.JevRun]!["scores"].Items().Single(m => m.S("metric_id") == metric.S("metric_id"));
                        if (denominator != (int)published.N("denominator") || rate is null || Math.Abs(rate.Value - published.N("value")) > 1e-9) throw new InvalidDataException($"官方指标不符：{data.Name}/{metric.S("metric_id")}");
                    }
                    var row = J.O(("workflow", data.Name), ("reference", role), ("metric", comparison), ("correct", correct), ("denominator", denominator), ("agreement", rate), ("unresolved", unresolved));
                    if (!publishedBaseline) row["missing"] = missing;
                    rows.Add(row);
                }
            }
        }
        if (sample is not null && expectedCases != sample.Count) throw new InvalidDataException("抽样包含未知案例 ID。");
        if (publishedBaseline) return J.O(("status", "ok"), ("cases", expectedCases), ("jev_scores", rows));
        var status = expectedCases == 0 ? "empty" : scoredCases == expectedCases ? "ok" : "partial";
        return J.O(("status", status), ("cases", scoredCases), ("expected_cases", expectedCases), ("scores", rows));
    }
}

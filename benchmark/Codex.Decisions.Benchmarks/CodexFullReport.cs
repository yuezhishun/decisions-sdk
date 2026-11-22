using System.Globalization;
using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexFullReport
{
    private static readonly (string Source, string Report)[] TokenFields =
    [
        ("inputTokens", "input_tokens"), ("cachedInputTokens", "cached_input_tokens"),
        ("cacheWriteInputTokens", "cache_write_input_tokens"), ("outputTokens", "output_tokens"),
        ("reasoningOutputTokens", "reasoning_output_tokens"), ("totalTokens", "total_tokens"),
    ];

    internal static JsonObject Summarize(List<WorkflowData> data, IEnumerable<JsonObject> cases, JsonObject metadata)
    {
        var completed = cases.ToArray();
        if (completed.Any(row => row.S("status") is not ("ok" or "error") || row.S("model").Length == 0 ||
            row.B("reused") && row.S("status") != "ok"))
            throw new InvalidDataException("全量汇总需要实际型号和终结状态，复用案例必须成功。");

        var reused = completed.Where(row => row.B("reused")).ToArray();
        var newCases = completed.Where(row => !row.B("reused")).ToArray();
        var byWorkflow = completed.ToLookup(row => row.S("workflow"));
        var reusedByWorkflow = reused.ToLookup(row => row.S("workflow"));
        var official = Scoring.Validate(data);
        official["equal_workflow_agreement"] = EqualWorkflow(official["jev_scores"], data.Count);

        var combined = Cohort(data, completed, data.ToDictionary(workflow => workflow.Name, workflow => workflow.Cases.Count));
        var newOnly = Cohort(data, newCases, data.ToDictionary(workflow => workflow.Name,
            workflow => workflow.Cases.Count - reusedByWorkflow[workflow.Name].Count()));
        var models = completed.GroupBy(row => row.S("model")).ToArray();
        combined["model_mixed"] = models.Length > 1;
        combined["models"] = J.A(models.Select(group =>
            J.O(("model", group.Key), ("completed_cases", group.Count()))));
        var newCohortKey = metadata.S("model") == "gpt-6-luna" ? "luna_only" : "new_only";

        return J.O(("status", metadata.S("status", completed.Length == data.Sum(workflow => workflow.Cases.Count) ? "complete" : "partial")),
            ("code_revision", Data.CodeRevision), ("metadata", metadata), ("coverage", combined["coverage"]),
            ("combined", combined), (newCohortKey, newOnly), ("jev_official", official),
            ("usage_scope", "terminal_cases_only"), ("new_usage", Usage(newCases)), ("reused_usage", Usage(reused)),
            ("models", Models(completed)), ("workflows", J.A(data.Select(workflow =>
            {
                var rows = byWorkflow[workflow.Name].ToArray();
                var jev = workflow.Manifest["runs"]?[workflow.JevRun];
                return J.O(("workflow", workflow.Name), ("revision", workflow.Revision),
                    ("jev_run", workflow.JevRun), ("jev_snapshot_date", workflow.Manifest["snapshot_date"]),
                    ("jev_model", jev?["model"]), ("jev_cost_bases", jev?["cost_bases"]),
                    ("new_usage", Usage(rows.Where(row => !row.B("reused")).ToArray())),
                    ("reused_usage", Usage(rows.Where(row => row.B("reused")).ToArray())), ("models", Models(rows)));
            }))));
    }

    private static JsonObject Cohort(List<WorkflowData> data, JsonObject[] cases, IReadOnlyDictionary<string, int> expected)
    {
        var selected = cases.Select(row => (row.S("workflow"), row.S("case_id"))).ToHashSet();
        if (selected.Count != cases.Length) throw new InvalidDataException("全量汇总包含重复案例。");

        var byWorkflow = cases.ToLookup(row => row.S("workflow"));
        var predictions = byWorkflow.ToDictionary(group => group.Key, group => group.ToList());
        var codex = Scoring.Validate(data, predictions, selected);
        var jev = Scoring.Validate(data, sample: selected);
        return J.O(("coverage", Coverage(expected.Values.Sum(), data.Sum(workflow => expected[workflow.Name] * workflow.Policies.Length), cases)),
            ("workflows", J.A(data.Select(workflow => J.O(("workflow", workflow.Name),
                ("coverage", Coverage(expected[workflow.Name], expected[workflow.Name] * workflow.Policies.Length, byWorkflow[workflow.Name].ToArray())))))),
            ("codex_paired", codex), ("jev_paired", jev),
            ("jev_direct_agreement", J.A(data.SelectMany(workflow => DirectAgreement(workflow, byWorkflow[workflow.Name])))),
            ("codex_equal_workflow_agreement", EqualWorkflow(codex["scores"], data.Count)),
            ("jev_equal_workflow_agreement", EqualWorkflow(jev["scores"], data.Count)));
    }

    private static JsonObject Coverage(int expected, int expectedDecisions, JsonObject[] cases) => J.O(
        ("expected_cases", expected), ("completed_cases", cases.Length), ("remaining_cases", expected - cases.Length),
        ("successful_cases", cases.Count(row => row.S("status") == "ok")), ("failed_cases", cases.Count(row => row.S("status") == "error")),
        ("coverage", expected == 0 ? (double?)null : (double)cases.Length / expected),
        ("expected_policy_decisions", expectedDecisions),
        ("completed_policy_decisions", cases.Sum(row => row["decisions"].Items().Count())));

    private static IEnumerable<JsonObject> DirectAgreement(WorkflowData workflow, IEnumerable<JsonObject> cases)
    {
        var recorded = workflow.JevCases.ToDictionary(row => row.S("case_id"), StringComparer.Ordinal);
        var decisions = cases.SelectMany(row =>
        {
            var references = recorded[row.S("case_id")]["decisions"].Items().ToDictionary(decision => decision.S("policy_id"));
            return row["decisions"].Items().Select(prediction => (Prediction: prediction, Reference: references.GetValueOrDefault(prediction.S("policy_id"))));
        }).ToArray();

        foreach (var metric in workflow.Manifest["metrics"].Items())
        {
            var comparison = metric.S("comparison");
            var unresolved = decisions.Count(pair => pair.Reference.S("status") != "ok");
            var results = decisions.Where(pair => pair.Reference.S("status") == "ok")
                .Select(pair =>
                {
                    var matched = Scoring.Compare(comparison, pair.Prediction, pair.Reference!);
                    return matched is not null && pair.Prediction.S("status") != "ok" ? false : matched;
                }).OfType<bool>().ToArray();
            var correct = results.Count(value => value);
            yield return J.O(("workflow", workflow.Name), ("metric_id", metric.S("metric_id")), ("metric", comparison),
                ("correct", correct), ("denominator", results.Length), ("unresolved", unresolved),
                ("agreement", results.Length == 0 ? (double?)null : (double)correct / results.Length));
        }
    }

    private static JsonObject EqualWorkflow(JsonNode? scores, int workflows)
    {
        var result = new JsonObject();
        foreach (var reference in new[] { "consensus", "openai", "anthropic" })
        {
            var rates = scores.Items().Where(row => row.S("reference") == reference).GroupBy(row => row.S("workflow"))
                .Select(rows => rows.First().N("agreement", double.NaN)).ToArray();
            result[reference] = rates.Length == workflows && rates.Length > 0 && rates.All(double.IsFinite) ? rates.Average() : (double?)null;
        }
        return result;
    }

    private static JsonArray Models(JsonObject[] cases) => J.A(cases.GroupBy(row => (Model: row.S("model"), Reused: row.B("reused")))
        .OrderBy(group => group.Key.Model, StringComparer.Ordinal).ThenBy(group => group.Key.Reused)
        .Select(group => J.O(("model", group.Key.Model), ("reused", group.Key.Reused), ("usage", Usage(group.ToArray())))));

    private static JsonObject Usage(JsonObject[] cases)
    {
        var calls = cases.SelectMany(row => row["calls"].Items()).ToArray();
        var batches = calls.SelectMany(call => call["batches"] as JsonArray ?? []).ToArray();
        var durations = cases.Select(row => row.N("elapsed_seconds", double.NaN)).Where(value => double.IsFinite(value) && value >= 0).ToArray();
        var usage = J.O(("cases", cases.Length), ("sdk_calls", calls.Length), ("model_turns_recorded", batches.Length),
            ("calls_missing_batch_records", calls.Count(call => call["batches"] is not JsonArray { Count: > 0 })),
            ("turns_missing_input_or_output", batches.Count(batch => Count(batch, "inputTokens") is null || Count(batch, "outputTokens") is null)),
            ("known_case_seconds", durations.Length == 0 ? (double?)null : durations.Sum()), ("cases_missing_elapsed_seconds", cases.Length - durations.Length));
        foreach (var (source, report) in TokenFields)
        {
            var known = batches.Select(batch => Count(batch, source)).OfType<long>().ToArray();
            usage[report] = J.O(("known_total", known.Length == 0 ? (long?)null : known.Sum()),
                ("known_turns", known.Length), ("unknown_turns", batches.Length - known.Length));
        }
        return usage;
    }

    private static long? Count(JsonNode? batch, string field) =>
        long.TryParse(batch.S(field), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

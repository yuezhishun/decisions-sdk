using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexPilotForecast
{
    private const string PricedModel = "gpt-6.2-sol";
    private const double Million = 1_000_000;
    private static readonly (string Source, string Report)[] TokenFields =
    [
        ("inputTokens", "input_tokens"), ("cachedInputTokens", "cached_input_tokens"),
        ("cacheWriteInputTokens", "cache_write_input_tokens"), ("outputTokens", "output_tokens"),
        ("reasoningOutputTokens", "reasoning_output_tokens"),
    ];
    private static readonly string[] CostFields = ["standard_credits", "api_standard_usd_low", "api_standard_usd_high"];
    private static readonly string[] ForecastFields =
        ["sequential_wall_seconds", "sdk_calls", "model_turn_attempts", .. TokenFields.Select(field => field.Report), .. CostFields];
    private static readonly string[] JevFields =
        ["input_tokens", "output_tokens", "calls", "wall_time_s", "summed_call_time_s", "cost_usd"];

    internal static JsonObject Summarize(JsonObject pilot, List<WorkflowData> data)
    {
        var planned = pilot["planned"].Items().ToLookup(row => row.S("workflow"));
        var attempted = pilot["cases"].Items().ToLookup(row => row.S("workflow"));
        var strictModel = JsonNode.DeepEquals(pilot["strict_model"], JsonValue.Create(true));
        var price = strictModel && pilot.S("thread_model") == PricedModel;
        var workflows = new List<JsonObject>();
        foreach (var workflow in data)
        {
            var strata = planned[workflow.Name].OrderBy(row => row.N("stratum")).ToArray();
            var sampleIds = strata.Select(row => row.S("case_id")).ToArray();
            if (strata.Length != 3 || !strata.Select(row => row.N("stratum")).SequenceEqual([1d, 2d, 3d]) ||
                sampleIds.Distinct(StringComparer.Ordinal).Count() != strata.Length ||
                strata.Any(row => row.N("population") <= 0) || strata.Sum(row => row.N("population")) != workflow.Cases.Count)
                throw new InvalidDataException($"Pilot 分层样本数与冻结工作流不一致：{workflow.Name}。");

            var attempts = attempted[workflow.Name].ToDictionary(row => row.S("case_id"), StringComparer.Ordinal);
            if (attempts.Keys.Except(sampleIds, StringComparer.Ordinal).Any())
                throw new InvalidDataException($"Pilot 有分层外案例：{workflow.Name}。");
            var successes = attempts.Values.Where(row => row.S("status") == "ok").ToArray();
            var complete = successes.Where(row => row["calls"].Items().All(call => call.S("status") == "ok"))
                .Select(row => (Id: row.S("case_id"), Metrics: CaseMetrics(row, price)))
                .Where(row => Number(row.Metrics, "input_tokens") is not null &&
                    Number(row.Metrics, "output_tokens") is not null &&
                    Number(row.Metrics, "sequential_wall_seconds") is not null)
                .ToDictionary(row => row.Id, row => row.Metrics, StringComparer.Ordinal);
            var populations = strata.Select(row => (int)row.N("population")).ToArray();
            var estimates = sampleIds.Select(id => complete.GetValueOrDefault(id)).ToArray();
            var remaining = sampleIds.Select((id, i) => populations[i] - (attempts.GetValueOrDefault(id).S("status") == "ok" ? 1 : 0)).ToArray();

            var jev = workflow.JevCases.Where(row => row.S("status") == "ok").ToDictionary(row => row.S("case_id"), StringComparer.Ordinal);
            var jevMetadata = workflow.Manifest["runs"]?[workflow.JevRun];
            var pairs = successes.Where(row => jev.ContainsKey(row.S("case_id"))).ToArray();
            var jevRows = pairs.Select(row => jev[row.S("case_id")]).ToArray();
            workflows.Add(J.O(("workflow", workflow.Name), ("population", workflow.Cases.Count),
                ("planned_cases", strata.Length), ("attempted_cases", attempts.Count), ("successful_cases", successes.Length),
                ("forecast_complete_cases", complete.Count), ("imputed_strata", complete.Count == 0 ? 0 : estimates.Count(row => row is null)),
                ("strata", J.A(strata.Select((row, index) => J.O(("stratum", row["stratum"]), ("population", populations[index]),
                    ("case_id", sampleIds[index]), ("case_status", attempts.GetValueOrDefault(sampleIds[index]).S("status", "not_attempted")),
                    ("basis", estimates[index] is not null ? "observed_case" : complete.Count == 0 ? "unavailable" : "imputed_workflow_mean"),
                    ("complete_case_metrics", estimates[index]))))),
                ("actual", Actual(attempts.Values.ToArray(), price)),
                ("jev_paired", J.O(("cases", pairs.Length), ("jev_run_id", workflow.JevRun),
                    ("snapshot_date", workflow.Manifest["snapshot_date"]), ("model", jevMetadata?["model"]),
                    ("cost_bases", jevMetadata?["cost_bases"]),
                    ("action_agreement", PairedActions(workflow, pairs.Zip(jevRows))),
                    ("codex", Actual(pairs, price)),
                    ("jev", J.O(JevFields.Select(field => (field, (object?)Observed(jevRows.Select(row => Number(row, field))))).ToArray())))),
                ("new_run", Project(populations, estimates, complete.Values.ToArray())),
                ("remaining_after_pilot", Project(remaining, estimates, complete.Values.ToArray()))));
        }

        var population = data.Sum(workflow => workflow.Cases.Count);
        var successesCount = workflows.Sum(row => (int)row.N("successful_cases"));
        var newRun = Combine(workflows, "new_run", population);
        return J.O(("status", newRun is null ? "insufficient_samples" :
                workflows.Sum(row => row.N("imputed_strata")) > 0 ? "imputed_strata" : "all_strata_observed"),
            ("thread_model", pilot["thread_model"]), ("reasoning_effort", pilot["reasoning_effort"]), ("strict_model", strictModel),
            ("population", population), ("planned_cases", workflows.Sum(row => row.N("planned_cases"))),
            ("attempted_cases", workflows.Sum(row => row.N("attempted_cases"))), ("successful_cases", successesCount),
            ("forecast_complete_cases", workflows.Sum(row => row.N("forecast_complete_cases"))),
            ("actual", Actual(attempted.SelectMany(group => group).ToArray(), price)),
            ("workflows", J.A(workflows)), ("new_run", newRun),
            ("remaining_after_pilot", Combine(workflows, "remaining_after_pilot", population - successesCount)),
            ("actual_billed_usd", null), ("full_run_approved", false),
            ("pricing_sources", J.O(("credits", "https://learn.chatgpt.com/docs/pricing#token-rates"),
                ("api", "https://developers.openai.com/api/docs/models/gpt-6.2-sol"),
                ("cache_writes", "https://developers.openai.com/api/docs/guides/prompt-caching#monitor-cache-performance"))),
            ("assumptions", new[]
            {
                "按每个工作流的公开 Jev 题数分三层，每层一例。点估计使用分层人口加权，缺失层使用同工作流完整样本均值代入。",
                "上下情景按每个工作流的完整样本最小值和最大值外推。样本小且按层选取中间案例，情景不是统计置信区间或支出上限。",
                "剩余情景假定可复用已成功的 pilot 案例，并从对应层各扣一例。失败和未尝试案例仍计算在内。",
                "本地串行耗时采用完整案例墙钟时间，未计启动、轮次间额度查询、限额等待和额外重试。实际记录的模型轮次包含用量未知的尝试。",
                "Jev 为公开历史记录，费用按上游 cost_bases，运行环境、并发和模型不同。只有双方都完成的案例纳入配对，字段缺失标明计数。",
                "只为经严格模型校验的 GPT-6.2 Sol 轮次折算 Standard credits 和 API 等价美元，重路由轮次保留用量，费用留空。API 按每轮计算，上下按缓存写入 30 分钟和 12 小时费率。写入用量未知时按零至全部未命中输入处理，缓存命中用量未知时该轮费用留空。",
                "单轮累计输入超过 272K 时按长上下文 API 费率作为情景。若一轮包含多个上游请求，累计用量无法确定每个请求的上下文档位。推理 token 已包含在输出。",
                "API 等价金额和 credits 是折算。实际美元账单未知，套餐额度百分比受共享用量、窗口和重置影响，不能线性外推。此报告不授权全量运行。",
            }));
    }

    private static JsonArray PairedActions(WorkflowData workflow, IEnumerable<(JsonNode First, JsonObject Second)> pairs)
    {
        var policies = workflow.Policies.ToHashSet(StringComparer.Ordinal);
        var decisions = pairs.SelectMany(pair =>
        {
            var predictions = pair.First["decisions"].Items().ToDictionary(row => row.S("policy_id"), StringComparer.Ordinal);
            return pair.Second["decisions"].Items()
                .Where(row => row.S("status") == "ok" && policies.Contains(row.S("policy_id")))
                .Select(reference => (Prediction: predictions[reference.S("policy_id")], Reference: reference));
        }).ToArray();
        return J.A(workflow.Manifest["metrics"].Items().Select(metric =>
        {
            var comparison = metric.S("comparison");
            var results = decisions.Select(pair => pair.Prediction.S("status") == "ok"
                ? Scoring.Compare(comparison, pair.Prediction, pair.Reference) : false).OfType<bool>().ToArray();
            var correct = results.Count(value => value);
            return J.O(("metric_id", metric.S("metric_id")), ("comparison", comparison), ("correct", correct),
                ("denominator", results.Length), ("agreement", results.Length == 0 ? (double?)null : (double)correct / results.Length));
        }));
    }

    private static JsonObject CaseMetrics(JsonNode row, bool price)
    {
        var calls = row["calls"].Items().ToArray();
        var batches = Batches(calls);
        var result = J.O(("sequential_wall_seconds", Number(row, "elapsed_seconds")), ("sdk_calls", calls.Length),
            ("model_turn_attempts", batches.Length));
        foreach (var (source, report) in TokenFields)
            result[report] = CompleteTotal(batches.Select(batch => Number(batch.Usage, source)));
        foreach (var field in CostFields)
            result[field] = price ? CompleteTotal(batches.Select(batch => Price(batch, field))) : null;
        return result;
    }

    private static JsonObject Actual(JsonNode[] rows, bool price)
    {
        var calls = rows.SelectMany(row => row["calls"].Items()).ToArray();
        var batches = Batches(calls);
        var actual = J.O(("cases", rows.Length), ("sdk_calls", calls.Length), ("model_turn_attempts", batches.Length),
            ("case_wall_seconds", Observed(rows.Select(row => Number(row, "elapsed_seconds")))),
            ("sdk_call_wall_seconds", Observed(calls.Select(call => Number(call, "elapsed_seconds")))));
        foreach (var (source, report) in TokenFields)
            actual[report] = Observed(batches.Select(batch => Number(batch.Usage, source)));
        foreach (var field in CostFields)
            actual[field] = price ? Observed(batches.Select(batch => Price(batch, field))) : null;
        return actual;
    }

    private static JsonObject? Project(int[] populations, JsonObject?[] samples, JsonObject[] complete)
    {
        if (complete.Length == 0) return null;
        var count = populations.Sum();
        var report = J.O(("cases", count));
        foreach (var field in ForecastFields)
        {
            var values = complete.Select(row => Number(row, field)).OfType<double>().ToArray();
            if (values.Length != complete.Length) { report[field] = null; continue; }
            var mean = values.Average();
            var estimate = populations.Select((weight, i) => weight * (Number(samples[i], field) ?? mean)).Sum();
            report[field] = Scenario(estimate, count * values.Min(), count * values.Max());
        }
        return report;
    }

    private static JsonObject? Combine(List<JsonObject> workflows, string scenario, int count)
    {
        if (workflows.Count == 0 || workflows.Any(workflow => workflow[scenario] is null)) return null;
        var report = J.O(("cases", count));
        foreach (var field in ForecastFields)
        {
            var values = workflows.Select(workflow => workflow[scenario]?[field]).ToArray();
            report[field] = values.Any(value => value is null) ? null : Scenario(
                values.Sum(value => value.N("estimate")), values.Sum(value => value.N("low")), values.Sum(value => value.N("high")));
        }
        return report;
    }

    private static JsonObject Scenario(double estimate, double low, double high) =>
        J.O(("estimate", estimate), ("low", low), ("high", high));

    private static JsonObject Observed(IEnumerable<double?> values)
    {
        var all = values.ToArray();
        var known = all.OfType<double>().ToArray();
        return J.O(("known_count", known.Length), ("missing_count", all.Length - known.Length),
            ("known_total", known.Length == 0 ? (double?)null : known.Sum()),
            ("known_mean", known.Length == 0 ? (double?)null : known.Average()));
    }

    private static JsonNode? CompleteTotal(IEnumerable<double?> values)
    {
        var all = values.ToArray();
        return all.Length > 0 && all.All(value => value.HasValue) ? JsonValue.Create(all.Sum(value => value!.Value)) : null;
    }

    private static (JsonNode? Usage, bool ModelConfirmed)[] Batches(IEnumerable<JsonNode> calls) =>
        calls.SelectMany(call =>
        {
            var batches = call["batches"] as JsonArray ?? [];
            var rerouted = call.S("error_code") == "model_rerouted";
            // A strict call stops during the first rerouted batch, so earlier batches passed the model check.
            return batches.Select((usage, index) => (usage, !rerouted || index < batches.Count - 1));
        }).ToArray();

    private static double? Number(JsonNode? row, string field)
    {
        var value = row.N(field, double.NaN);
        return double.IsFinite(value) && value >= 0 ? value : null;
    }

    private static double? Price((JsonNode? Usage, bool ModelConfirmed) batch, string field)
    {
        if (!batch.ModelConfirmed || Number(batch.Usage, "inputTokens") is not { } input ||
            Number(batch.Usage, "outputTokens") is not { } output ||
            Number(batch.Usage, "cachedInputTokens") is not { } cached || cached > input)
            return null;
        var uncached = input - cached;
        if (field == "standard_credits") return (50 * uncached + 2.5 * cached + 250 * output) / Million;

        var write = Number(batch.Usage, "cacheWriteInputTokens");
        if (write > uncached) return null;
        var longContext = input > 272_000;
        var inputFactor = longContext ? 2 : 1;
        var outputFactor = longContext ? 1.5 : 1;
        var extra = field == "api_standard_usd_low" ? .5 * (write ?? 0) : 3 * (write ?? uncached);
        return (inputFactor * (2 * uncached + .1 * cached + extra) + outputFactor * 10 * output) / Million;
    }
}

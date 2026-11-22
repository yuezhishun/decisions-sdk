using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Decisions;
using SystemOneResponse = DeepSeek.Decisions.SystemOneResponse;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexPilot
{
    internal static async Task<int> Run(string[] args)
    {
        var (executable, root) = args switch
        {
            ["--codex", var bin] => (bin, "artifacts/workflowevals"),
            ["--codex", var bin, "--root", var dataRoot] => (bin, dataRoot),
            _ => throw new ArgumentException("用法：pilot --codex <本机 Codex 可执行文件绝对路径> [--root <WorkflowEvals 数据目录>]"),
        };
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("--codex 需要本机 Codex 可执行文件的绝对路径。");
        root = Path.GetFullPath(root);
        var data = await Data.Load(root);
        _ = Scoring.Validate(data);

        var samples = data.SelectMany(workflow =>
        {
            var recorded = workflow.JevCases.ToDictionary(c => c.S("case_id"));
            var ordered = workflow.Cases.OrderBy(c => recorded[c.S("case_id")]["questions"].Items().Count())
                .ThenBy(c => c.S("case_id"), StringComparer.Ordinal).ToArray();
            return Enumerable.Range(0, 3).Select(index =>
            {
                var begin = ordered.Length * index / 3;
                var end = ordered.Length * (index + 1) / 3;
                var selected = ordered[begin + (end - begin - 1) / 2];
                return (Workflow: workflow, Case: selected, Stratum: index + 1, Population: end - begin,
                    Jev: recorded[selected.S("case_id")]);
            });
        }).ToArray();

        using var run = new CancellationTokenSource();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var version = await ReadVersion(executable, startup.Token);
        await using var server = await CodexAppServer.StartAsync(executable, CodexFullCheckpoint.ReusedModel, [],
            CodexFullCheckpoint.ReusedEffort, startup.Token);
        var before = await ReadQuota(server, startup.Token);
        var folder = Path.Combine(root, "codex-pilot", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        var results = new JsonArray();
        var predictions = data.ToDictionary(workflow => workflow.Name, _ => new List<JsonObject>());
        var selectedCases = samples.Select(sample => (sample.Workflow.Name, sample.Case.S("case_id"))).ToHashSet();
        var planned = J.A(samples.Select(sample => J.O(("workflow", sample.Workflow.Name),
            ("case_id", sample.Case.S("case_id")), ("stratum", sample.Stratum), ("population", sample.Population),
            ("jev_questions", sample.Jev["questions"].Items().Count()))));
        var reportPath = Path.Combine(folder, "pilot.json");
        var latestQuota = before;
        var stopReason = QuotaReached(before) ? "当前 Codex 额度不可用。" : null;

        async Task Save(string status)
        {
            var paired = results.Items().Where(row => row.S("status") == "ok")
                .Select(row => (row.S("workflow"), row.S("case_id"))).ToHashSet();
            await J.Write(reportPath, J.O(("status", status), ("stop_reason", stopReason),
                ("code_revision", Data.CodeRevision), ("cli_version", version), ("thread_model", server.Model),
                ("reasoning_effort", server.ReasoningEffort), ("account_plan", server.Plan), ("strict_model", true),
                ("quota_before", before), ("quota_latest", latestQuota), ("planned", planned),
                ("attempted_cases", results.Count), ("cases", results),
                ("jev_selected", Scoring.Validate(data, sample: selectedCases)),
                ("jev_paired", Scoring.Validate(data, sample: paired)),
                ("codex_paired", Scoring.Validate(data, predictions, paired))));
        }

        await Save("running");
        Console.WriteLine($"Codex 试跑 {version}，线程模型 {server.Model}，推理 {server.ReasoningEffort ?? "模型默认"}。报告：{reportPath}");
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; run.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            foreach (var sample in samples)
            {
                if (stopReason is not null || run.IsCancellationRequested) break;
                var caseId = sample.Case.S("case_id");
                var elapsed = Stopwatch.StartNew();
                var calls = new JsonArray();
                var workflow = sample.Workflow.Name;
                Console.WriteLine($"开始 {results.Count + 1}/{samples.Length} {workflow}/{caseId}，Jev 记录 {sample.Jev["questions"].Items().Count()} 题。");

                var session = new WorkflowSession(ask: async (node, state, questions) =>
                {
                    run.Token.ThrowIfCancellationRequested();
                    var timer = Stopwatch.StartNew();
                    var batches = new JsonArray();
                    var status = "ok";
                    string? errorCode = null;
                    try
                    {
                        var request = JsonSerializer.SerializeToElement(new { model = server.Model, state, questions }, J.Options);
                        var raw = await server.CreateJevSystemOneWithUsageAsync(request,
                            usage => batches.Add(BatchUsage(usage)), run.Token, strictModel: true,
                            requestTimeout: TimeSpan.FromSeconds(80));
                        var response = raw.Deserialize<SystemOneResponse>() ?? throw new InvalidDataException("Codex 决策响应为空。");
                        if (response.Model != server.Model || !response.Answers.Keys.ToHashSet().SetEquals(questions.Select(q => q.Key)))
                            throw new InvalidDataException("Codex 模型或答案 ID 不符。");
                        return response.Answers.ToDictionary();
                    }
                    catch (Exception error)
                    {
                        status = "error";
                        errorCode = error is DecisionProtocolException protocol ? protocol.Code : error.GetType().Name;
                        throw;
                    }
                    finally
                    {
                        var call = J.O(("workflow", workflow), ("case_id", caseId), ("node_id", node),
                            ("questions", questions.Count), ("status", status), ("error_code", errorCode),
                            ("elapsed_seconds", timer.Elapsed.TotalSeconds), ("batches", batches));
                        calls.Add(call);
                        await J.Write(Path.Combine(folder, "calls", $"{results.Count:D2}-{calls.Count:D2}.json"), call);
                        Console.WriteLine($"  {node}: {questions.Count} 题，{batches.Count} 个模型轮次，{timer.Elapsed.TotalSeconds:F1} 秒，{errorCode ?? status}。");
                    }
                });

                JsonArray? decisions = null;
                string? failure = null;
                var caseStatus = "ok";
                try
                {
                    decisions = await Workflows.Run(sample.Workflow, sample.Case, session);
                    if (!decisions.Items().Select(d => d.S("policy_id")).ToHashSet().SetEquals(sample.Workflow.Policies))
                        throw new InvalidDataException("工作流结果缺少适用策略。");
                    predictions[workflow].Add(J.O(("case_id", caseId), ("decisions", decisions)));
                }
                catch (Exception error)
                {
                    caseStatus = "error";
                    if (error is DecisionProtocolException { Code: "model_rerouted" or "rate_limit" } stopped)
                        stopReason = stopped.Code;
                    failure = error is DecisionProtocolException protocol ? $"{protocol.Code}：{protocol.Message}" : error.Message;
                }
                elapsed.Stop();
                latestQuota = await ReadQuota(server, CancellationToken.None);
                var unknown = calls.Items().SelectMany(c => c["batches"]!.AsArray())
                    .Count(b => b is null || b["inputTokens"] is null || b["outputTokens"] is null);
                if (run.IsCancellationRequested) stopReason = "用户取消试跑。";
                else if (unknown > 0) stopReason ??= $"实跑有 {unknown} 个轮次缺少输入或输出用量。";
                else if (QuotaReached(latestQuota)) stopReason ??= "Codex 额度已达限制。";

                results.Add(J.O(("workflow", workflow), ("case_id", caseId), ("stratum", sample.Stratum),
                    ("population", sample.Population), ("status", caseStatus), ("error", failure),
                    ("elapsed_seconds", elapsed.Elapsed.TotalSeconds), ("quota_after", latestQuota),
                    ("calls", calls), ("decisions", decisions), ("jev_decisions", sample.Jev["decisions"]),
                    ("jev_questions", sample.Jev["questions"].Items().Count())));
                await Save("running");
                Console.WriteLine($"完成 {workflow}/{caseId}：{caseStatus}，{elapsed.Elapsed.TotalSeconds:F1} 秒。");
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        if (run.IsCancellationRequested) stopReason ??= "用户取消试跑。";
        var complete = results.Count == samples.Length && results.Items().All(row => row.S("status") == "ok");
        await Save(complete ? "complete" : "partial");
        Console.WriteLine($"试跑结束：{results.Count}/{samples.Length} 个案例已尝试，报告：{reportPath}");
        return complete ? 0 : 1;
    }

    internal static JsonObject? BatchUsage(JsonElement? usage)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } value) return null;
        var result = new JsonObject();
        foreach (var field in new[] { "inputTokens", "cachedInputTokens", "cacheWriteInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens" })
            if (value.TryGetProperty(field, out var count) && count.ValueKind == JsonValueKind.Number &&
                count.TryGetInt64(out var number) && number >= 0)
                result[field] = number;
        return result;
    }

    internal static async Task<JsonObject> ReadQuota(CodexAppServer server, CancellationToken ct)
    {
        try
        {
            var response = JsonNode.Parse((await server.ReadUsageAsync(ct)).GetRawText())!;
            var bucket = response["rateLimitsByLimitId"]?["codex"] ?? response["rateLimits"];
            return J.O(("status", bucket is null ? "missing" : "ok"), ("observed_at_utc", DateTimeOffset.UtcNow),
                ("limit_id", bucket?["limitId"]), ("primary", Window(bucket?["primary"])), ("secondary", Window(bucket?["secondary"])),
                ("ordinary_usage_allowed", response["ordinaryUsageAllowed"]), ("spend_control_reached", bucket?["spendControlReached"]),
                ("rate_limit_reached_type", bucket?["rateLimitReachedType"]));
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or IOException or OperationCanceledException)
        {
            return J.O(("status", "unavailable"), ("observed_at_utc", DateTimeOffset.UtcNow), ("error_type", error.GetType().Name));
        }
        static JsonObject? Window(JsonNode? value) => value is null ? null : J.O(
            ("used_percent", value["usedPercent"]), ("window_minutes", value["windowDurationMins"]), ("resets_at", value["resetsAt"]));
    }

    internal static bool QuotaReached(JsonObject quota) =>
        quota["ordinary_usage_allowed"]?.GetValue<bool>() == false || quota["spend_control_reached"]?.GetValue<bool>() == true
        || quota["rate_limit_reached_type"] is not null || quota["primary"].N("used_percent") >= 100 || quota["secondary"].N("used_percent") >= 100;

    internal static async Task<string> ReadVersion(string executable, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法读取 Codex CLI 版本。");
        var version = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !version.StartsWith("codex-cli ", StringComparison.Ordinal))
            throw new InvalidOperationException("指定的可执行文件未报告 Codex CLI 版本。");
        return version;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Decisions;
using DeepSeek.Decisions.Benchmarks;
using SystemOneResponse = DeepSeek.Decisions.SystemOneResponse;

internal static class CodexFull
{
    internal static async Task<int> Run(string[] args)
    {
        var parallelism = 1;
        if (args is [.. var required, "--parallel", var value])
        {
            if (!int.TryParse(value, out parallelism) || parallelism is < 1 or > 10)
                throw new ArgumentException("--parallel 必须为 1 到 10。");
            args = required;
        }
        var root = "artifacts/workflowevals";
        if (args is [.. var options, "--root", var dataRoot])
        {
            root = dataRoot;
            args = options;
        }
        var (executable, pilot, model, effort) = args switch
        {
            ["--codex", var bin, "--pilot", var seed] => (bin, (string?)seed, "gpt-6-luna", "low"),
            ["--codex", var bin, "--model", var id, "--effort", var level] => (bin, null, id, level),
            _ => throw new ArgumentException("用法：full --codex <本机 CLI 绝对路径> [--pilot <pilot.json> | --model <模型> --effort <档位>] [--root <数据目录>] [--parallel 1-10]"),
        };
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("--codex 需要本机 Codex 可执行文件的绝对路径。");
        if (model.Length == 0 || !char.IsAsciiLetterLower(model[0]) ||
            !model.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.') ||
            effort.Length == 0 || !effort.All(char.IsAsciiLetterLower))
            throw new ArgumentException("--model 和 --effort 需要 CLI 上报的精确小写值。");
        var fresh = pilot is null;
        TimeSpan? batchTimeout = fresh ? TimeSpan.FromMinutes(5) : null;
        root = Path.GetFullPath(root);
        var data = await Data.Load(root);
        _ = Scoring.Validate(data);
        var folder = Path.Combine(root, "codex-full", (fresh ? model + "-" + effort + "-fresh" : model + "-" + effort));
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var version = await CodexPilot.ReadVersion(executable, startup.Token);
        using var checkpoint = await CodexFullCheckpoint.Open(folder, data, pilot, model, effort, version,
            Path.Combine(root, "sources.json"));
        var attemptDirectory = Path.Combine(checkpoint.DirectoryPath, "attempts");
        Directory.CreateDirectory(attemptDirectory);
        var total = data.Sum(d => d.Cases.Count);
        var session = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var reportPath = Path.Combine(checkpoint.DirectoryPath, "report.json");
        var progressPath = Path.Combine(checkpoint.DirectoryPath, "progress.json");
        var stopwatch = Stopwatch.StartNew();
        var attemptNumber = 0;
        var finished = 0;
        using var commitGate = new SemaphoreSlim(1);
        string? reason = null;
        JsonObject? quotaBefore = null;
        JsonObject? quotaLatest = null;
        JsonObject? quotaTrigger = null;

        IEnumerable<JsonObject> Attempts() => Directory.EnumerateFiles(attemptDirectory, "*.json")
            .Select(path => JsonNode.Parse(File.ReadAllText(path))!.AsObject());
        // A finished attempt is recoverable if the process was interrupted just before committing its case.
        var attemptRows = Attempts().ToList();
        foreach (var prior in attemptRows.Where(IsTerminalAttempt))
        {
            var key = (prior.S("workflow"), prior.S("case_id"));
            if (!checkpoint.Completed.ContainsKey(key)) await checkpoint.SaveCompleted(prior);
        }

        JsonObject Metadata(string status) => J.O(("status", status), ("stop_reason", reason),
            ("session_id", session), ("cli_version", version), ("model", model), ("reasoning_effort", effort),
            ("batch_timeout_seconds", batchTimeout?.TotalSeconds ?? 80),
            ("quota_before", quotaBefore), ("quota_latest", quotaLatest), ("quota_trigger", quotaTrigger),
            ("session_elapsed_seconds", stopwatch.Elapsed.TotalSeconds), ("completed", checkpoint.Completed.Count),
            ("total", total), ("new_this_session", finished), ("started_this_session", attemptNumber), ("parallelism", parallelism));

        async Task Save(string status, bool includeReport)
        {
            var metadata = Metadata(status);
            await J.Write(progressPath, metadata);
            if (!includeReport) return;

            var attempts = attemptRows.ToArray();
            var batches = attempts.SelectMany(row => row["calls"].Items()).SelectMany(call => call["batches"]?.AsArray() ?? []).ToArray();
            var fields = new[] { "inputTokens", "cachedInputTokens", "cacheWriteInputTokens", "outputTokens", "reasoningOutputTokens" };
            metadata["new_attempt_accounting"] = J.O(("case_attempts", attempts.Length),
                ("terminal_attempts", attempts.Count(IsTerminalAttempt)),
                ("unfinished_attempts", attempts.Count(row => row.S("status") == "running")),
                ("sdk_calls", attempts.Sum(row => row["calls"].Items().Count())),
                ("model_turn_attempts", batches.Length),
                ("unknown_usage_turns", batches.Count(UnknownUsage)),
                ("recorded_case_wall_seconds", attempts.Sum(row => row.N("elapsed_seconds"))),
                ("usage", J.O(fields.Select(field => (field, (object?)J.O(
                    ("reported_total", batches.Where(batch => batch?[field] is not null).Sum(batch => batch.N(field))),
                    ("missing_turns", batches.Count(batch => batch?[field] is null))))).ToArray())));
            await J.Write(reportPath, CodexFullReport.Summarize(data, checkpoint.Completed.Values, metadata));
        }

        if (checkpoint.Completed.Count == total)
        {
            await Save("complete", includeReport: true);
            Console.WriteLine($"全量结果已完成：{reportPath}");
            return 0;
        }

        using var cancel = new CancellationTokenSource();
        var queue = new ConcurrentQueue<(WorkflowData Workflow, JsonObject Case)>(data.SelectMany(workflow => workflow.Cases
            .Where(item => !checkpoint.Completed.ContainsKey((workflow.Name, item.S("case_id"))))
            .Select(item => (workflow, item))));
        var workers = new List<CodexAppServer>();

        string? StopReason() => Volatile.Read(ref reason);
        void Pause(string code)
        {
            if (code == "rate_limit") Interlocked.Exchange(ref reason, code);
            else Interlocked.CompareExchange(ref reason, code, null);
        }
        void CheckPermit()
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (StopReason() is not null)
                throw new DecisionProtocolException(429, "benchmark_paused", "评测已收到暂停信号。");
        }

        async Task Work(CodexAppServer server)
        {
            try
            {
                while (StopReason() is null && !cancel.IsCancellationRequested && queue.TryDequeue(out var work))
                {
                    var (workflow, item) = work;
                    var caseId = item.S("case_id");
                    var attemptId = $"{session}-{Interlocked.Increment(ref attemptNumber):D4}";
                    var attemptPath = Path.Combine(attemptDirectory, attemptId + ".json");
                    var row = J.O(("attempt_id", attemptId), ("workflow", workflow.Name), ("case_id", caseId),
                        ("model", model), ("reasoning_effort", effort), ("reused", false), ("status", "running"),
                        ("terminal", false), ("started_at_utc", DateTimeOffset.UtcNow), ("calls", new JsonArray()));
                    await J.Write(attemptPath, row);
                    var caseTimer = Stopwatch.StartNew();
                    var calls = row["calls"]!.AsArray();
                    var workflowSession = new WorkflowSession(ask: async (node, state, questions) =>
                    {
                        CheckPermit();
                        var timer = Stopwatch.StartNew();
                        var call = J.O(("node_id", node), ("questions", questions.Count), ("status", "running"), ("batches", new JsonArray()));
                        var ids = questions.Select(q => q.Key).ToArray();
                        var safety = fresh && workflow.Name == "security_incidents";
                        if (safety) call["raw_probabilities"] = new JsonObject();
                        calls.Add(call);
                        await J.Write(attemptPath, row);
                        var batches = call["batches"]!.AsArray();
                        try
                        {
                            var request = JsonSerializer.SerializeToElement(new { model, state, questions }, J.Options);
                            var raw = await server.CreateJevSystemOneWithUsageAsync(request, usage =>
                            {
                                batches.Add(CodexPilot.BatchUsage(usage));
                                J.Write(attemptPath, row).GetAwaiter().GetResult();
                            }, cancel.Token, strictModel: true, beforeBatch: CheckPermit, observeBatch: fresh ? detail =>
                            {
                                if (detail.S("status") == "ok")
                                {
                                    if (safety)
                                        foreach (var answer in detail["raw_output"]?["answers"].Items() ?? [])
                                            call["raw_probabilities"]![ids[(int)answer.N("index")]] = answer["probabilities"]?.DeepClone();
                                    return;
                                }
                                var index = (int)detail.N("question_index", -1);
                                if (index >= 0 && index < ids.Length) detail["question_id"] = ids[index];
                                call["diagnostic"] = detail;
                                J.Write(attemptPath, row).GetAwaiter().GetResult();
                            } : null, batchTimeout: batchTimeout);
                            var response = raw.Deserialize<SystemOneResponse>() ?? throw new InvalidDataException("Codex 决策响应为空。");
                            if (response.Model != model || !response.Answers.Keys.ToHashSet().SetEquals(ids))
                                throw new InvalidDataException("Codex 模型或答案 ID 不符。");
                            if (batches.Count == 0 || batches.Any(UnknownUsage))
                                throw new DecisionProtocolException(502, "unknown_usage", "未收到完整的轮次 token 用量。");
                            if (safety) call["answers"] = JsonNode.Parse(raw.GetProperty("answers").GetRawText());
                            call["status"] = "ok";
                            return response.Answers.ToDictionary();
                        }
                        catch (Exception error)
                        {
                            var code = ErrorCode(error);
                            call["status"] = "error";
                            call["error_code"] = code;
                            if (code is "rate_limit" or "model_rerouted" or "unknown_usage") Pause(code);
                            throw;
                        }
                        finally
                        {
                            call["elapsed_seconds"] = timer.Elapsed.TotalSeconds;
                            await J.Write(attemptPath, row);
                        }
                    });

                    try
                    {
                        var decisions = await Workflows.Run(workflow, item, workflowSession);
                        if (!decisions.Items().Select(d => d.S("policy_id")).ToHashSet().SetEquals(workflow.Policies))
                            throw new InvalidDataException("工作流结果缺少适用策略。");
                        row["decisions"] = decisions;
                        row["status"] = "ok";
                        row["terminal"] = true;
                    }
                    catch (Exception error)
                    {
                        var code = ErrorCode(error);
                        row["error_code"] = code;
                        row["error"] = error.Message;
                        row["status"] = "error";
                        // Completed but invalid model answers count against the official decision metrics.
                        if (CodexFullCheckpoint.IsTerminalResult(row))
                        {
                            row["decisions"] = J.A(workflow.Policies.Select(policy =>
                                J.O(("policy_id", policy), ("status", "error"), ("actions", new JsonArray()))));
                            row["terminal"] = true;
                        }
                        else if (code != "benchmark_paused")
                        {
                            Pause(cancel.IsCancellationRequested ? "cancelled" : code);
                        }
                    }
                    row["elapsed_seconds"] = caseTimer.Elapsed.TotalSeconds;
                    row["finished_at_utc"] = JsonValue.Create(DateTimeOffset.UtcNow);
                    await J.Write(attemptPath, row);
                    var quota = cancel.IsCancellationRequested ? null : await CodexPilot.ReadQuota(server, CancellationToken.None);
                    if (quota is not null && CodexPilot.QuotaReached(quota))
                    {
                        Interlocked.CompareExchange(ref quotaTrigger, quota, null);
                        Pause("rate_limit");
                    }

                    await commitGate.WaitAsync();
                    try
                    {
                        if (row.B("terminal")) await checkpoint.SaveCompleted(row);
                        if (quota is not null && string.CompareOrdinal(quota.S("observed_at_utc"), quotaLatest.S("observed_at_utc")) > 0)
                            quotaLatest = quota;
                        attemptRows.Add(row);
                        finished++;
                        Console.WriteLine($"{checkpoint.Completed.Count}/{total} {workflow.Name}/{caseId} {row.S("status")} {row.N("elapsed_seconds"):F1}s"
                            + (StopReason() is { } stopped ? $"，暂停 {stopped}" : ""));
                        await Save(StopReason() is null ? "running" : "pausing", includeReport: finished % 25 == 0);
                    }
                    finally { commitGate.Release(); }
                }
            }
            catch
            {
                Pause("checkpoint_error");
                throw;
            }
        }

        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (workers.Count < parallelism && workers.Count < queue.Count && StopReason() is null)
            {
                var client = await CodexAppServer.StartAsync(executable, model, [], effort, startup.Token);
                workers.Add(client);
                if (workers.Count == 1)
                {
                    quotaBefore = quotaLatest = await CodexPilot.ReadQuota(client, startup.Token);
                    if (CodexPilot.QuotaReached(quotaLatest))
                    {
                        quotaTrigger = quotaLatest;
                        Pause("rate_limit");
                    }
                }
            }
            Console.WriteLine($"WorkflowEvals：{version} {model}/{effort}，{workers.Count} 路并行，已保存 {checkpoint.Completed.Count}/{total}。进度：{progressPath}");
            await Save(StopReason() is null ? "running" : "paused", includeReport: true);
            if (StopReason() is null)
                await Task.WhenAll(workers.Select(Work));
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            foreach (var worker in workers) await worker.DisposeAsync();
        }

        if (cancel.IsCancellationRequested) reason ??= "cancelled";
        var complete = checkpoint.Completed.Count == total;
        await Save(complete ? "complete" : "paused", includeReport: true);
        Console.WriteLine($"{(complete ? "全量完成" : "评测已暂停")} {checkpoint.Completed.Count}/{total}。对照报告：{reportPath}");
        return complete ? 0 : 1;
    }

    private static string ErrorCode(Exception error) =>
        error is DecisionProtocolException protocol ? protocol.Code : error.GetType().Name;

    private static bool IsTerminalAttempt(JsonObject row) => row.B("terminal") && CodexFullCheckpoint.IsTerminalResult(row);

    private static bool UnknownUsage(JsonNode? batch) => !CodexFullCheckpoint.HasKnownUsage(batch);
}

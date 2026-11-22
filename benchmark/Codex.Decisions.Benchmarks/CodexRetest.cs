using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Decisions;
using DeepSeek.Decisions.Benchmarks;
using SystemOneResponse = DeepSeek.Decisions.SystemOneResponse;

internal static class CodexRetest
{
    private const string TriageInstructions = """
        For this triage panel, distinguish the behavior actually observed, applicable authorization,
        and evidence that the behavior violated that authorization. Check records against the actor,
        asset, time window, and activity scope. Treat missing or non-matching records as uncertainty
        about authorization. Count repeated
        reports from the same source as one signal. Distinguish suspicion or an uncorroborated indicator
        from independent corroboration and direct proof. Use asset importance only when judging impact.
        Reflect the relevant evidence and remaining uncertainty in
        each distribution. Answer the original questions using the original response format.
        """;
    private const string TriageEvidenceInstructions = """
        Use the same observed facts across the three questions and apply each question's own criteria.
        A matching authorization that covers the actual activity, or reliable evidence that the alert
        is mistaken, supports the non-violating interpretation. Reliable evidence of exceeding applicable
        authorization or violating an established restriction supports the unauthorized interpretation.
        A missing or out-of-scope record leaves authorization unresolved. Weigh independent evidence on
        its own merits in either direction. A reliable direct observation can be strong on its own.
        For indirect evidence, weigh independent corroboration and credible conflicts.
        """;
    private const string TriageNoulScaleInstructions = """
        For each Noul question, 0 represents certainty that its statement is false, 1 represents certainty
        that it is true, and 0.5 is the midpoint when neither outcome is better supported. Use intermediate
        values to express the direction and strength of the evidence for that question. Keep the original
        [true, false] probability order.
        """;
    private const string TriageTruePositiveInstructions = """
        Only for the Noul question asking whether the alert describes unauthorized activity, weigh
        evidence in both directions. Matching authorization that covers the observed activity or
        reliable evidence of an expected event or false alarm supports a negative answer. Reliable
        direct evidence of an applicable restriction being violated or independent corroborating
        observations support an affirmative answer. Missing or inapplicable records leave
        authorization unverified by those records. Assess independent evidence on its own merits.
        Apply the original criteria to the other questions independently.
        """;
    private const string TriageObservedEvidenceInstructions = """
        Only for the Noul question asking whether the alert describes unauthorized activity,
        distinguish observed behavior from the detector's interpretation. Check whether authorization
        covers the actor, asset, time, and operation. An absent or mismatched record leaves permission
        unverified by that record. Weigh reliable evidence that the activity was authorized, expected,
        or did not occur, and reliable evidence that it breached an applicable restriction or exceeded
        established permission. A specific current observation or credible firsthand account can
        support either conclusion. Count repeated reports and aggregate counts of the same observation
        as one source. Weigh independent corroboration on its merits.
        Apply the original criteria to the other questions independently.
        """;
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(5);

    internal static async Task<int> Run(string[] args, bool compareTriage = false)
    {
        var variant = "v1";
        if (compareTriage && args is [.. var sameArgs, "--variant", var requestedVariant])
        {
            variant = requestedVariant;
            args = sameArgs;
        }
        var triageInstructions = variant switch
        {
            "v1" => TriageInstructions,
            "v2" => TriageInstructions + "\n\n" + TriageEvidenceInstructions,
            "v2-scale" => TriageInstructions + "\n\n" + TriageEvidenceInstructions + "\n\n" + TriageNoulScaleInstructions,
            "v3" => TriageTruePositiveInstructions,
            "v4" => TriageObservedEvidenceInstructions,
            _ => throw new ArgumentException("--variant 必须为 v1、v2、v2-scale、v3 或 v4。"),
        };
        var triageVariant = "security-triage-" + variant;
        var (executable, effort, root) = (compareTriage, args) switch
        {
            (true, ["--codex", var bin]) => (bin, "medium", "artifacts/workflowevals"),
            (true, ["--codex", var bin, "--root", var dataRoot]) => (bin, "medium", dataRoot),
            (false, ["--codex", var bin, "--effort", var requested]) => (bin, requested, "artifacts/workflowevals"),
            (false, ["--codex", var bin, "--effort", var requested, "--root", var dataRoot]) => (bin, requested, dataRoot),
            _ => throw new ArgumentException(compareTriage
                ? "用法：triage-ab --codex <本机 CLI 绝对路径> [--root <数据目录>] [--variant v1|v2|v2-scale|v3|v4]"
                : "用法：retest --codex <本机 CLI 绝对路径> --effort <精确推理档位> [--root <数据目录>]"),
        };
        var model = compareTriage ? "gpt-6.2-sol" : "gpt-6-luna";
        var parallelism = compareTriage ? 10 : 4;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("--codex 需要本机 Codex 可执行文件的绝对路径。");
        if (effort.Length == 0 || !effort.All(char.IsAsciiLetterLower))
            throw new ArgumentException("推理档位必须使用 CLI 上报的小写名称。");
        root = Path.GetFullPath(root);
        var data = await Data.Load(root);
        _ = Scoring.Validate(data);
        var triageRoot = Path.Combine(root, "codex-triage-ab");
        JsonNode? excludedPlan = null;
        HashSet<string>? excludedActivities = null;
        if (variant is "v3" or "v4")
        {
            var v1Folder = Path.Combine(triageRoot, "security-triage-v1");
            var v1Report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(v1Folder, "report.json")));
            excludedPlan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(v1Folder, "plan.json")));
            excludedActivities = excludedPlan?["cases"].Items().Select(row => row.S("activity")).ToHashSet(StringComparer.Ordinal);
            if (v1Report.S("status") != "complete" || v1Report?["cases"] is not JsonArray { Count: 36 } ||
                !JsonNode.DeepEquals(v1Report?["plan"], excludedPlan) ||
                excludedPlan.S("prompt_variant") != "security-triage-v1" || excludedPlan?["cases"] is not JsonArray { Count: 36 } ||
                excludedActivities is not { Count: 36 } || excludedActivities.Contains(""))
                throw new InvalidDataException("V3/V4 需要原 36 例完整的 V1 选样和报告来隔离活动底稿。");
        }
        var baseline = Path.Combine(root, "codex-full", compareTriage ? "gpt-6.2-sol-medium-fresh" : "gpt-6-luna-low");
        var security = compareTriage ? data.Single(d => d.Name == "security_incidents") : null;
        var triageSelection = security is null ? null : CodexTriageSelection.Load(security, baseline, excludedActivities);
        var selected = triageSelection is null ? CodexRetestSelection.Load(data, baseline) :
            triageSelection.Select(x => (Workflow: security!, x.Case, x.Prior, x.Jev)).ToList();

        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(compareTriage ? 120 : 60));
        var version = await CodexPilot.ReadVersion(executable, startup.Token);
        var folder = compareTriage ? Path.Combine(triageRoot, triageVariant) :
            Path.Combine(root, "codex-retest", $"{model}-{effort}");
        Directory.CreateDirectory(folder);
        using var runLock = new FileStream(Path.Combine(folder, ".run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var caseFolder = Path.Combine(folder, "cases");
        Directory.CreateDirectory(caseFolder);
        var attempts = Path.Combine(folder, "attempts");
        Directory.CreateDirectory(attempts);
        var planPath = Path.Combine(folder, "plan.json");
        var selectedCases = selected.ToDictionary(x => (x.Workflow.Name, x.Case.S("case_id")));
        var plan = J.O(("model", model), ("effort", effort), ("cli_version", version), ("code_revision", Data.CodeRevision),
            ("sources_sha256", J.Hash(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sources.json"))))),
            ("baseline_sha256", J.Hash(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(baseline, "report.json"))))),
            ("selection_rule", excludedPlan is not null ? CodexTriageSelection.V3SelectionRule :
                compareTriage ? CodexTriageSelection.SelectionRule : CodexRetestSelection.SelectionRule),
            ("cases", J.A(selected.Select(x => J.O(("workflow", x.Workflow.Name), ("case_id", x.Case.S("case_id")))))));
        JsonNode? v3Comparison = null;
        if (triageSelection is not null)
        {
            plan["prompt_variant"] = triageVariant;
            plan["additional_instructions"] = triageInstructions;
            plan["parallelism"] = parallelism;
            plan["cases"] = J.A(triageSelection.Select(x => J.O(("case_id", x.Case.S("case_id")),
                ("cohort", x.Cohort), ("activity", x.Activity), ("reference_agreement", x.ReferenceAgreement),
                ("baseline_case_sha256", J.Hash(x.Prior)))));
            if (excludedPlan is not null)
            {
                if (new[] { "model", "effort", "code_revision", "sources_sha256", "baseline_sha256" }
                    .Any(key => !JsonNode.DeepEquals(excludedPlan[key], plan[key])))
                    throw new InvalidDataException("V3/V4 与原 36 例的冻结来源或基线不一致。");
                plan["excluded_v1_plan_sha256"] = J.Hash(excludedPlan);
                if (variant == "v4")
                {
                    var v3Folder = Path.Combine(triageRoot, "security-triage-v3");
                    var v3Report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(v3Folder, "report.json")));
                    var v3Plan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(v3Folder, "plan.json")));
                    v3Comparison = v3Report?["comparison"];
                    if (v3Report.S("status") != "complete" || v3Report?["cases"] is not JsonArray { Count: 24 } v3Rows ||
                        v3Comparison.S("status") != "complete" || !JsonNode.DeepEquals(v3Report?["plan"], v3Plan) ||
                        v3Plan.S("prompt_variant") != "security-triage-v3" ||
                        v3Plan.S("additional_instructions") != TriageTruePositiveInstructions ||
                        new[] { "model", "effort", "code_revision", "sources_sha256", "baseline_sha256", "excluded_v1_plan_sha256", "cases" }
                            .Any(key => !JsonNode.DeepEquals(v3Plan?[key], plan[key])))
                        throw new InvalidDataException("V4 需要与完整 V3 报告匹配的同一批 24 例及基线。");
                    var v3Cases = v3Rows.OfType<JsonObject>()
                        .Where(row => row.S("workflow") == "security_incidents" && row.S("status") == "ok")
                        .ToLookup(row => row.S("case_id"));
                    if (triageSelection.Any(item => v3Cases[item.Case.S("case_id")].Count() != 1))
                        throw new InvalidDataException("V4 需要 V3 选样中的 24 例实际案例均成功且无重复。");
                    var recomputed = CodexTriageReport.Summarize(triageSelection, id => v3Cases[id].Single(), expectedPerCohort: 8);
                    if (!JsonNode.DeepEquals(v3Comparison, recomputed))
                        throw new InvalidDataException("V3 摘要与实际案例不一致。");

                    _ = CodexTriageReport.Summarize(triageSelection, _ => null, expectedPerCohort: 8, v3Comparison: v3Comparison);
                    plan["v3_report_sha256"] = J.Hash(v3Report);
                }
            }
            else if (variant != "v1")
            {
                var required = variant == "v2-scale" ? "v2" : "v1";
                var requiredName = "security-triage-" + required;
                var priorReportPath = Path.Combine(triageRoot, requiredName, "report.json");
                if (!File.Exists(priorReportPath))
                    throw new InvalidDataException($"先完成 {requiredName} 才能启动 {triageVariant}。");
                var priorReport = JsonNode.Parse(await File.ReadAllTextAsync(priorReportPath));
                var priorPlan = priorReport?["plan"];
                var priorInstructions = required == "v1" ? TriageInstructions :
                    TriageInstructions + "\n\n" + TriageEvidenceInstructions;
                if (priorReport.S("status") != "complete" || priorReport?["cases"] is not JsonArray { Count: 36 } ||
                    priorPlan.S("prompt_variant") != requiredName || priorPlan.S("additional_instructions") != priorInstructions ||
                    new[] { "model", "effort", "code_revision", "sources_sha256", "baseline_sha256", "cases" }
                        .Any(key => !JsonNode.DeepEquals(priorPlan?[key], plan[key])))
                    throw new InvalidDataException($"{requiredName} 的提示、案例清单或基线与当前实验不兼容。");
            }
        }
        var resuming = File.Exists(planPath);
        if (resuming)
        {
            var previous = JsonNode.Parse(await File.ReadAllTextAsync(planPath));
            if (!JsonNode.DeepEquals(plan, previous)) throw new InvalidDataException("复测参数或来源与现有检查点不一致。");
        }
        else await J.Write(planPath, plan);

        var done = new ConcurrentDictionary<(string Workflow, string CaseId), JsonObject>(
            Directory.EnumerateFiles(caseFolder, "*.json").Select(path =>
            {
                var row = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                var key = (row.S("workflow"), row.S("case_id"));
                if (!selectedCases.ContainsKey(key) || row.S("model") != model || row.S("reasoning_effort") != effort ||
                    row.S("status") is not ("ok" or "error") || row["decisions"] is not JsonArray)
                    throw new InvalidDataException($"复测检查点不兼容：{path}");
                return KeyValuePair.Create(key, row);
            }).Where(pair => pair.Value.S("error_code") != "backend_transport_failed" &&
                (pair.Value.S("error_code") != "backend_timeout" ||
                    pair.Value.N("batch_timeout_seconds") == BatchTimeout.TotalSeconds)));
        async Task PublishCase((string Workflow, string CaseId) key, string id, string log, JsonObject row)
        {
            var casePath = Path.Combine(caseFolder, id + ".json");
            if (File.Exists(casePath)) casePath = Path.Combine(caseFolder, Path.GetFileName(log));
            await J.Write(casePath, row);
            done[key] = row;
        }

        bool IsTerminalAttempt((string Workflow, string CaseId) key, string id, string log, JsonObject row)
        {
            if (!selectedCases.TryGetValue(key, out var test) ||
                row.S("model") != model || row.S("reasoning_effort") != effort ||
                row.S("prior_reasoning_effort") != test.Prior.S("reasoning_effort") ||
                !JsonNode.DeepEquals(row["prior_decisions"], test.Prior["decisions"]) ||
                !JsonNode.DeepEquals(row["jev_decisions"], test.Jev["decisions"]) ||
                row["calls"] is not JsonArray { Count: > 0 } calls || calls.Any(call => call is not JsonObject) ||
                !CodexFullCheckpoint.IsTerminalResult(row))
                return false;

            var status = row.S("status");
            var elapsed = row.N("elapsed_seconds", double.NaN);
            return Path.GetFileNameWithoutExtension(log).EndsWith("-" + id, StringComparison.Ordinal) &&
                double.IsFinite(elapsed) && elapsed >= 0 &&
                (status != "ok" || calls.Items().All(call => call.S("status") == "ok" &&
                    call["batches"] is JsonArray { Count: > 0 } batches && batches.All(CodexFullCheckpoint.HasKnownUsage))) &&
                (!compareTriage || calls.Items().Any(call => call.S("node_id") == "triage" && call.S("prompt_variant") == triageVariant)) &&
                row["decisions"] is JsonArray decisions && decisions.Count == test.Workflow.Policies.Length &&
                decisions.All(decision => decision is JsonObject && decision.S("status") == status && decision["actions"] is JsonArray) &&
                decisions.Items().Select(decision => decision.S("policy_id")).ToHashSet(StringComparer.Ordinal).SetEquals(test.Workflow.Policies);
        }

        if (resuming && done.Count < selected.Count)
        {
            // The attempt is written before publishing the case, including when the process exits between those writes.
            foreach (var log in Directory.EnumerateFiles(attempts, "*.json").OrderDescending(StringComparer.Ordinal))
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(log)) is not JsonObject row) continue;
                var key = (Workflow: row.S("workflow"), CaseId: row.S("case_id"));
                if (done.ContainsKey(key)) continue;
                var id = J.Hash(key.Workflow + "\n" + key.CaseId);
                if (!IsTerminalAttempt(key, id, log, row)) continue;
                await PublishCase(key, id, log, row);
                if (done.Count == selected.Count) break;
            }
        }

        var queue = new ConcurrentQueue<(WorkflowData Workflow, JsonObject Case, JsonObject Prior, JsonObject Jev)>(
            selected.Where(x => !done.ContainsKey((x.Workflow.Name, x.Case.S("case_id"))))
                .OrderByDescending(x => x.Workflow.Name == "security_incidents"));
        await using var first = await CodexAppServer.StartAsync(executable, model, [], effort, startup.Token);
        var quotaBefore = await CodexPilot.ReadQuota(first, startup.Token);
        var session = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        using var cancel = new CancellationTokenSource();
        using var writes = new SemaphoreSlim(1);
        var workers = new List<CodexAppServer> { first };
        string? stop = CodexPilot.QuotaReached(quotaBefore) ? "rate_limit" : null;
        JsonObject? quotaTrigger = stop is null ? null : quotaBefore;
        var quotaLatest = quotaBefore;
        var timer = Stopwatch.StartNew();

        void Pause(string reason) => Interlocked.CompareExchange(ref stop, reason, null);
        void CheckPermit()
        {
            cancel.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref stop) is not null)
                throw new DecisionProtocolException(429, "benchmark_paused", "复测已暂停派发。");
        }

        async Task Work(CodexAppServer server)
        {
            try
            {
                while (!cancel.IsCancellationRequested && Volatile.Read(ref stop) is null && queue.TryDequeue(out var test))
                {
                    var key = (test.Workflow.Name, test.Case.S("case_id"));
                    var id = J.Hash(key.Name + "\n" + key.Item2);
                    var log = Path.Combine(attempts, session + "-" + id + ".json");
                    var safety = key.Name == "security_incidents";
                    var calls = new JsonArray();
                    var row = J.O(("workflow", key.Name), ("case_id", key.Item2), ("model", model), ("reasoning_effort", effort),
                        ("status", "running"), ("batch_timeout_seconds", BatchTimeout.TotalSeconds),
                        ("prior_reasoning_effort", test.Prior.S("reasoning_effort")),
                        ("prior_decisions", test.Prior["decisions"]), ("jev_decisions", test.Jev["decisions"]),
                        ("prior_nodes", test.Prior["calls"].Items().Select(c => c.S("node_id"))),
                        ("jev_nodes", test.Jev["questions"].Items().Select(q => q.S("node_id")).Distinct()),
                        ("jev_questions", safety ? J.A(test.Jev["questions"].Items().Select(q => J.O(("node_id", q.S("node_id")),
                            ("question_id", q.S("question_id")), ("kind", q.S("kind")), ("probabilities", q["probabilities"])))) : null),
                        ("calls", calls));
                    calls = row["calls"]!.AsArray();
                    await J.Write(log, row);
                    var elapsed = Stopwatch.StartNew();
                    var workflow = new WorkflowSession(ask: async (node, state, questions) =>
                    {
                        CheckPermit();
                        var ids = questions.Select(q => q.Key).ToArray();
                        var call = J.O(("node_id", node), ("question_ids", ids), ("status", "running"),
                            ("batches", new JsonArray()), ("diagnostics", new JsonArray()));
                        var revisedTriage = compareTriage && node == "triage";
                        if (revisedTriage) call["prompt_variant"] = triageVariant;
                        if (safety) call["raw_probabilities"] = new JsonObject();
                        calls.Add(call);
                        var callTimer = Stopwatch.StartNew();
                        try
                        {
                            var request = JsonSerializer.SerializeToElement(new { model, state, questions }, J.Options);
                            var response = await server.CreateJevSystemOneWithUsageAsync(request,
                                usage => call["batches"]!.AsArray().Add(CodexPilot.BatchUsage(usage)), cancel.Token,
                                strictModel: true, beforeBatch: CheckPermit, observeBatch: detail =>
                                {
                                    if (detail.S("status") == "ok")
                                    {
                                        if (safety)
                                            foreach (var answer in detail["raw_output"]?["answers"].Items() ?? [])
                                                call["raw_probabilities"]![ids[(int)answer.N("index")]] = answer["probabilities"]?.DeepClone();
                                        return;
                                    }
                                    var position = (int)detail.N("question_index", -1);
                                    if (position >= 0 && position < ids.Length)
                                        detail["question_id"] = ids[position];
                                    call["diagnostics"]!.AsArray().Add(detail);
                                    J.Write(log, row).GetAwaiter().GetResult();
                                }, batchTimeout: BatchTimeout, additionalInstructions: revisedTriage ? triageInstructions : null);
                            var mapped = response.Deserialize<SystemOneResponse>()
                                ?? throw new InvalidDataException("复测决策响应为空。");
                            if (mapped.Model != model || !mapped.Answers.Keys.ToHashSet().SetEquals(ids))
                                throw new InvalidDataException("复测响应模型或题目 ID 不符。");
                            if (safety)
                            {
                                var answers = JsonNode.Parse(response.GetProperty("answers").GetRawText())!.AsObject();
                                foreach (var (_, answer) in answers)
                                    if (answer?["noul"] is JsonValue n)
                                    {
                                        var p = n.GetValue<double>();
                                        answer["probabilities"] = J.O(("true", p), ("false", 1 - p));
                                    }
                                call["answers"] = answers;
                            }
                            call["status"] = "ok";
                            return mapped.Answers.ToDictionary();
                        }
                        catch (Exception error)
                        {
                            var code = ErrorCode(error);
                            call["status"] = "error";
                            call["error_code"] = code;
                            if (code is "rate_limit" or "model_rerouted" or "backend_transport_failed") Pause(code);
                            throw;
                        }
                        finally
                        {
                            call["elapsed_seconds"] = callTimer.Elapsed.TotalSeconds;
                            await J.Write(log, row);
                        }
                    });
                    try
                    {
                        row["decisions"] = await Workflows.Run(test.Workflow, test.Case, workflow);
                        row["status"] = "ok";
                    }
                    catch (Exception error)
                    {
                        var code = ErrorCode(error);
                        row["error_code"] = code;
                        row["error"] = error.Message;
                        row["status"] = cancel.IsCancellationRequested ? "interrupted" : "error";
                        row["decisions"] = J.A(test.Workflow.Policies.Select(policy =>
                            J.O(("policy_id", policy), ("status", "error"), ("actions", new JsonArray()))));
                    }
                    row["elapsed_seconds"] = elapsed.Elapsed.TotalSeconds;
                    var terminal = IsTerminalAttempt(key, id, log, row);
                    if (!terminal)
                    {
                        row["status"] = "interrupted";
                        row.Remove("decisions");
                        Pause(cancel.IsCancellationRequested ? "cancelled" : row.S("error_code", "invalid_retest_result"));
                    }
                    await J.Write(log, row);
                    var quota = cancel.IsCancellationRequested ? null : await CodexPilot.ReadQuota(server, CancellationToken.None);
                    if (quota is not null && CodexPilot.QuotaReached(quota))
                    {
                        Interlocked.CompareExchange(ref quotaTrigger, quota, null);
                        Pause("rate_limit");
                    }
                    await writes.WaitAsync();
                    try
                    {
                        if (quota is not null && string.CompareOrdinal(quota.S("observed_at_utc"), quotaLatest.S("observed_at_utc")) > 0)
                            quotaLatest = quota;
                        if (terminal) await PublishCase(key, id, log, row);
                        Console.WriteLine($"{done.Count}/{selected.Count} {key.Name}/{key.Item2} {row.S("status")} {row.N("elapsed_seconds"):F1}s");
                    }
                    finally { writes.Release(); }
                }
            }
            catch
            {
                Pause("checkpoint_error");
                throw;
            }
        }

        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            while (workers.Count < Math.Min(queue.Count, parallelism) && Volatile.Read(ref stop) is null)
                workers.Add(await CodexAppServer.StartAsync(executable, model, [], effort, startup.Token));
            Console.WriteLine($"复测 {model}/{effort}，{workers.Count} 路，检查点 {done.Count}/{selected.Count}。{folder}");
            if (stop is null) await Task.WhenAll(workers.Select(Work));
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            foreach (var server in workers.Skip(1)) await server.DisposeAsync();
        }
        var completed = done.Count == selected.Count;
        var cohorts = new JsonArray();
        if (triageSelection is null)
        {
            foreach (var cohort in selected.GroupBy(x => x.Workflow.Name == "security_incidents" ? "security_branch_disagreement" : "invalid_backend_response"))
            {
                var evaluated = cohort.Where(x => done.ContainsKey((x.Workflow.Name, x.Case.S("case_id")))).ToArray();
                var sample = evaluated.Select(x => (x.Workflow.Name, x.Case.S("case_id"))).ToHashSet();
                var prior = data.ToDictionary(d => d.Name, d => evaluated.Where(x => x.Workflow.Name == d.Name).Select(x => x.Prior).ToList());
                var actual = data.ToDictionary(d => d.Name, d => evaluated.Where(x => x.Workflow.Name == d.Name)
                    .Select(x => done[(x.Workflow.Name, x.Case.S("case_id"))]).ToList());
                cohorts.Add(J.O(("cohort", cohort.Key), ("planned_cases", cohort.Count()), ("evaluated_cases", evaluated.Length),
                    ("prior", Scoring.Validate(data, prior, sample)), ("retest", Scoring.Validate(data, actual, sample)),
                    ("published_jev", Scoring.Validate(data, sample: sample))));
            }
        }
        var reportPath = Path.Combine(folder, "report.json");
        var report = J.O(("status", completed ? "complete" : "partial"),
            ("stop_reason", cancel.IsCancellationRequested ? "cancelled" : stop), ("plan", plan),
            ("batch_timeout_seconds", BatchTimeout.TotalSeconds),
            ("quota_before", quotaBefore), ("quota_latest", quotaLatest), ("quota_trigger", quotaTrigger),
            ("session_elapsed_seconds", timer.Elapsed.TotalSeconds), ("comparison", cohorts),
            ("cases", J.A(selected.Where(x => done.ContainsKey((x.Workflow.Name, x.Case.S("case_id"))))
                .Select(x => done[(x.Workflow.Name, x.Case.S("case_id"))]))));
        if (triageSelection is not null)
            report["comparison"] = CodexTriageReport.Summarize(triageSelection,
                id => done.GetValueOrDefault(("security_incidents", id)), expectedPerCohort: excludedPlan is null ? 12 : 8,
                v3Comparison: v3Comparison);
        await J.Write(reportPath, report);
        Console.WriteLine($"复测{(completed ? "完成" : "暂停")} {done.Count}/{selected.Count}。报告：{reportPath}");
        return completed ? 0 : 1;
    }

    private static string ErrorCode(Exception error) => error is DecisionProtocolException protocol ? protocol.Code : error.GetType().Name;
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Decisions;
using DeepSeek.Decisions.Benchmarks;

try
{
    if (args is ["retest", .. var retestArgs])
        return await CodexRetest.Run(retestArgs);
    if (args is ["triage-ab", .. var triageArgs])
        return await CodexRetest.Run(triageArgs, compareTriage: true);
    if (args is ["full", .. var fullArgs])
        return await CodexFull.Run(fullArgs);
    if (args is ["pilot", .. var pilotArgs])
        return await CodexPilot.Run(pilotArgs);
    if (args is ["pilot-report", var pilotPath])
    {
        var report = Path.GetFullPath(pilotPath);
        var runDirectory = Directory.GetParent(report)!;
        var dataRoot = runDirectory.Parent?.Parent?.FullName
            ?? throw new ArgumentException("无法从试跑报告路径找到 WorkflowEvals 数据目录。");
        var pilot = JsonNode.Parse(await File.ReadAllTextAsync(report))?.AsObject()
            ?? throw new InvalidDataException("试跑报告不是 JSON 对象。");
        var forecast = CodexPilotForecast.Summarize(pilot, await Data.Load(dataRoot));
        var forecastPath = Path.Combine(runDirectory.FullName, "forecast.json");
        await J.Write(forecastPath, forecast);
        Console.WriteLine($"试跑用量与全量估算：{forecastPath}");
        return 0;
    }

    var root = Path.GetFullPath(args switch
    {
        [] => "artifacts/workflowevals",
        ["--root", var path] => path,
        _ => throw new ArgumentException("用法：Codex.Decisions.Benchmarks [--root <本地 WorkflowEvals 数据目录>]"),
    });
    var data = await Data.Load(root);
    var jev = Scoring.Validate(data);
    var baseline = await Workflows.Replay(data);

    var fixture = Directory.CreateTempSubdirectory("workflowevals-codex-");
    try
    {
        var executable = StageFixture(fixture.FullName);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await CodexAppServer.StartAsync(executable, "fixture-model", startup.Token);
        var replay = new CodexReplay(server, Path.Combine(fixture.FullName, "workflow-replay.json"));
        var (runs, cases) = await Workflows.Replay(data, replay.Ask);
        var codex = Scoring.Validate(data, runs);
        var complete = codex.S("status") == "ok";
        var matched = cases.Items().Count(row => row.S("status") == "matched");
        var scores = J.A(codex["scores"].Items().Select(row => CompareScore(row, jev)));

        // The fixed corpus and SDK's batching boundaries provide independent checks of coverage.
        if (complete && (matched + cases.Items().Count(row => row.S("status") == "mismatch") != 705 ||
            replay.Panels != 1249 || replay.Questions != 12080 || replay.Turns != 1392 || replay.Renormalized != 35))
            throw new InvalidDataException("Codex 回放覆盖数与冻结语料不符。");

        var report = J.O(("status", complete ? "complete" : "incomplete"),
            ("mode", "offline_jev_recording_through_codex_sdk"), ("code_revision", Data.CodeRevision),
            ("model_calls", 0), ("fixture_model", server.Model), ("fixture_turns", replay.Turns),
            ("sdk_requests", replay.Panels), ("sdk_questions", replay.Questions),
            ("renormalized_distributions", replay.Renormalized),
            ("datasets", data.Select(d => new { workflow = d.Name, revision = d.Revision, jev_run = d.JevRun })),
            ("baseline_replay", baseline), ("jev_metrics", jev["jev_scores"]),
            ("matched_cases", matched), ("evaluated_cases", codex["cases"]),
            ("codex_replay_overall_equal_workflow_agreement", complete ? Overall(scores, "agreement") : (double?)null),
            ("jev_overall_equal_workflow_agreement", Overall(jev["jev_scores"], "agreement")),
            ("comparison", scores), ("cases", cases));
        var reportPath = Path.Combine(root, "codex-replay.json");
        await J.Write(reportPath, report);
        Console.WriteLine($"Codex 回放核对 {matched}/{jev.N("cases")} 个案例，{replay.Panels} 个 SDK 请求，{replay.Questions} 道题，{replay.Turns} 个模拟批次。");
        Console.WriteLine($"调整 {replay.Renormalized} 个舍入后的概率分布，真实模型调用 0。报告：{reportPath}");
        return complete ? 0 : 1;
    }
    finally
    {
        fixture.Delete(recursive: true);
    }
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static string StageFixture(string directory)
{
    var source = Path.Combine(AppContext.BaseDirectory, "fixture");
    var appHost = OperatingSystem.IsWindows() ? "CodexFixture.exe" : "CodexFixture";
    foreach (var name in new[] { appHost, "CodexFixture.dll", "CodexFixture.deps.json", "CodexFixture.runtimeconfig.json" })
        File.Copy(Path.Combine(source, name), Path.Combine(directory, name));
    File.Copy(Path.Combine(AppContext.BaseDirectory, "Codex.Decisions.dll"), Path.Combine(directory, "Codex.Decisions.dll"));
    var executable = Path.Combine(directory, appHost);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(executable, File.GetUnixFileMode(Path.Combine(source, appHost)));
    File.WriteAllText(Path.Combine(directory, "mode.txt"), "workflow-evals");
    return executable;
}

static JsonObject CompareScore(JsonNode codex, JsonObject jev)
{
    var published = jev["jev_scores"].Items().Single(reference => reference.S("workflow") == codex.S("workflow") &&
        reference.S("reference") == codex.S("reference") && reference.S("metric") == codex.S("metric"));
    var row = codex.DeepClone().AsObject();
    row["jev_correct"] = published["correct"].Copy();
    row["jev_denominator"] = published["denominator"].Copy();
    row["jev_agreement"] = published["agreement"].Copy();
    row["delta"] = row["agreement"] is not null && published["agreement"] is not null
        && row.N("denominator") == published.N("denominator")
        ? row.N("agreement") - published.N("agreement") : (double?)null;
    return row;
}

static double Overall(JsonNode? metrics, string field) => metrics.Items()
    .Where(row => row.S("reference") == "consensus")
    .GroupBy(row => row.S("workflow"))
    .Average(rows => rows.First().N(field));

using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexRetestSelection
{
    internal const string SelectionRule = "取 Luna-low 原运行的全部 39 个终态 invalid_backend_response。"
        + "安全样本仅选 Luna 曾进入处置且 Jev 未进入处置的成功新跑案例。"
        + "先从 consensus 不处置且 Jev 完整动作匹配的案例中，按 EDR、IDP、NET 各取 ordinal case ID 最小的两例，"
        + "缺额按 case ID 递增补齐。再从 consensus 处置的案例中按最小 case ID 选两个不同检测器，缺额按 case ID 递增补齐。";

    internal static List<(WorkflowData Workflow, JsonObject Case, JsonObject Prior, JsonObject Jev)> Load(
        List<WorkflowData> data, string fullFolder)
    {
        var manifest = Read(Path.Combine(fullFolder, "checkpoint.json"));
        var source = manifest.S("source_sha256");
        var pilot = manifest.S("pilot_sha256");
        if (manifest.S("code_revision") != Data.CodeRevision || manifest.S("model") != "gpt-6-luna" ||
            manifest.S("reasoning_effort") != "low" || source.Length != 64 || pilot.Length != 64)
            throw new InvalidDataException("复测只接受已冻结的 Luna-low 全量检查点。");

        var checkpoint = new Dictionary<(string Workflow, string CaseId), JsonObject>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(fullFolder, "cases"), "*.json"))
        {
            var row = Read(file);
            var reused = row.B("reused");
            var workflow = row.S("workflow");
            var id = row.S("case_id");
            if (row.S("source_sha256") != source || row.S("pilot_sha256") != pilot || row.S("reasoning_effort") != "low" ||
                (reused ? row.S("model") != "gpt-6.2-sol" || row.S("status") != "ok"
                    : row.S("model") != "gpt-6-luna" || !row.B("terminal") || row.S("status") is not ("ok" or "error")) ||
                !checkpoint.TryAdd((workflow, id), row))
                throw new InvalidDataException($"全量检查点来源、终态或案例唯一性不符：{workflow}/{id}。");
        }

        if (data.Count != Data.Specs.Length || Data.Specs.Any(spec => data.Count(workflow =>
                workflow.Name == spec.Name && workflow.Revision == spec.Revision && workflow.Cases.Count == spec.Count) != 1) ||
            checkpoint.Count != 705 || checkpoint.Values.Count(row => row.B("reused")) != 12)
            throw new InvalidDataException("复测需要冻结数据和 705 个唯一全量检查点，其中 12 个为已复用 pilot 案例。");

        var all = new List<(WorkflowData Workflow, JsonObject Case, JsonObject Prior, JsonObject Jev)>();
        foreach (var workflow in data)
        {
            var jev = workflow.JevCases.ToDictionary(row => row.S("case_id"), StringComparer.Ordinal);
            foreach (var item in workflow.Cases.OrderBy(row => row.S("case_id"), StringComparer.Ordinal))
            {
                var id = item.S("case_id");
                if (!checkpoint.Remove((workflow.Name, id), out var prior) || !jev.TryGetValue(id, out var original))
                    throw new InvalidDataException($"冻结案例缺少全量或 Jev 记录：{workflow.Name}/{id}。");
                all.Add((workflow, item, prior, original));
            }
        }

        var failed = all.Where(row => row.Prior.S("status") == "error").ToList();
        if (checkpoint.Count != 0 || failed.Count != 39 ||
            failed.Any(row => row.Prior.S("error_code") != "invalid_backend_response"))
            throw new InvalidDataException("复测集合要求 39 个终态 invalid_backend_response 且没有其他失败案例。");

        var eligible = all.Where(row => row.Workflow.Name == "security_incidents" && !row.Prior.B("reused") &&
                row.Prior.S("status") == "ok" && row.Jev.S("status") == "ok" &&
                row.Prior["calls"].Items().Any(call => call.S("node_id") == "containment" && call.S("status") == "ok") &&
                row.Jev["questions"].Items().All(question => question.S("node_id") != "containment"))
            .Select(row => (Row: row, Detector: Detector(row.Case), Consensus: Playbook(row.Case["consensus"]),
                Jev: Playbook(row.Jev))).ToArray();
        var referenceMiddle = eligible.Where(row => NoContainment(row.Consensus) &&
            Scoring.Compare("exact_actions", row.Jev, row.Consensus) == true).ToArray();
        var referenceAct = eligible.Where(row => !NoContainment(row.Consensus)).ToArray();
        var controls = new[] { "EDR", "IDP", "NET" }
            .SelectMany(detector => referenceMiddle.Where(row => row.Detector == detector).Take(2))
            .Concat(referenceMiddle).DistinctBy(row => row.Row.Case.S("case_id")).Take(6).ToArray();
        var counterexamples = referenceAct.GroupBy(row => row.Detector).Select(group => group.First())
            .Concat(referenceAct).DistinctBy(row => row.Row.Case.S("case_id")).Take(2).ToArray();
        if (controls.Length != 6 || counterexamples.Length != 2)
            throw new InvalidDataException("现有运行记录无法选出 6 个一致不处置样本和 2 个处置对照样本。");

        failed.AddRange(controls.Select(row => row.Row));
        failed.AddRange(counterexamples.Select(row => row.Row));
        return failed;
    }

    private static string Detector(JsonObject item) =>
        JsonNode.Parse(item.S("input_json")).S("alert").Split('-', 2)[0].Trim().ToUpperInvariant();

    private static bool NoContainment(JsonNode decision) => decision["actions"].Items().Single().S("name") is
        "auto_close" or "escalate_tier2" or "notify_user";

    private static JsonNode Playbook(JsonNode? record) => record?["decisions"].Items()
        .Single(row => row.S("policy_id") == "playbook" && row.S("status") == "ok")
        ?? throw new InvalidDataException("安全事件缺少有效的 playbook 动作标签。");

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"检查点为空：{path}");
}

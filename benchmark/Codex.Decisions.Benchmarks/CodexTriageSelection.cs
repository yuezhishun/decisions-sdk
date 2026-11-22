using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexTriageSelection
{
    internal const string ExtraContainment = "extra_containment";
    internal const string NoContainmentControl = "no_containment_control";
    internal const string ContainmentControl = "containment_control";
    internal const string SelectionRule = "每个活动底稿最多入样一次，保留技术编号，去除活动编号末尾的派生数字。"
        + "固定选择 12 个原 Sol 额外处置案例（OpenAI/Anthropic 同判不处置与分歧各 6 个）、"
        + "12 个三方均不处置的案例、12 个 consensus 需要处置的案例（Jev 进入与未进入各 6 个）。"
        + "优先保留 2 个原 Sol 接近处置边界的正对照，各组按检测器覆盖和活动底稿哈希选取。";
    internal const string V3SelectionRule = "排除 V1 的 36 个活动底稿，其余每个底稿最多入样一次。"
        + "额外处置、无需处置和需要处置各 8 例。额外处置组的 OpenAI/Anthropic 一致与分歧各 4 例。"
        + "需要处置组先取原 Sol 概率最低的两个底稿，再补至最多 4 个 Jev 也进入处置的底稿，"
        + "额外保证两个原 Sol 概率至少 0.95 的正对照。按检测器覆盖和活动底稿哈希填满各组。";

    internal static List<(JsonObject Case, JsonObject Prior, JsonObject Jev, string Cohort, string Activity, string ReferenceAgreement)>
        Load(WorkflowData workflow, string fullFolder, IReadOnlySet<string>? excludedActivities = null)
    {
        if (excludedActivities is not null &&
            (excludedActivities.Count != 36 || excludedActivities.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("V3 选样须排除 V1 的 36 个活动底稿。");
        var manifest = Read(Path.Combine(fullFolder, "checkpoint.json"));
        var report = Read(Path.Combine(fullFolder, "report.json"));
        var source = Read(Path.Combine(fullFolder, "..", "..", "sources.json"));
        var spec = Data.Specs.Single(s => s.Name == "security_incidents");
        if (workflow.Name != spec.Name || workflow.Revision != spec.Revision || workflow.Cases.Count != spec.Count ||
            manifest.S("code_revision") != Data.CodeRevision || manifest.S("source_sha256") != J.Hash(source) ||
            !manifest.ContainsKey("pilot_sha256") || manifest["pilot_sha256"] is not null ||
            manifest.S("model") != "gpt-6.2-sol" || manifest.S("reasoning_effort") != "medium" ||
            report.S("status") != "complete" || report["coverage"].N("completed_cases") != 705)
            throw new InvalidDataException("初筛实验要求已完成的 Sol medium 全量检查点和冻结安全数据。");

        var baseline = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fullFolder, "cases"), "*.json"))
        {
            var row = Read(path);
            if (row.S("workflow") != workflow.Name) continue;
            var id = row.S("case_id");
            if (row.S("source_sha256") != manifest.S("source_sha256") || !row.ContainsKey("pilot_sha256") ||
                row["pilot_sha256"] is not null || row.S("model") != "gpt-6.2-sol" || row.S("reasoning_effort") != "medium" ||
                row["reused"]?.GetValue<bool>() != false || !row.B("terminal") || row.S("status") != "ok" ||
                id.Length == 0 || !baseline.TryAdd(id, row))
                throw new InvalidDataException($"安全事件 Sol 检查点来源、终态或案例唯一性不符：{id}。");
        }

        var published = workflow.JevCases.ToDictionary(row => row.S("case_id"), StringComparer.Ordinal);
        if (baseline.Count != spec.Count || published.Count != spec.Count)
            throw new InvalidDataException("初筛实验要求 240 例均有 Sol 和公开 Jev 基线。");

        var candidates = workflow.Cases.Select(item =>
        {
            var id = item.S("case_id");
            if (!baseline.TryGetValue(id, out var prior) || !published.TryGetValue(id, out var jev) || jev.S("status") != "ok")
                throw new InvalidDataException($"安全事件缺少可配对的案例：{id}。");
            var triage = prior["calls"].Items().Single(c => c.S("node_id") == "triage");
            var p = triage["answers"]?["is_true_positive"].N("noul", double.NaN) ?? double.NaN;
            var sol = EnteredContainment(prior, "calls");
            var jevEntered = EnteredContainment(jev, "questions");
            if (triage.S("status") != "ok" || !double.IsFinite(p) || p is < 0 or > 1 || (p > .75) != sol ||
                sol != RequiresContainment(prior) || jevEntered != RequiresContainment(jev))
                throw new InvalidDataException($"安全事件初筛逐题日志与既有处置分支不符：{id}。");

            var activity = BaseActivity(item);
            var detector = JsonNode.Parse(item.S("input_json")).S("alert").Split('-', 2)[0].Trim().ToUpperInvariant();
            var consensus = RequiresContainment(item["consensus"]);
            var referenceAgree = RequiresContainment(item["openai"]) == RequiresContainment(item["anthropic"]);
            return (Id: id, Case: item, Prior: prior, Jev: jev, Activity: activity, Detector: detector,
                Sol: sol, JevEntered: jevEntered, Consensus: consensus, ReferenceAgree: referenceAgree, P: p);
        }).OrderBy(r => J.Hash(r.Activity), StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();

        var target = candidates.Where(r => r.Sol && !r.JevEntered && !r.Consensus).ToArray();
        var negative = candidates.Where(r => !r.Sol && !r.JevEntered && !r.Consensus).ToArray();
        var positive = candidates.Where(r => r.Sol && r.Consensus).ToArray();
        if (target.Length != 83 || target.Count(r => r.ReferenceAgree) != 57 || negative.Length != 84 || positive.Length != 72)
            throw new InvalidDataException("初筛实验的冻结基线分组与既有分析的 83、84、72 例不符。");

        var chosen = new List<(JsonObject Case, JsonObject Prior, JsonObject Jev, string Cohort, string Activity, string ReferenceAgreement)>();
        var byId = candidates.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var usedActivities = new HashSet<string>(excludedActivities ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        void Take(string cohort, IEnumerable<string> candidateIds, int count)
        {
            var pool = candidateIds.Select(id => byId[id]).ToArray();
            var detectors = chosen.Where(r => r.Cohort == cohort).Select(r => byId[r.Case.S("case_id")].Detector).ToList();
            for (var i = 0; i < count; i++)
            {
                var next = pool.Where(r => !usedActivities.Contains(r.Activity))
                    .OrderBy(r => detectors.Count(detector => detector == r.Detector)).FirstOrDefault();
                if (next.Id is null)
                    throw new InvalidDataException($"无法在独立活动底稿中选满安全事件分组：{cohort}。");
                usedActivities.Add(next.Activity);
                detectors.Add(next.Detector);
                chosen.Add((next.Case, next.Prior, next.Jev, cohort, next.Activity, next.ReferenceAgree ? "agree" : "disagree"));
            }
        }

        var perCohort = excludedActivities is null ? 12 : 8;
        if (excludedActivities is null)
        {
            Take(ContainmentControl, positive.Where(r => !r.JevEntered && r.P <= .80).Select(r => r.Id), 2);
            Take(ContainmentControl, positive.Where(r => r.JevEntered).Select(r => r.Id), 6);
            Take(ContainmentControl, positive.Where(r => !r.JevEntered).Select(r => r.Id), 4);
        }
        else
        {
            var nearest = positive.Where(r => !usedActivities.Contains(r.Activity))
                .OrderBy(r => r.P).DistinctBy(r => r.Activity).Take(2).ToArray();
            Take(ContainmentControl, nearest.Select(r => r.Id), 2);
            var jevControls = positive.Where(r => r.JevEntered && !usedActivities.Contains(r.Activity)).ToArray();
            var jevCount = Math.Min(4 - nearest.Count(r => r.JevEntered),
                jevControls.Select(r => r.Activity).Distinct().Count());
            Take(ContainmentControl, jevControls.Select(r => r.Id), jevCount);
            Take(ContainmentControl, positive.Where(r => !r.JevEntered && r.P >= .95).Select(r => r.Id), 2);
            Take(ContainmentControl, positive.Where(r => !r.JevEntered).Select(r => r.Id), perCohort - chosen.Count);
        }
        Take(ExtraContainment, target.Where(r => !r.ReferenceAgree).Select(r => r.Id), perCohort / 2);
        Take(ExtraContainment, target.Where(r => r.ReferenceAgree).Select(r => r.Id), perCohort / 2);
        Take(NoContainmentControl, negative.Select(r => r.Id), perCohort);

        if (chosen.Count != 3 * perCohort || usedActivities.Count != chosen.Count + (excludedActivities?.Count ?? 0) ||
            chosen.GroupBy(r => r.Cohort).Any(g => g.Count() != perCohort))
            throw new InvalidDataException($"初筛样本需包含三个各 {perCohort} 例的分组，且活动底稿须独立。");
        return chosen.OrderBy(r => r.Cohort, StringComparer.Ordinal).ThenBy(r => r.Case.S("case_id"), StringComparer.Ordinal).ToList();
    }

    internal static bool EnteredContainment(JsonNode record, string field) =>
        record[field].Items().Any(node => node.S("node_id") == "containment");

    internal static bool RequiresContainment(JsonNode? label)
    {
        var decision = label?["decisions"].Items().SingleOrDefault(d => d.S("policy_id") == "playbook" && d.S("status") == "ok");
        var action = decision?["primary_action"].S("name");
        if (string.IsNullOrEmpty(action)) throw new InvalidDataException("安全事件缺少有效的 playbook 动作。");
        return action is not ("auto_close" or "escalate_tier2" or "notify_user");
    }

    private static string BaseActivity(JsonObject item)
    {
        var activity = JsonNode.Parse(item.S("metadata_json")).S("activity");
        var dash = activity.LastIndexOf('-');
        var dot = activity.LastIndexOf('.');
        if (dash < 0 || dash + 1 == activity.Length)
            throw new InvalidDataException($"安全事件缺少可分组的活动底稿：{item.S("case_id")}。");
        return dot > dash && dot + 1 < activity.Length && activity[(dot + 1)..].All(char.IsAsciiDigit)
            ? activity[..dot]
            : activity;
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"安全事件检查点为空：{path}。");
}

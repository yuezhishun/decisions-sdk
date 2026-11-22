using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal static class CodexTriageReport
{
    internal static JsonObject Summarize(
        List<(JsonObject Case, JsonObject Prior, JsonObject Jev, string Cohort, string Activity, string ReferenceAgreement)> selected,
        Func<string, JsonObject?> revised, int expectedPerCohort = 12, JsonNode? v3Comparison = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPerCohort);
        var cohorts = new[] { CodexTriageSelection.ExtraContainment, CodexTriageSelection.NoContainmentControl,
            CodexTriageSelection.ContainmentControl };
        var expectedCases = cohorts.Length * expectedPerCohort;
        var v3Cases = v3Comparison?["cases"].Items().ToDictionary(row => row.S("case_id"));
        if (v3Comparison is not null && (expectedPerCohort != 8 || v3Comparison.S("status") != "complete" ||
            v3Cases?.Count != expectedCases))
            throw new InvalidDataException("V4 需要完整的 V3 24 例报告。");
        var cases = selected.Select(item =>
        {
            var caseId = item.Case.S("case_id");
            var label = item.Case["consensus"]!;
            var current = revised(caseId);
            var row = J.O(("case_id", caseId), ("cohort", item.Cohort), ("activity", item.Activity),
                ("reference_agreement", item.ReferenceAgreement),
                ("consensus", Result(label, CodexTriageSelection.RequiresContainment(label))),
                ("baseline", Sol(item.Prior)),
                ("jev", PublishedJev(item.Jev)),
                ("revised", current is null ? J.O(("status", "pending")) : Sol(current)));
            if (v3Cases is not null)
            {
                if (!v3Cases.TryGetValue(caseId, out var v3) || v3["revised"].S("status") != "ok" ||
                    v3["revised"]?["containment"] is not JsonValue ||
                    new[] { "cohort", "activity", "reference_agreement", "consensus", "baseline", "jev" }
                        .Any(key => !JsonNode.DeepEquals(row[key], v3[key])))
                    throw new InvalidDataException($"V3 的案例、已知分支或比较基线不匹配：{caseId}。");
                row["v3"] = v3["revised"].Copy();
            }
            return row;
        }).ToArray();
        if (cases.Length != expectedCases || cases.Select(row => row.S("case_id")).Distinct().Count() != cases.Length ||
            cases.Select(row => row.S("activity")).Distinct().Count() != cases.Length)
            throw new InvalidDataException($"初筛汇总要求 {expectedCases} 个案例和 {expectedCases} 个不同活动底稿。");

        var groups = cases.ToLookup(row => row.S("cohort"));
        if (cohorts.Any(cohort => groups[cohort].Count() != expectedPerCohort))
            throw new InvalidDataException($"初筛汇总要求三个分组各 {expectedPerCohort} 例。");

        var models = v3Cases is null ? new[] { "baseline", "revised", "jev" } : ["baseline", "v3", "revised", "jev"];
        var counts = SummarizeGroup("all", cases, models);
        var report = J.O(("status", counts.N("pending_cases") == 0 ? "complete" : "partial"),
            ("cohorts", J.A(new[] { counts }.Concat(cohorts.Select(cohort => SummarizeGroup(cohort, groups[cohort], models))))),
            ("extra_reference_agreement", J.A(new[] { "agree", "disagree" }.Select(agreement =>
                SummarizeGroup(agreement, groups[CodexTriageSelection.ExtraContainment]
                    .Where(row => row.S("reference_agreement") == agreement), models)))),
            ("cases", J.A(cases)));
        if (v3Cases is not null)
            report["acceptance"] = V4Acceptance(cases, counts);
        else if (expectedPerCohort == 8)
        {
            var baselineScores = counts["models"]!["baseline"]!;
            var revisedScores = counts["models"]!["revised"]!;
            var missed = revisedScores["missed_containment"];
            var extra = revisedScores["extra_containment"];
            var exact = revisedScores["exact_actions_vs_consensus"];
            var hasFullCoverage = counts.N("branch_paired_cases") == expectedCases && exact.N("denominator") == expectedCases;
            var allControlsContained = missed.N("denominator") == expectedPerCohort && missed.N("count") == 0;
            var fewerExtra = extra.N("denominator") == expectedCases - expectedPerCohort &&
                extra.N("count") < baselineScores["extra_containment"].N("count");
            var exactNotWorse = exact.N("denominator") == expectedCases &&
                exact.N("count") >= baselineScores["exact_actions_vs_consensus"].N("count");
            var passed = hasFullCoverage && allControlsContained && fewerExtra && exactNotWorse;
            report["acceptance"] = J.O(("status", hasFullCoverage ? (passed ? "passed" : "failed") : "incomplete"),
                ("passed", passed), ("full_coverage", hasFullCoverage), ("all_containment_controls_recognized", allControlsContained),
                ("fewer_extra_containments_than_baseline", fewerExtra), ("exact_actions_at_least_baseline", exactNotWorse));
        }
        return report;
    }

    private static JsonObject V4Acceptance(JsonObject[] cases, JsonObject totals)
    {
        var missed = cases.Where(c => c["consensus"].B("containment") && c["baseline"].B("containment") && !c["v3"].B("containment")).ToArray();
        var newExtra = cases.Where(c => !c["consensus"].B("containment") && !c["baseline"].B("containment") && c["v3"].B("containment")).ToArray();
        var corrected = cases.Where(c => !c["consensus"].B("containment") && c["baseline"].B("containment") && !c["v3"].B("containment")).ToArray();
        if (missed.Length != 2 || newExtra.Length != 1 || corrected.Length != 6)
            throw new InvalidDataException("V3 的处置回归案例数与 V4 验收条件不一致。");

        var exact = totals["models"]!["revised"]!["exact_actions_vs_consensus"];
        var fullCoverage = totals.N("branch_paired_cases") == cases.Length && totals.N("failed_cases") == 0;
        var recover = BranchTargets(missed, true);
        var remove = BranchTargets(newExtra, false);
        var preserve = BranchTargets(corrected, false);
        const int minimumExactActions = 15;
        var enoughExact = exact.N("denominator") == cases.Length && exact.N("count") >= minimumExactActions;
        var passed = fullCoverage && recover.B("passed") && remove.B("passed") && preserve.B("passed") && enoughExact;
        return J.O(("status", fullCoverage ? (passed ? "passed" : "failed") : "incomplete"), ("passed", passed),
            ("full_coverage", fullCoverage), ("recovered_v3_misses", recover), ("eliminated_v3_new_extras", remove),
            ("preserved_v3_corrected_extras", preserve), ("exact_actions_vs_consensus", exact),
            ("minimum_exact_actions", minimumExactActions), ("exact_actions_at_least_minimum", enoughExact));
    }

    private static JsonObject BranchTargets(JsonObject[] cases, bool containment)
    {
        var matching = cases.Where(row => row["revised"]?["containment"]?.GetValue<bool>() == containment).ToArray();
        return J.O(("count", matching.Length), ("denominator", cases.Length), ("passed", matching.Length == cases.Length),
            ("case_ids", cases.Select(row => row.S("case_id"))), ("matched_case_ids", matching.Select(row => row.S("case_id"))));
    }

    private static JsonObject PublishedJev(JsonObject record)
    {
        var question = record["questions"].Items().Single(row =>
            row.S("node_id") == "triage" && row.S("question_id") == "is_true_positive");
        var probability = question["probabilities"].Items().Single(option => option.S("option") == "true")["probability"]!.GetValue<double>();
        return Result(record, CodexTriageSelection.EnteredContainment(record, "questions"), probability);
    }

    private static JsonObject Result(JsonNode record, bool? containment, double? probability = null)
    {
        var status = record.S("status");
        var decision = record["decisions"].Items().SingleOrDefault(row => row.S("policy_id") == "playbook");
        if (status == "ok" && decision.S("status") != "ok")
            throw new InvalidDataException("成功的安全事件缺少有效的 playbook 决策。");
        return J.O(("status", status), ("containment", containment), ("triage_probability", probability),
            ("decision", decision), ("error_code", record["error_code"]));
    }

    private static JsonObject Sol(JsonObject record)
    {
        var triage = record["calls"].Items().SingleOrDefault(call => call.S("node_id") == "triage" && call.S("status") == "ok");
        var probability = triage?["answers"]?["is_true_positive"]?["noul"]?.GetValue<double>();
        var entered = CodexTriageSelection.EnteredContainment(record, "calls");
        if (record.S("status") == "ok" && (!probability.HasValue || (probability > .75) != entered))
            throw new InvalidDataException($"安全事件分支与初筛概率不符：{record.S("case_id")}。");

        return Result(record, probability.HasValue ? probability > .75 : entered ? true : null, probability);
    }

    private static JsonObject SummarizeGroup(string group, IEnumerable<JsonObject> rows, string[] models)
    {
        var cases = rows.ToArray();
        var terminal = cases.Where(row => row["revised"].S("status") is "ok" or "error").ToArray();
        var branchPaired = terminal.Where(row => row["revised"]!["containment"] is not null).ToArray();
        var needsContainment = branchPaired.Count(row => row["consensus"].B("containment"));
        var needsNoContainment = branchPaired.Length - needsContainment;
        var scores = J.O(models.Select(model => (model, (object?)J.O(
            ("extra_containment", Rate(branchPaired.Count(row => row[model].B("containment") && !row["consensus"].B("containment")),
                needsNoContainment)),
            ("missed_containment", Rate(branchPaired.Count(row => !row[model].B("containment") && row["consensus"].B("containment")),
                needsContainment)),
            ("exact_actions_vs_consensus", Rate(terminal.Count(row => Matches(row[model], row["consensus"])), terminal.Length)),
            ("exact_actions_vs_jev", model == "jev" ? null : Rate(terminal.Count(row => Matches(row[model], row["jev"])), terminal.Length))
        ))).ToArray());
        return J.O(("group", group), ("planned_cases", cases.Length), ("terminal_cases", terminal.Length),
            ("failed_cases", terminal.Count(row => row["revised"].S("status") == "error")),
            ("pending_cases", cases.Length - terminal.Length), ("branch_paired_cases", branchPaired.Length),
            ("unknown_branch_cases", terminal.Length - branchPaired.Length), ("models", scores));
    }

    private static bool Matches(JsonNode? prediction, JsonNode? reference) =>
        prediction.S("status") == "ok" && prediction?["decision"] is JsonObject actual &&
        reference?["decision"] is JsonObject wanted && Scoring.Compare("exact_actions", actual, wanted) == true;

    private static JsonObject Rate(int count, int denominator) =>
        J.O(("count", count), ("denominator", denominator), ("rate", denominator == 0 ? (double?)null : (double)count / denominator));
}

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeepSeek.Decisions.Benchmarks;

internal sealed class CodexFullCheckpoint : IDisposable
{
    internal const string ReusedModel = "gpt-6.2-sol";
    internal const string ReusedEffort = "low";
    private const string PinnedSourceSha256 = "b7dcbb41344f7ef706ce81a064cfbe33f89eab4d16a287e0cadfaedf390a1887";
    private readonly FileStream runLock;
    private readonly string model;
    private readonly string? effort;
    private readonly string? pilotHash;
    private readonly Dictionary<(string Workflow, string CaseId), string[]> policies;
    private readonly Dictionary<(string Workflow, string CaseId), JsonObject> completed = [];

    private CodexFullCheckpoint(string outputDir, FileStream runLock, List<WorkflowData> data,
        string model, string? effort, string? pilotHash)
    {
        DirectoryPath = outputDir;
        this.runLock = runLock;
        this.model = model;
        this.effort = effort;
        this.pilotHash = pilotHash;
        policies = data.SelectMany(workflow =>
        {
            var policyIds = workflow.Policies;
            return workflow.Cases.Select(row => (Identity: (workflow.Name, row.S("case_id")), PolicyIds: policyIds));
        }).ToDictionary(caseInfo => caseInfo.Identity, caseInfo => caseInfo.PolicyIds);
    }

    internal string DirectoryPath { get; }
    internal IReadOnlyDictionary<(string Workflow, string CaseId), JsonObject> Completed => completed;

    internal static async Task<CodexFullCheckpoint> Open(string outputDir, List<WorkflowData> data,
        string? pilotPath, string model, string? effort, string cliVersion, string? sourcesPath = null)
    {
        if (!Path.IsPathFullyQualified(outputDir)) throw new ArgumentException("检查点输出目录须为绝对路径。", nameof(outputDir));
        outputDir = Path.GetFullPath(outputDir);
        if (pilotPath is not null) pilotPath = Path.GetFullPath(pilotPath);
        sourcesPath ??= pilotPath is null
            ? throw new ArgumentException("未复用 Pilot 时须提供固定数据清单。", nameof(sourcesPath))
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pilotPath)!, "..", "..", "sources.json"));
        var source = JsonNode.Parse(await File.ReadAllTextAsync(sourcesPath));
        if (source.S("code_revision") != Data.CodeRevision || J.Hash(source) != PinnedSourceSha256 ||
            data.Count != Data.Specs.Length || Data.Specs.Any(spec => data.Count(row =>
                row.Name == spec.Name && row.Revision == spec.Revision && row.Cases.Count == spec.Count) != 1))
            throw new InvalidDataException("检查点要求使用同一份固定的 WorkflowEvals 数据及文件指纹。");

        string? pilotHash = null;
        JsonNode? pilot = null;
        if (pilotPath is not null)
        {
            var pilotBytes = await File.ReadAllBytesAsync(pilotPath);
            pilotHash = Convert.ToHexStringLower(SHA256.HashData(pilotBytes));
            var payloadOffset = pilotBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            pilot = JsonNode.Parse(pilotBytes.AsSpan(payloadOffset));
            if (pilot.S("status") != "complete" || pilot?["stop_reason"] is not null ||
                pilot.S("code_revision") != Data.CodeRevision ||
                pilot.S("thread_model") != ReusedModel || pilot.S("reasoning_effort") != ReusedEffort ||
                !pilot.S("cli_version").StartsWith("codex-cli ", StringComparison.Ordinal) || pilot.N("attempted_cases") != 12 ||
                pilot?["cases"] is not JsonArray { Count: 12 } || pilot["planned"] is not JsonArray { Count: 12 })
                throw new InvalidDataException("复用数据应包含固定代码版本下 gpt-6.2-sol/low 的 12 例完整试跑。");
            if (!JsonNode.DeepEquals(pilot["strict_model"], JsonValue.Create(true)))
                throw new InvalidDataException("旧版 Pilot 未确认每轮模型归属，不能作为全量评测的复用来源。");
            outputDir = Path.TrimEndingDirectorySeparator(outputDir) + "-pilot-" + pilotHash;
        }

        Directory.CreateDirectory(outputDir);
        FileStream runLock;
        try { runLock = new FileStream(Path.Combine(outputDir, ".run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException($"无法锁定检查点输出目录，可能已有运行写入：{outputDir}", ex); }

        try
        {
            var checkpoint = new CodexFullCheckpoint(outputDir, runLock, data, model, effort, pilotHash);
            var seeded = pilot is null ? [] : checkpoint.ValidatePilot(data, pilot["cases"]!.AsArray(), pilot["planned"]!.AsArray());
            var manifest = J.O(("version", 1), ("code_revision", Data.CodeRevision), ("source_sha256", PinnedSourceSha256),
                ("pilot_sha256", pilotHash), ("pilot_model", pilot is null ? null : ReusedModel),
                ("pilot_reasoning_effort", pilot is null ? null : ReusedEffort), ("pilot_cli_version", pilot?["cli_version"]),
                ("model", model), ("reasoning_effort", effort), ("cli_version", cliVersion),
                ("datasets", data.OrderBy(workflow => workflow.Name, StringComparer.Ordinal)
                    .Select(workflow => new { workflow = workflow.Name, revision = workflow.Revision, cases = workflow.Cases.Count })));
            var manifestPath = Path.Combine(outputDir, "checkpoint.json");
            var caseDir = Path.Combine(outputDir, "cases");
            var files = Directory.Exists(caseDir) ? Directory.GetFiles(caseDir, "*.json") : [];
            if (File.Exists(manifestPath))
            {
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath));
                if (!JsonNode.DeepEquals(manifest, saved))
                    throw new InvalidDataException("检查点与当前数据、pilot、模型、推理或 CLI 版本不兼容。");
            }
            else
            {
                if (files.Length != 0) throw new InvalidDataException("已有案例文件缺少检查点来源清单。");
                await J.Write(manifestPath, manifest);
            }

            foreach (var file in files)
            {
                var row = JsonNode.Parse(await File.ReadAllTextAsync(file))?.AsObject()
                    ?? throw new InvalidDataException($"检查点案例为空：{file}");
                var identity = checkpoint.Validate(row);
                var fromPilot = seeded.TryGetValue(identity, out var seed);
                if (!StringComparer.Ordinal.Equals(file, checkpoint.CasePath(identity)) ||
                    row.S("model") != (fromPilot ? ReusedModel : model) ||
                    row["reasoning_effort"]?.GetValue<string>() != (fromPilot ? ReusedEffort : effort) ||
                    row.S("source_sha256") != PinnedSourceSha256 || row["pilot_sha256"]?.GetValue<string>() != pilotHash ||
                    row["reused"]?.GetValue<bool>() != fromPilot || fromPilot && !JsonNode.DeepEquals(row, seed))
                    throw new InvalidDataException($"检查点案例来源或文件名不匹配：{file}");
                if (fromPilot || IsTerminalResult(row)) checkpoint.completed.Add(identity, row);
            }

            foreach (var (identity, row) in seeded)
            {
                if (checkpoint.completed.ContainsKey(identity)) continue;
                await J.Write(checkpoint.CasePath(identity), row);
                checkpoint.completed.Add(identity, row);
            }
            return checkpoint;
        }
        catch
        {
            runLock.Dispose();
            throw;
        }
    }

    internal async Task SaveCompleted(JsonObject row)
    {
        var identity = Validate(row);
        if (row.S("model") != model || row["reasoning_effort"]?.GetValue<string>() != effort || row.B("reused"))
            throw new InvalidDataException($"完成案例的模型、推理或来源不匹配：{identity.Workflow}/{identity.CaseId}");
        if (completed.ContainsKey(identity)) throw new InvalidOperationException($"案例已完成：{identity.Workflow}/{identity.CaseId}");
        if (!IsTerminalResult(row)) throw new InvalidDataException($"案例尚无有效终态：{identity.Workflow}/{identity.CaseId}");
        var path = CasePath(identity);
        var copy = WithProvenance(row, reused: false);
        if (File.Exists(path))
        {
            // Preserve the superseded record when the new terminal case is committed.
            var archiveDir = Path.Combine(DirectoryPath, "superseded-cases");
            Directory.CreateDirectory(archiveDir);
            var replacement = path + ".replacement";
            await J.Write(replacement, copy);
            File.Replace(replacement, path, Path.Combine(archiveDir, Path.GetFileName(path)));
        }
        else
        {
            await J.Write(path, copy);
        }
        completed.Add(identity, copy);
    }

    private Dictionary<(string Workflow, string CaseId), JsonObject> ValidatePilot(
        List<WorkflowData> data, JsonArray cases, JsonArray planned)
    {
        var byIdentity = cases.Select(row => row as JsonObject ?? throw new InvalidDataException("Pilot 含空案例。"))
            .ToDictionary(Validate);
        var plan = planned.Items().ToDictionary(row => (row.S("workflow"), row.S("case_id")));
        var seeded = new Dictionary<(string Workflow, string CaseId), JsonObject>();
        foreach (var workflow in data)
        {
            var jev = workflow.JevCases.ToDictionary(row => row.S("case_id"));
            var ordered = workflow.Cases.OrderBy(row => jev[row.S("case_id")]["questions"].Items().Count())
                .ThenBy(row => row.S("case_id"), StringComparer.Ordinal).ToArray();
            for (var stratum = 0; stratum < 3; stratum++)
            {
                var begin = ordered.Length * stratum / 3;
                var end = ordered.Length * (stratum + 1) / 3;
                var population = end - begin;
                var caseId = ordered[begin + (population - 1) / 2].S("case_id");
                var identity = (workflow.Name, caseId);
                var questions = jev[caseId]["questions"].Items().Count();
                if (!byIdentity.TryGetValue(identity, out var row) || row.S("status") != "ok" ||
                    !plan.TryGetValue(identity, out var selection) || selection.N("stratum") != stratum + 1 ||
                    selection.N("population") != population || selection.N("jev_questions") != questions ||
                    row.N("stratum") != stratum + 1 || row.N("population") != population || row.N("jev_questions") != questions ||
                    row["calls"].Items().Any(call => !ValidPilotCall(call, identity)) ||
                    !JsonNode.DeepEquals(row["jev_decisions"], jev[caseId]["decisions"]))
                    throw new InvalidDataException($"Pilot 与固定分层、Jev 标签或成功检查点不符：{workflow.Name}/{caseId}");
                seeded.Add(identity, WithProvenance(row, reused: true));
            }
        }
        return seeded;
    }

    private static bool ValidPilotCall(JsonNode call, (string Workflow, string CaseId) identity) =>
        call.S("workflow") == identity.Workflow && call.S("case_id") == identity.CaseId && call.S("status") == "ok" &&
        call["batches"] is JsonArray { Count: > 0 } batches && batches.All(HasKnownUsage);

    internal static bool IsTerminalResult(JsonObject row) => row.S("status") == "ok" ||
        row.S("status") == "error" && row.S("error_code") is ("invalid_backend_response" or "model_refusal") &&
        row["calls"].Items().LastOrDefault()?["batches"] is JsonArray { Count: > 0 } batches && batches.All(HasKnownUsage);

    internal static bool HasKnownUsage(JsonNode? batch) =>
        batch is JsonObject && IsTokenCount(batch["inputTokens"]) && IsTokenCount(batch["outputTokens"]);

    private static bool IsTokenCount(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0;

    private (string Workflow, string CaseId) Validate(JsonObject row)
    {
        var identity = (Workflow: row.S("workflow"), CaseId: row.S("case_id"));
        var status = row.S("status");
        if (!policies.TryGetValue(identity, out var expected) || status is not ("ok" or "error") ||
            row["decisions"] is not JsonArray decisions || decisions.Count != expected.Length ||
            !decisions.Items().Select(decision => decision.S("policy_id")).ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
            decisions.Items().Any(decision => decision.S("status") != status) ||
            row["calls"] is not JsonArray calls || calls.Count == 0 || calls.Items().Count() != calls.Count ||
            !double.IsFinite(row.N("elapsed_seconds", double.NaN)) || row.N("elapsed_seconds") < 0)
            throw new InvalidDataException($"检查点案例状态、策略覆盖或已知用量记录不完整：{identity.Workflow}/{identity.CaseId}");
        if (status == "ok" && decisions.Items().Any(decision => decision["actions"] is not JsonArray actions ||
            actions.Items().Count() != actions.Count || actions.Items().Any(action => action.S("name").Length == 0) ||
            decision["primary_action"] is { } primary && primary.S("name").Length == 0))
            throw new InvalidDataException($"检查点成功案例的动作记录不完整：{identity.Workflow}/{identity.CaseId}");
        return identity;
    }

    private JsonObject WithProvenance(JsonObject row, bool reused)
    {
        var copy = row.DeepClone().AsObject();
        copy["model"] = reused ? ReusedModel : model;
        copy["reasoning_effort"] = reused ? ReusedEffort : effort;
        copy["source_sha256"] = PinnedSourceSha256;
        copy["pilot_sha256"] = pilotHash;
        copy["reused"] = reused;
        return copy;
    }

    private string CasePath((string Workflow, string CaseId) identity) =>
        Path.Combine(DirectoryPath, "cases", J.Hash(identity.Workflow + "\n" + identity.CaseId) + ".json");

    public void Dispose() => runLock.Dispose();
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Codex.Decisions;
using DeepSeek.Decisions;
using DeepSeek.Decisions.Benchmarks;

internal sealed class CodexReplay(CodexAppServer server, string recordingPath)
{
    public int Panels { get; private set; }
    public int Questions { get; private set; }
    public int Renormalized { get; private set; }
    public long Turns { get; private set; }

    internal async Task<Dictionary<string, SystemOneAnswer>> Ask(string node, JsonNode state, JsonObject questions,
        Dictionary<string, SystemOneAnswer> recorded)
    {
        var fixtureQuestions = new JsonArray();
        var expected = new Dictionary<string, SystemOneAnswer>();
        var adjusted = 0;
        foreach (var (id, question) in questions)
        {
            var source = recorded[id];
            var probabilities = source.Type == "noul" && source.Noul is { } noul
                ? new Dictionary<string, double> { ["true"] = noul, ["false"] = 1 - noul }
                : source.Probabilities ?? throw new InvalidDataException($"缺少已记录的概率：{node}/{id}。");
            if (probabilities.Count == 0 || probabilities.Values.Any(p => !double.IsFinite(p) || p is < 0 or > 1))
                throw new InvalidDataException($"录制概率超出范围：{node}/{id}。");
            var total = probabilities.Values.Sum();
            if (total <= 0)
                throw new InvalidDataException($"录制概率总和为零：{node}/{id}。");
            var normalize = Math.Abs(total - 1) > 0.005;
            if (normalize) adjusted++;

            var fixtureValues = probabilities.Values.Select(value => normalize ? value / total : value).ToArray();
            var comparison = probabilities.Select(p => new { option = p.Key, probability = p.Value / total }).ToArray();
            expected.Add(id, Answers.Read(J.O(("kind", source.Type), ("probabilities", comparison)), question!));

            var choices = question!["criteria"];
            if (source.Type == "choice")
                choices = J.A(choices!.AsObject().Select(p => J.O(("value", p.Key), ("description", p.Value))));
            fixtureQuestions.Add(J.O(("index", fixtureQuestions.Count), ("type", source.Type),
                ("instructions", question["instructions"]), ("choices", choices), ("probabilities", fixtureValues)));
        }
        await J.Write(recordingPath, J.O(("state", state), ("questions", fixtureQuestions)));

        var request = JsonSerializer.SerializeToElement(new { model = server.Model, state, questions }, J.Options);
        var response = await server.CreateJevSystemOneAsync(request, enableImages: false, enableLongContext: false);
        var mapped = response.Deserialize<SystemOneResponse>() ?? throw new InvalidDataException("Codex 响应为空。");
        if (mapped.Model != server.Model || !mapped.Answers.Keys.SequenceEqual(expected.Keys))
            throw new InvalidDataException($"Codex 模型或答案 ID 不符：{node}。");
        foreach (var (id, reference) in expected)
        {
            var answer = mapped.Answers[id];
            Require(answer.Type == reference.Type && answer.Choice == reference.Choice &&
                Close(answer.Noul, reference.Noul) && Close(answer.Score, reference.Score) &&
                Close(answer.Confidence, reference.Confidence), node, id);
            if (reference.Probabilities is { } distribution)
            {
                Require(answer.Probabilities is { } actual && actual.Keys.SequenceEqual(distribution.Keys) &&
                    distribution.All(pair => Close(actual[pair.Key], pair.Value)), node, id);
            }
            if (answer.Type == "score")
            {
                var criteria = questions[id]!["criteria"]!.AsArray();
                var legend = answer.Legend;
                Require(legend?.Count == criteria.Count &&
                    criteria.Select((item, index) => (item, index)).All(pair =>
                        legend.TryGetValue(pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture), out var level) &&
                        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pair.item), JsonSerializer.SerializeToElement(level))), node, id);
            }
        }

        if (mapped.Usage.InputTokens is not > 0 || mapped.Usage.OutputTokens != mapped.Usage.InputTokens)
            throw new InvalidDataException($"Codex 合成批次用量不符：{node}。");
        Panels++;
        Questions += mapped.Answers.Count;
        Renormalized += adjusted;
        Turns += mapped.Usage.InputTokens.Value;
        return mapped.Answers.ToDictionary();
    }

    private static bool Close(double? actual, double? expected) =>
        actual == expected || actual is { } a && expected is { } e && Math.Abs(a - e) <= 1e-12;

    private static void Require(bool condition, string node, string question)
    {
        if (!condition) throw new InvalidDataException($"Codex 答案映射和归一化后的录制值不符：{node}/{question}。");
    }
}

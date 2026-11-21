using System.ClientModel;
using System.Text.Json;
using OpenAI.Decisions;

#pragma warning disable OPENAI001 // The official Decisions .NET APIs are experimental.
if (args is not [var baseUrl, var codexModel, var deepSeekModel])
    throw new ArgumentException("Expected the HTTP Demo address and both model names.");

var credential = new ApiKeyCredential(Environment.GetEnvironmentVariable("DECISIONS_DEMO_API_KEY")!);
foreach (var (backend, model) in new[] { ("codex", codexModel), ("deepseek", deepSeekModel) })
{
    var endpoint = new Uri(new Uri(baseUrl), $"{backend}/v1/");
    var client = new DecisionsClient(credential, new DecisionsClientOptions { Endpoint = endpoint });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var request = new DecisionCreationOptions(
        [new DecisionsUserMessageItem([new DecisionsInputTextContentPart("The meeting is Tuesday.")])],
        model,
        [new DecisionsPredicateQuestion([new DecisionsInputTextInstructionsPart("Is a day specified?")])
            { Name = "scheduled" }]);
    using var response = (await client.CreateDecisionAsync(request, timeout.Token)).GetRawResponse();
    using var body = JsonDocument.Parse(response.Content.ToMemory());
    var result = body.RootElement;
    var answer = result.GetProperty("answers").EnumerateArray().Single();
    if (response.Status != 200 || result.GetProperty("model").GetString() != model ||
        answer.GetProperty("type").GetString() != "predicate" || answer.GetProperty("name").GetString() != "scheduled" ||
        answer.GetProperty("probability").GetDouble() <= 0.5 || result.GetProperty("usage").GetProperty("total_tokens").GetInt64() <= 0)
        throw new InvalidOperationException($"Unexpected Decisions response on {backend}.");
    Console.WriteLine($"{backend} official OpenAI SDK: passed");
}

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using Codex.Decisions;
using DeepSeek;
using DeepSeek.Decisions;

internal sealed class DemoBackends : IAsyncDisposable
{
    private readonly IConfiguration settings;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private readonly SemaphoreSlim codexGate = new(1, 1);
    private readonly DecisionsClient? deepseek;
    private CodexAppServer? codex;

    public DemoBackends(IConfiguration settings)
    {
        this.settings = settings;
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;

        var endpoint = new Uri(settings["DeepSeekEndpoint"] ?? "https://api.deepseek.com/", UriKind.Absolute);
        if (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Scheme != Uri.UriSchemeHttps && (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
            throw new InvalidOperationException("DeepSeekEndpoint must be HTTPS or loopback HTTP.");
        deepseek = new DeepSeekClient(key, new DeepSeekClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(http),
            AllowMessageContentLogging = false,
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetDecisionsClient(settings["DeepSeekModel"] ?? "deepseek-flash");
    }

    public async Task<JsonElement> EvaluateAsync(string backend, JsonElement request, bool jev, bool images,
        bool longContext, CancellationToken ct)
    {
        if (backend == "codex")
        {
            var server = await GetCodexAsync(ct);
            return jev
                ? await server.CreateJevSystemOneAsync(request, images, longContext, ct)
                : await server.CreateOpenAiDecisionAsync(request, ct);
        }

        var client = RequireDeepSeek();
        return jev
            ? await client.CreateJevSystemOneAsync(request, images, longContext, ct)
            : await client.CreateOpenAiDecisionAsync(request, ct);
    }

    public async Task<object> ListModelsAsync(string backend, CancellationToken ct)
    {
        var prefix = backend == "codex" ? "Codex" : "DeepSeek";
        if (!DateOnly.TryParseExact(settings[prefix + "ReleaseDate"], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
            throw new DemoRequestException(503, "backend_unavailable",
                $"Configure DecisionsDemo__{prefix}ReleaseDate as yyyy-MM-dd for model discovery.");

        var model = backend == "codex" ? (await GetCodexAsync(ct)).Model : RequireDeepSeek().Model;
        var description = settings[prefix + "Description"] ?? $"{prefix} model configured in DecisionsDemo.";
        return new { models = new[] { new { name = model, description, release_date = releaseDate } } };
    }

    private DecisionsClient RequireDeepSeek() => deepseek ??
        throw new DemoRequestException(503, "backend_unavailable", "Configure DEEPSEEK_API_KEY to use the DeepSeek backend.");

    private async Task<CodexAppServer> GetCodexAsync(CancellationToken ct)
    {
        await codexGate.WaitAsync(ct);
        try
        {
            var effort = settings["CodexReasoningEffort"];
            return codex ??= await CodexAppServer.StartAsync(settings["CodexExecutable"], settings["CodexModel"],
                ["DECISIONS_DEMO_API_KEY", "DEEPSEEK_API_KEY", "OPENAI_API_KEY"],
                string.IsNullOrWhiteSpace(effort) ? null : effort, ct);
        }
        finally { codexGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (codex is not null) await codex.DisposeAsync();
        codexGate.Dispose();
        http.Dispose();
    }
}

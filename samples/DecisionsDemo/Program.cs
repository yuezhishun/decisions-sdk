using System.ClientModel;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeek;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("DecisionsDemo");
var key = Environment.GetEnvironmentVariable("DECISIONS_DEMO_API_KEY");
if (string.IsNullOrWhiteSpace(key))
    throw new InvalidOperationException("Set DECISIONS_DEMO_API_KEY before starting the demo.");

var apiKey = Encoding.UTF8.GetBytes(key);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
    options.Listen(IPAddress.Loopback, settings.GetValue("Port", 3090));
    options.Limits.MaxRequestBodySize = 48L * 1024 * 1024;
});
await using var backends = new DemoBackends(settings);
await using var app = builder.Build();

foreach (var backend in new[] { "codex", "deepseek" })
{
    var group = app.MapGroup($"/{backend}/v1");
    group.MapPost("/decisions", (Delegate)((HttpContext context) => HandleAsync(context, backend, jev: false, list: false)));
    group.MapPost("/systemone", (Delegate)((HttpContext context) => HandleAsync(context, backend, jev: true, list: false)));
    group.MapGet("/models", (Delegate)((HttpContext context) => HandleAsync(context, backend, jev: true, list: true)));
}

await app.RunAsync();

async Task<IResult> HandleAsync(HttpContext context, string backend, bool jev, bool list)
{
    var headers = context.Request.Headers;
    if (headers.Authorization.Count != 1 ||
        !AuthenticationHeaderValue.TryParse(headers.Authorization[0], out var authorization) ||
        !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
        authorization.Parameter is not { } token ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), apiKey))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Error(jev, 401, "invalid_api_key", "Missing or invalid Bearer key.");
    }

    context.Response.Headers.CacheControl = "no-store";
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromMinutes(3));
    var ct = timeout.Token;
    try
    {
        if (list) return Results.Json(await backends.ListModelsAsync(backend, ct));

        bool images = jev && ReadFlag(context.Request, "TypeSafe-Danger-Zone-Images");
        bool longContext = jev && ReadFlag(context.Request, "TypeSafe-Danger-Zone-Long-Context");
        if (jev && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 32L * 1024 * 1024;
        var request = jev ? await JevTransport.ReadAsync(context.Request, ct) : await ReadOpenAiAsync(context.Request, ct);
        return Results.Json(await backends.EvaluateAsync(backend, request, jev, images, longContext, ct));
    }
    catch (DemoRequestException e) { return Error(jev, e.StatusCode, e.Code, e.Message, e.Parameter); }
    catch (Codex.Decisions.DecisionProtocolException e) { return Error(jev, e.StatusCode, e.Code, e.Message, e.Parameter); }
    catch (DeepSeek.Decisions.DecisionProtocolException e) { return Error(jev, e.StatusCode, e.Code, e.Message, e.Parameter); }
    catch (BadHttpRequestException e) { return Error(jev, e.StatusCode, "invalid_request", "Invalid HTTP request body."); }
    catch (JsonException) { return Error(jev, 400, "invalid_json", "Invalid JSON request body."); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return Results.Empty; }
    catch (OperationCanceledException) { return Error(jev, 504, "backend_timeout", "The backend timed out."); }
    catch (Exception e) when (e is DeepSeekException or ClientResultException or HttpRequestException or
        InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
    {
        var status = e switch
        {
            DeepSeekException error => error.Status,
            ClientResultException error => error.Status,
            HttpRequestException error => (int)(error.StatusCode ?? 0),
            _ => 0
        };
        var transport = (e as HttpRequestException ?? e.InnerException as HttpRequestException)?.HttpRequestError;
        app.Logger.LogWarning("Decision demo backend {Backend} failed ({ErrorType}, {Status}, {Transport})",
            backend, e.GetType().Name, status, transport);
        return BackendError(jev, status);
    }
}

static bool ReadFlag(HttpRequest request, string name)
{
    if (!request.Headers.TryGetValue(name, out var value)) return false;
    if (value.Count == 1 && value[0] == "true") return true;
    throw new DemoRequestException(422, "literal_error", "The header value must be true.", name);
}

static async Task<JsonElement> ReadOpenAiAsync(HttpRequest request, CancellationToken ct)
{
    if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding"))
        throw new DemoRequestException(415, "unsupported_media_type", "Send an application/json body.");
    using var json = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 128 }, ct);
    return json.RootElement.Clone();
}

static IResult BackendError(bool jev, int status) => status switch
{
    429 => Error(jev, 429, "rate_limit_exceeded", "The backend rate limit was reached."),
    503 or 529 when jev => Error(true, 529, "backend_overloaded", "The backend is temporarily overloaded."),
    504 => Error(jev, 504, "backend_timeout", "The backend timed out."),
    _ => Error(jev, 502, "backend_error", "The selected backend could not complete the evaluation.")
};

static IResult Error(bool jev, int status, string code, string message, string? parameter = null)
{
    object body;
    if (jev)
    {
        body = status == 422
            ? new { detail = new[] { new { loc = ErrorLocation(parameter), msg = message, type = code } } }
            : new { detail = message };
    }
    else
    {
        var type = status >= 500 ? "server_error" : status == 429 ? "rate_limit_error" : "invalid_request_error";
        body = new { error = new { message, type, param = parameter, code } };
    }
    return Results.Json(body, statusCode: status);
}

static object[] ErrorLocation(string? parameter)
{
    if (parameter?.StartsWith("TypeSafe-", StringComparison.Ordinal) == true)
        return ["header", parameter];
    if (string.IsNullOrEmpty(parameter) || parameter == "body") return ["body"];
    return new object[] { "body" }.Concat(parameter.Split(['.', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(part, out var index) ? (object)index : part)).ToArray();
}

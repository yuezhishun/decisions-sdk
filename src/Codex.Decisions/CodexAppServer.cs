using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Codex.Decisions;

public sealed partial class CodexAppServer : IAsyncDisposable
{
    private const string ChatGptUrl = "https://chatgpt.com/backend-api/";
    private static readonly string[] DisabledFeatures =
    [
        "shell_tool", "shell_snapshot", "hooks", "plugins", "remote_plugin", "plugin_sharing", "apps",
        "computer_use", "browser_use", "browser_use_external", "in_app_browser", "in_app_local_automation",
        "multi_agent", "multi_agent_v2", "skill_mcp_dependency_install", "skill_search"
    ];
    private static readonly string[] DisabledInstructions =
        ["include_environment_context", "include_apps_instructions", "include_collaboration_mode_instructions", "include_permissions_instructions"];

    private readonly string executable;
    private readonly string[] privateEnvironment;
    private readonly string? requestedEffort;
    private readonly DirectoryInfo workingDirectory;
    private readonly SemaphoreSlim gate = new(1);
    private Process? process;
    private Task? stderr;
    private int nextId;
    private string? effort;
    private bool disposed;

    private CodexAppServer(string? executable, IReadOnlyList<string> privateEnvironment, string? requestedEffort)
    {
        executable = string.IsNullOrWhiteSpace(executable) ? FindExecutable() : executable;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("请提供可用的 Codex 可执行文件绝对路径。", nameof(executable));
        this.executable = executable;
        this.privateEnvironment = [.. privateEnvironment];
        this.requestedEffort = requestedEffort;
        workingDirectory = Directory.CreateTempSubdirectory("sag-codex-");
    }

    public string Model { get; private set; } = "";
    public string Plan { get; private set; } = "unknown";
    internal string? ReasoningEffort => effort;
    internal bool SupportsImages { get; private set; }

    public static Task<CodexAppServer> StartAsync(string? executable, CancellationToken ct = default) =>
        StartAsync(executable, null, ct);

    public static Task<CodexAppServer> StartAsync(string? executable, string? model, CancellationToken ct) =>
        StartAsync(executable, model, [], ct);

    public static Task<CodexAppServer> StartAsync(string? executable, string? model,
        IReadOnlyList<string> privateEnvironment, CancellationToken ct) =>
        StartAsync(executable, model, privateEnvironment, null, ct);

    internal static async Task<CodexAppServer> StartAsync(string? executable, string? model,
        IReadOnlyList<string> privateEnvironment, string? reasoningEffort, CancellationToken ct)
    {
        var client = new CodexAppServer(executable, privateEnvironment, reasoningEffort) { Model = model?.Trim() ?? "" };
        try
        {
            await client.StartProcessAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string FindExecutable()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(entry => entry.Trim('"')).Where(Path.IsPathFullyQualified).ToArray();
        var name = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && (OperatingSystem.IsWindows() ||
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var architectures = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? new[] { "arm64", "x64" } : ["x64"];
            foreach (var directory in directories.Where(path => File.Exists(Path.Combine(path, "codex.cmd"))))
            {
                var modules = Path.Combine(directory, "node_modules", "@openai");
                foreach (var suffix in architectures)
                {
                    var triple = suffix == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
                    // shortcut: 找标准 npm 全局布局，包布局变化时更新探测路径。
                    foreach (var packages in new[] { Path.Combine(modules, "codex", "node_modules", "@openai"), modules })
                    {
                        var candidate = Path.Combine(packages, $"codex-win32-{suffix}", "vendor", triple, "bin", name);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
        }

        throw new InvalidOperationException("PATH 中找不到 Codex CLI。请安装 Codex、使用 ChatGPT 登录，并配置 Codex 可执行文件路径。");
    }

    public async Task<JsonElement> ReadUsageAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureProcessAsync(ct).ConfigureAwait(false);
            return await RequestAsync("account/rateLimits/read", new { }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    public Task<(JsonElement Output, JsonElement? Usage)> CompleteJsonAsync(
        string instructions, string input, JsonElement schema, CancellationToken ct = default) =>
        CompleteJsonAsync(instructions, [new { type = "text", text = input }], [], schema, ct);

    internal async Task<(JsonElement Output, JsonElement? Usage)> CompleteJsonAsync(
        string instructions, IReadOnlyList<object> input, IReadOnlyList<object> history, JsonElement schema, CancellationToken ct,
        bool strictModel = false, Action<string?, bool>? observeFinalText = null, TimeSpan? batchTimeout = null,
        Action<JsonElement>? observeUsage = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(batchTimeout ?? TimeSpan.FromSeconds(80));
        ct = deadline.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? threadId = null;
        string? turnId = null;
        string? finalText = null;
        JsonElement? usage = null;
        bool started = false, completed = false, explicitFinal = false;

        void ObserveTokenUsage(JsonElement message)
        {
            if (turnId is null || Text(message, "method") != "thread/tokenUsage/updated") return;
            var data = Field(message, "params");
            if (Text(data, "threadId") != threadId || Text(data, "turnId") != turnId) return;
            var total = Field(Field(data, "tokenUsage"), "total");
            if (total.ValueKind != JsonValueKind.Object) return;
            usage = total;
            observeUsage?.Invoke(total);
        }

        try
        {
            await EnsureProcessAsync(ct).ConfigureAwait(false);
            var threadResult = await RequestAsync("thread/start", new
            {
                model = Model, modelProvider = "openai", cwd = workingDirectory.FullName,
                ephemeral = true, baseInstructions = instructions, developerInstructions = "",
                sandbox = "read-only", approvalPolicy = "never", environments = Array.Empty<object>()
            }, ct).ConfigureAwait(false);
            var thread = Field(threadResult, "thread");
            threadId = RequiredText(thread, "id");
            var sandbox = Field(threadResult, "sandbox");
            if (Field(thread, "ephemeral").ValueKind != JsonValueKind.True ||
                Text(threadResult, "modelProvider") != "openai" || Text(threadResult, "model") != Model ||
                Text(threadResult, "approvalPolicy") != "never" || Text(sandbox, "type") != "readOnly" ||
                Field(sandbox, "networkAccess").ValueKind != JsonValueKind.False)
                throw new InvalidOperationException("Codex 线程未应用所需的隔离设置。");

            if (history.Count != 0)
                await RequestAsync("thread/inject_items", new { threadId, items = history }, ct).ConfigureAwait(false);

            // Notifications can precede the turn/start response on the same connection.
            var early = new List<JsonElement>();
            void Observe(JsonElement message)
            {
                string? method = Text(message, "method");
                var data = Field(message, "params");
                if (Text(data, "threadId") != threadId) return;
                string? eventTurn = method == "turn/completed" ? Text(Field(data, "turn"), "id") : Text(data, "turnId");
                if (eventTurn is null || turnId is not null && eventTurn != turnId) return;
                if (strictModel && turnId is not null)
                {
                    if (method == "model/rerouted" && Text(data, "toModel") != Model)
                        throw new DecisionProtocolException(502, "model_rerouted", "Codex 将轮次切换到了其他模型。");
                    var error = method switch
                    {
                        "turn/completed" => Field(Field(data, "turn"), "error"),
                        "error" when Field(data, "willRetry").ValueKind != JsonValueKind.True => Field(data, "error"),
                        _ => default,
                    };
                    var info = Field(error, "codexErrorInfo");
                    if (info.ValueKind == JsonValueKind.String &&
                        info.GetString() is "usageLimitExceeded" or "rateLimitExceeded")
                        throw new DecisionProtocolException(429, "rate_limit", "Codex 报告额度或速率限制。");
                }
                if (method is "item/started" or "item/completed")
                {
                    var item = Field(data, "item");
                    var itemType = Text(item, "type");
                    if (itemType is not ("userMessage" or "agentMessage" or "reasoning" or "contextCompaction"))
                        throw new InvalidOperationException("Codex 请求了知识库模式不允许的操作。");
                    if (method != "item/completed" || itemType != "agentMessage" ||
                        Text(item, "phase") is not (null or "final_answer")) return;
                }
                else if (method is not ("turn/completed" or "thread/tokenUsage/updated") &&
                    !(strictModel && method is "model/rerouted" or "error")) return;

                if (turnId is null)
                {
                    if (early.Count >= 64) throw new InvalidOperationException("Codex 返回过多未匹配的轮次事件。");
                    early.Add(message);
                    return;
                }
                switch (method)
                {
                    case "item/completed":
                        var item = Field(data, "item");
                        var isFinal = Text(item, "phase") == "final_answer";
                        if (isFinal || !explicitFinal)
                        {
                            finalText = RequiredText(item, "text");
                            explicitFinal = isFinal;
                        }
                        break;
                    case "thread/tokenUsage/updated":
                        ObserveTokenUsage(message);
                        break;
                    case "turn/completed":
                        completed = true;
                        if (Text(Field(data, "turn"), "status") != "completed")
                            throw new InvalidOperationException("Codex 轮次未正常完成，请检查登录和套餐额度。");
                        break;
                }
            }

            started = true;
            var turn = Field(await RequestAsync("turn/start", new
            {
                threadId, input, outputSchema = schema, effort,
                sandboxPolicy = new { type = "readOnly", networkAccess = false }, approvalPolicy = "never"
            }, ct, Observe).ConfigureAwait(false), "turn");
            turnId = RequiredText(turn, "id");
            var nextEvent = 0;
            try
            {
                while (nextEvent < early.Count) Observe(early[nextEvent++]);
            }
            catch
            {
                // The remaining notifications have already been read from the stream.
                for (; nextEvent < early.Count; nextEvent++) ObserveTokenUsage(early[nextEvent]);
                throw;
            }
            while (!completed) Observe(await ReadMessageAsync(ct).ConfigureAwait(false));
            JsonElement result;
            var parsed = false;
            try
            {
                if (string.IsNullOrWhiteSpace(finalText)) throw new InvalidOperationException("Codex 未返回可用的结构化结果。");
                result = Parse(finalText);
                parsed = true;
            }
            finally { observeFinalText?.Invoke(finalText, parsed); }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await RequestAsync("thread/unsubscribe", new { threadId }, cleanup.Token, Observe).ConfigureAwait(false);
            return (result, usage);
        }
        catch
        {
            // If start was sent without receiving its ID, terminating this child is the only bounded cancellation.
            if (process is { HasExited: false } && threadId is not null && (turnId is not null || !started))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try
                {
                    if (!completed && turnId is not null)
                        await RequestAsync("turn/interrupt", new { threadId, turnId }, cleanup.Token, ObserveTokenUsage).ConfigureAwait(false);
                    await RequestAsync("thread/unsubscribe", new { threadId }, cleanup.Token, ObserveTokenUsage).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
            }
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task EnsureProcessAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process is { HasExited: false }) return;
        await StopProcessAsync(graceful: false).ConfigureAwait(false);
        await StartProcessAsync(ct).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(25));
        var token = limit.Token;
        try
        {
            var config = await InitializeAsync([], token).ConfigureAwait(false);
            var inherited = EnabledMcp(config);
            if (inherited.Length != 0)
            {
                if (inherited.Any(name => name.Length == 0 || name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-'))))
                    throw new InvalidOperationException("无法安全禁用所有继承的 Codex MCP 服务。");
                await StopProcessAsync(graceful: true).ConfigureAwait(false);
                config = await InitializeAsync(inherited, token).ConfigureAwait(false);
            }
            if (EnabledMcp(config).Length != 0 || Text(config, "model_provider") != "openai" ||
                Text(config, "chatgpt_base_url") != ChatGptUrl || Text(config, "openai_base_url") != "" ||
                Text(config, "web_search") != "disabled" || Field(config, "project_doc_max_bytes") is not { ValueKind: JsonValueKind.Number } maxBytes ||
                !maxBytes.TryGetInt32(out var docBytes) ||
                docBytes != 0 || DisabledInstructions.Any(key => Field(config, key).ValueKind != JsonValueKind.False) ||
                DisabledFeatures.Any(key => Field(Field(config, "features"), key).ValueKind != JsonValueKind.False) ||
                Field(config, "notify") is not { ValueKind: JsonValueKind.Array } notify || notify.GetArrayLength() != 0)
                throw new InvalidOperationException("无法确认 Codex 工具隔离配置，知识库模式已停止。");

            var account = Field(await RequestAsync("account/read", new { refreshToken = false }, token).ConfigureAwait(false), "account");
            if (Text(account, "type") != "chatgpt")
                throw new InvalidOperationException("Codex 需要当前用户的 ChatGPT 登录，请检查 codex login status。");
            Plan = Text(account, "planType") ?? "unknown";
            var models = Field(await RequestAsync("model/list", new { includeHidden = false }, token).ConfigureAwait(false), "data");
            if (models.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Codex 未返回可用模型。");
            var available = models.EnumerateArray().Where(item => Field(item, "hidden").ValueKind != JsonValueKind.True && Text(item, "model") is not null).ToArray();
            var model = string.IsNullOrEmpty(Model)
                ? available.FirstOrDefault(item => Field(item, "isDefault").ValueKind == JsonValueKind.True)
                : available.FirstOrDefault(item => Text(item, "model") == Model);
            if (model.ValueKind == JsonValueKind.Undefined && string.IsNullOrEmpty(Model)) model = available.FirstOrDefault();
            if (model.ValueKind == JsonValueKind.Undefined && !string.IsNullOrEmpty(Model))
                throw new InvalidOperationException("指定的 Codex 模型在当前 ChatGPT 账号不可用。请检查模型设置后重新启动。");
            Model = Text(model, "model") ?? throw new InvalidOperationException("Codex 未返回可用模型。");
            var modalities = Field(model, "inputModalities");
            SupportsImages = modalities.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                modalities.ValueKind == JsonValueKind.Array &&
                modalities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
            var efforts = Field(model, "supportedReasoningEfforts");
            var preferredEffort = requestedEffort ?? "low";
            effort = efforts.ValueKind == JsonValueKind.Array &&
                efforts.EnumerateArray().Any(item => Text(item, "reasoningEffort") == preferredEffort) ? preferredEffort : null;
            if (requestedEffort is not null && effort is null)
                throw new InvalidOperationException($"Codex 模型 {Model} 不支持推理强度 {requestedEffort}。");
        }
        catch
        {
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JsonElement> InitializeAsync(string[] inheritedMcp, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory.FullName, CreateNoWindow = true
        };
        foreach (var arg in new[] { "app-server", "--listen", "stdio://" }) start.ArgumentList.Add(arg);
        foreach (var feature in DisabledFeatures)
        {
            start.ArgumentList.Add("--disable");
            start.ArgumentList.Add(feature);
        }
        foreach (var value in new[]
        {
            // The managed ChatGPT endpoint is selected only without an explicit provider URL.
            "model_provider=\"openai\"", $"chatgpt_base_url=\"{ChatGptUrl}\"", "openai_base_url=\"\"",
            "web_search=\"disabled\"", "project_doc_max_bytes=0", "notify=[]"
        }.Concat(DisabledInstructions.Select(key => $"{key}=false")).Concat(inheritedMcp.Select(key => $"mcp_servers.{key}.enabled=false")))
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(value);
        }
        foreach (var name in new[] { "OPENAI_API_KEY", "DEEPSEEK_API_KEY", "SAG_SYSTEMONE_BEARER", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL", "CODEX_API_BASE_URL", "OPENAI_ORGANIZATION", "OPENAI_PROJECT" })
            start.Environment.Remove(name);
        foreach (var name in privateEnvironment)
            start.Environment.Remove(name);
        start.Environment["RUST_LOG"] = "off";
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法启动 Codex App Server。"); }
        if (process is null) throw new InvalidOperationException("无法启动 Codex App Server。");
        stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "sagwiki-codex", title = "SagWiki.Codex", version = "1.0.0" },
            capabilities = new { experimentalApi = true, requestAttestation = false }
        }, ct).ConfigureAwait(false);
        await WriteAsync(new { method = "initialized" }, ct).ConfigureAwait(false);
        return Field(await RequestAsync("config/read", new { includeLayers = false, cwd = workingDirectory.FullName }, ct).ConfigureAwait(false), "config");
    }

    private static string[] EnabledMcp(JsonElement config)
    {
        var servers = Field(config, "mcp_servers");
        if (servers.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new InvalidOperationException("无法读取 Codex 的 MCP 配置。");
        return servers.ValueKind == JsonValueKind.Null ? [] : servers.EnumerateObject()
            .Where(entry => Field(entry.Value, "enabled").ValueKind != JsonValueKind.False)
            .Select(entry => entry.Name).ToArray();
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, Action<JsonElement>? observe = null)
    {
        int id = ++nextId;
        await WriteAsync(new { id, method, @params = parameters }, ct).ConfigureAwait(false);
        while (true)
        {
            var message = await ReadMessageAsync(ct).ConfigureAwait(false);
            var messageId = Field(message, "id");
            if (messageId.ValueKind == JsonValueKind.Number && messageId.TryGetInt32(out int actual) && actual == id)
            {
                var result = Field(message, "result");
                if (result.ValueKind != JsonValueKind.Undefined) return result;
                throw new InvalidOperationException($"Codex App Server 请求失败（{method}）。请检查当前登录或套餐额度。");
            }
            observe?.Invoke(message);
        }
    }

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        try
        {
            var writer = process!.StandardInput;
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) { throw new InvalidOperationException("Codex App Server 输入已关闭。"); }
    }

    private async Task<JsonElement> ReadMessageAsync(CancellationToken ct)
    {
        string? line;
        try { line = await process!.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false); }
        catch (IOException) { throw new InvalidOperationException("Codex App Server 输出已关闭。"); }
        if (line is null) throw new InvalidOperationException("Codex App Server 已退出。");
        var message = Parse(line);
        if (Field(message, "method").ValueKind == JsonValueKind.String && Field(message, "id").ValueKind != JsonValueKind.Undefined)
        {
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
            throw new InvalidOperationException("Codex 请求了知识库模式不允许的操作。");
        }
        if (Text(message, "method") == "account/updated" && Text(Field(message, "params"), "authMode") != "chatgpt")
        {
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
            throw new InvalidOperationException("Codex 的 ChatGPT 登录已不可用。");
        }
        return message;
    }

    private static JsonElement Parse(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
        }
        catch (JsonException) { }
        throw new InvalidOperationException("Codex 返回了无效的 JSON 对象。");
    }

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element, string name) =>
        Field(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string RequiredText(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Codex App Server 返回缺少标识。");

    private async Task StopProcessAsync(bool graceful)
    {
        var child = process;
        var drain = stderr;
        process = null;
        stderr = null;
        if (child is null) return;
        using (child)
        {
            if (graceful && !child.HasExited)
            {
                try { child.StandardInput.Close(); }
                catch (IOException) { }
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await child.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await child.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (drain is not null)
            {
                try { await drain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            try { workingDirectory.Delete(); }
            catch (IOException) { }
        }
        finally { gate.Release(); }
    }
}

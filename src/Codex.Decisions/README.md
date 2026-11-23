# Decisions.Codex

`Decisions.Codex` 在 .NET 11 上通过本机 Codex App Server 提供 OpenAI Decisions 和 Jev System One 协议适配以及结构化回答。C# 命名空间为 `Codex.Decisions`。安装 Codex CLI 并登录后，可从 `PATH` 启动它，也可指定可执行文件路径。

```sh
dotnet add package Decisions.Codex --version 1.1.0
```

```csharp
using System.Text.Json;
using Codex.Decisions;

await using var codex = await CodexAppServer.StartAsync(executable: null);
using var request = JsonDocument.Parse($$"""
    {
      "model": {{JsonSerializer.Serialize(codex.Model)}},
      "state": "会议改到周二",
      "questions": {
        "scheduled": {"type": "noul", "instructions": "是否给出了具体日期？"}
      }
    }
    """);
var response = await codex.CreateJevSystemOneAsync(
    request.RootElement, enableImages: false, enableLongContext: false);
Console.WriteLine(response);
```

OpenAI Decisions 请求使用 `CreateOpenAiDecisionAsync`。两个 HTTP 协议的本地部署和调用示例见 [DecisionsDemo](https://github.com/yuezhishun/decisions-sdk/tree/main/samples/DecisionsDemo)。

从 `1.0.1` 起，可在启动会话时同时指定模型和思考级别。

```csharp
await using var codex = await CodexAppServer.StartAsync(
    executable: null,
    model: "gpt-6.2-sol",
    privateEnvironment: [],
    reasoningEffort: "low",
    ct: CancellationToken.None);
```

SDK 会在派发请求前校验模型是否可用以及模型是否支持指定档位。传入 `null` 或使用原有重载时，模型支持 `low` 就使用 `low`，否则由 Codex App Server 决定默认档位。

从 `1.1.0` 起，`CompleteJsonAsync(instructions, input, schema, onDelta, ct)` 会在收到当前轮次标记为 `final_answer` 的消息文本时，按顺序将原始 JSON 片段传给 `onDelta`。每个片段可能包含不完整的 JSON。App Server 未提供最终回答阶段时，仍会返回解析后的完整结果，回调可能收不到片段。调用方可用最终结果更新预览。

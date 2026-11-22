# Decisions.Codex

`Decisions.Codex` 在 .NET 11 上通过本机 Codex App Server 提供 OpenAI Decisions 和 Jev System One 协议适配以及结构化回答。C# 命名空间为 `Codex.Decisions`。安装 Codex CLI 并登录后，可从 `PATH` 启动它，也可指定可执行文件路径。

```sh
dotnet add package Decisions.Codex --version 1.0.0
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

# 双后端决策协议 Demo

这个独立示例用本机 Codex 或 DeepSeek 提供 OpenAI Decisions 和 Jev System One。每个 URL 前缀绑定一个后端，`model` 必须匹配该后端实际选用的模型。

## 启动

在 Decisions SDK 仓库根目录使用 .NET 11 SDK 运行以下 PowerShell 命令。Demo 直接引用仓库中的两个项目。用已配置的 `DEEPSEEK_API_KEY` 启用 DeepSeek，并通过本机 Codex 登录启用 Codex。模型列表需要各自的模型发布日期，格式为 `yyyy-MM-dd`。

```powershell
$env:DECISIONS_DEMO_API_KEY = 'local-demo-key'
$env:DecisionsDemo__DeepSeekModel = 'deepseek-flash'
# 可选。省略时使用本机 Codex 默认模型与 PATH 中的 CLI。
$env:DecisionsDemo__CodexModel = 'gpt-6-luna'
# 可选。档位须由所选 Codex 模型支持。
# $env:DecisionsDemo__CodexReasoningEffort = 'low'
# GET /models 需要填写所配置模型的实际发布日期。
# $env:DecisionsDemo__DeepSeekReleaseDate = 'yyyy-MM-dd'
# $env:DecisionsDemo__CodexReleaseDate = 'yyyy-MM-dd'
dotnet run --project samples/DecisionsDemo -c Release
```

默认只监听 `127.0.0.1:3090`，可通过 `DecisionsDemo__Port` 改端口。可设置 `DecisionsDemo__CodexExecutable` 指向 Codex CLI，或通过 `DecisionsDemo__DeepSeekEndpoint` 指向受控测试服务。

省略 `DecisionsDemo__CodexReasoningEffort` 时，模型支持 `low` 就使用 `low`，否则由 Codex App Server 决定默认档位。填写模型不支持的档位会在首次使用 Codex 后端时返回错误。

| 后端 | Base URL |
| --- | --- |
| Codex | `http://127.0.0.1:3090/codex/v1/` |
| DeepSeek | `http://127.0.0.1:3090/deepseek/v1/` |

每个 Base URL 提供 `POST /decisions`、`POST /systemone` 及 Jev `GET /models`。均使用 `Authorization: Bearer <DECISIONS_DEMO_API_KEY>`。原始请求正文上限为 OpenAI 48 MiB、Jev 32 MiB，Jev 解压后的请求同样限 32 MiB。

## 调用 OpenAI Decisions

下面的 C# 示例使用官方 `OpenAI` NuGet 2.15.0。把 Base URL 和模型一起替换成另一后端即可切换。

```csharp
using System.ClientModel;
using OpenAI.Decisions;

#pragma warning disable OPENAI001
var client = new DecisionsClient(
    new ApiKeyCredential(Environment.GetEnvironmentVariable("DECISIONS_DEMO_API_KEY")!),
    new DecisionsClientOptions { Endpoint = new Uri("http://127.0.0.1:3090/codex/v1/") });
var options = new DecisionCreationOptions(
    [new DecisionsUserMessageItem([new DecisionsInputTextContentPart("The meeting is scheduled for Tuesday.")])],
    "gpt-6-luna",
    [new DecisionsPredicateQuestion([new DecisionsInputTextInstructionsPart("Is a day specified?")]) { Name = "scheduled" }]);
var result = await client.CreateDecisionAsync(options);
Console.WriteLine(result.Value.Answers.Count);
```

## 调用 Jev System One

```powershell
$base = 'http://127.0.0.1:3090/deepseek/v1'
$headers = @{ Authorization = "Bearer $env:DECISIONS_DEMO_API_KEY" }
Invoke-RestMethod "$base/systemone" -Method Post -Headers $headers -ContentType 'application/json' -Body @'
{"model":"deepseek-flash","state":"会议改到周二","questions":{"scheduled":{"type":"noul","instructions":"是否给出了具体日期？"}}}
'@
```

图片场景可使用多部件请求。准备一张宽高各为 280–1568 像素的 PNG，保存为 `photo.png`，将以下请求保存为无 BOM 的 UTF-8 文件 `request.json`。

```json
{
  "model": "deepseek-flash",
  "state": {"photo": "image-1"},
  "assets": {"image-1": {"type": "image", "part": "photo"}},
  "questions": {"object": {"type": "box", "instructions": "Locate the main object in the photo."}}
}
```

```powershell
curl.exe "$base/systemone" -H "Authorization: Bearer $env:DECISIONS_DEMO_API_KEY" `
  -H 'TypeSafe-Danger-Zone-Images: true' `
  -F 'request=@request.json;type=application/json' -F 'photo=@photo.png;type=image/png'
```

Jev 支持 `gzip`、`x-gzip` 和 `zstd` 请求压缩。图片和 Box 取决于模型的视觉能力，定位结果由所选模型给出。OpenAI 的历史消息和工具记录按原顺序作为评估数据处理，`system` 和 `developer` 角色以标签保留。显式缓存字段和安全标识等不可兑现的选项会收到能力错误。 [OpenAI Decisions](https://developers.openai.com/api/reference/resources/decisions/methods/create) · [Jev System One](https://docs.typesafe.ai/api/system-one.md)

## 离线验收

在仓库根目录构建 Demo、受控模型和 SDK 探针，再用 Python 3 运行验证脚本。脚本会启动隔离的回环服务，并用官方 OpenAI SDK 分别访问两个 Base URL。

```powershell
dotnet build samples/DecisionsDemo -c Release
dotnet build scripts/codex-fixture -c Release
dotnet build scripts/decisions-demo-sdk -c Release
python scripts/test-decisions-demo.py samples/DecisionsDemo/bin/Release/net11.0/DecisionsDemo.dll scripts/codex-fixture/bin/Release/net11.0/CodexFixture.exe scripts/decisions-demo-sdk/bin/Release/net11.0/DecisionsDemoSdk.dll
```

Linux 和 macOS 的 fixture 可执行文件名为 `CodexFixture`。测试以两份固定合同为基线。 [OpenAI OpenAPI 提交](https://github.com/openai/openai-openapi/blob/2a24deae6f89f84d0e88ce3c9d96c00304dca0b8/openapi.json) · [TypeSafe OpenAPI 0.2.0](https://api.typesafe.ai/openapi.json)

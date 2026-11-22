# Decisions SDK

两个 .NET 决策库支持 OpenAI Decisions 和 Jev System One 请求格式。

| 包 | 目标框架 | 后端 |
| --- | --- | --- |
| [Decisions.Codex](src/Codex.Decisions/README.md) | .NET 11 | 本机 Codex App Server |
| [DeepSeek.Decisions](src/DeepSeek.Decisions/README.md) | .NET Standard 2.0 | 通过 [DeepSeek.Core](https://github.com/yuezhishun/deepseek-sdk) 调用 DeepSeek Chat Completions |

[English](README.md) · [双协议示例](samples/DecisionsDemo/README.md) · [DeepSeek WorkflowEvals 评测](benchmark/DeepSeek.Decisions.Benchmarks/README.md) · [Codex WorkflowEvals 回放](benchmark/Codex.Decisions.Benchmarks/README.md)

## 安装

```shell
dotnet add package Decisions.Codex --version 1.0.1
dotnet add package DeepSeek.Decisions --version 1.2.1
```

以 NuGet 上的 [Decisions.Codex](https://www.nuget.org/packages/Decisions.Codex) 和 [DeepSeek.Decisions](https://www.nuget.org/packages/DeepSeek.Decisions) 为准。发布前可以从本仓库直接构建。

## 构建与测试

安装 .NET 11 SDK 及 .NET 8、.NET 10 运行时。安装 Python 3 后，可运行使用模拟后端的双协议回归。以下命令无需模型凭据。

```shell
dotnet build Decisions.slnx -c Release
dotnet test Decisions.slnx -c Release --no-build --filter "Category!=Live"
python scripts/test-decisions-demo.py samples/DecisionsDemo/bin/Release/net11.0/DecisionsDemo.dll <fixture> scripts/decisions-demo-sdk/bin/Release/net11.0/DecisionsDemoSdk.dll
```

将 `<fixture>` 替换为 `scripts/codex-fixture/bin/Release/net11.0/CodexFixture`，Windows 上加 `.exe`。

DeepSeek 在线集成测试单独执行。配置 `DEEPSEEK_API_KEY` 或 `deepseek-api-key` 后运行以下命令，会调用 DeepSeek API。

```shell
dotnet test test/DeepSeek.Decisions.IntegrationTests -c Release
```

## 发布

两个包独立升版和发布。更新目标包项目及 README 版本，从目标提交手动运行 [Publish NuGet](https://github.com/yuezhishun/decisions-sdk/actions/workflows/publish-nuget.yml) 并选择包。发布 DeepSeek 包需配置 GitHub Actions Secret `DEEPSEEK_API_KEY`，用于在线回归。

在 NuGet 配置 `yuezhishun/decisions-sdk` 的 `publish-nuget.yml` 为 Trusted Publisher，并授权目标包。首次发布 `Decisions.Codex` 需授权新包。手动验证通过后创建并推送对应的注释标签。NuGet 确认可下载后，再创建带中英文说明的 GitHub Release，标题用版本号。

```shell
git tag -a Decisions.Codex/v1.0.1 -m "Decisions.Codex 1.0.1"
git push origin Decisions.Codex/v1.0.1

git tag -a DeepSeek.Decisions/v1.2.1 -m "DeepSeek.Decisions 1.2.1"
git push origin DeepSeek.Decisions/v1.2.1
```
